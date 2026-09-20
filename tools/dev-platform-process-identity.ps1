# Process identity helpers shared by dev-platform.ps1 and its tests.
#
# Ownership of a host process is proven by its PID plus a platform-specific
# start identity that survives PID reuse:
#   Windows: process StartTime (wall clock, UTC, 1 s tolerance).
#   Linux:   /proc/<pid>/stat field 22 (starttime, clock ticks since boot).
# The Linux identity is boot-relative, so stepping or resynchronising the
# guest wall clock (observed on WSL2) can no longer turn an owned process
# into an "unowned" one.

function ConvertFrom-ProcStat([string] $StatText) {
    # /proc/<pid>/stat is "<pid> (<comm>) <field3> <field4> ...". The comm
    # value may contain spaces and parentheses, so fields are located from
    # the last ')' rather than by splitting the whole line on whitespace.
    if ([string]::IsNullOrWhiteSpace($StatText)) { return $null }
    $open = $StatText.IndexOf('(')
    $close = $StatText.LastIndexOf(')')
    if ($open -lt 1 -or $close -le $open) { return $null }
    $statPid = 0
    if (-not [int]::TryParse($StatText.Substring(0, $open).Trim(), [ref]$statPid)) { return $null }
    $comm = $StatText.Substring($open + 1, $close - $open - 1)
    $fields = @($StatText.Substring($close + 1).Trim() -split '\s+')
    # $fields[0] is stat field 3 (state); stat field N is $fields[N - 3].
    if ($fields.Count -lt 20) { return $null }
    $startTimeTicks = [long]0
    if (-not [long]::TryParse($fields[19], [ref]$startTimeTicks)) { return $null }
    return [pscustomobject]@{
        pid = $statPid
        comm = $comm
        state = $fields[0]
        startTimeTicks = $startTimeTicks
    }
}

function Get-LinuxProcessStartTimeTicks([int] $ProcessId, [string] $ProcRoot = "/proc") {
    # Returns $null whenever a reliable boot-relative start time cannot be read.
    try {
        $statPath = "$ProcRoot/$ProcessId/stat"
        if (-not (Test-Path -LiteralPath $statPath -PathType Leaf)) { return $null }
        $parsed = ConvertFrom-ProcStat ([System.IO.File]::ReadAllText($statPath))
        if ($null -eq $parsed -or $parsed.pid -ne $ProcessId) { return $null }
        return $parsed.startTimeTicks
    }
    catch { return $null }
}

function Get-ProcessIdentityRecord($Process, [switch] $RunningOnWindows, [string] $ProcRoot = "/proc") {
    if ($RunningOnWindows) {
        return [ordered]@{ startTimeUtc = $Process.StartTime.ToUniversalTime().ToString("O") }
    }
    $startTimeTicks = Get-LinuxProcessStartTimeTicks -ProcessId $Process.Id -ProcRoot $ProcRoot
    if ($null -eq $startTimeTicks) {
        throw "Unable to read the boot-relative start time of process $($Process.Id) from $ProcRoot."
    }
    return [ordered]@{ startTimeTicks = $startTimeTicks }
}

function Test-ProcessIdentityMatch($Record, $Process, [switch] $RunningOnWindows, [string] $ProcRoot = "/proc") {
    # Fails closed: any missing, legacy or unreadable identity means "not owned".
    if ($null -eq $Record -or $null -eq $Process) { return $false }
    try {
        if ($RunningOnWindows) {
            if ($null -eq $Record.psobject.Properties["startTimeUtc"]) { return $false }
            $recordedStart = [DateTimeOffset]::Parse(
                $Record.startTimeUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind)
            $actualStart = [DateTimeOffset]$Process.StartTime.ToUniversalTime()
            return [Math]::Abs(($actualStart - $recordedStart).TotalSeconds) -lt 1
        }
        if ($null -eq $Record.psobject.Properties["startTimeTicks"]) { return $false }
        $recordedTicks = [long]0
        if (-not [long]::TryParse([string]$Record.startTimeTicks, [ref]$recordedTicks)) { return $false }
        $actualTicks = Get-LinuxProcessStartTimeTicks -ProcessId $Process.Id -ProcRoot $ProcRoot
        return ($null -ne $actualTicks) -and ($actualTicks -eq $recordedTicks)
    }
    catch { return $false }
}
