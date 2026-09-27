[CmdletBinding()]
param(
    [switch] $Full,
    [switch] $CI,
    [string] $EnvironmentFile,
    [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$platform = Join-Path $PSScriptRoot "dev-platform.ps1"
$processIdentity = Join-Path $PSScriptRoot "dev-platform-process-identity.ps1"
$seedProject = Join-Path $PSScriptRoot "Paqueteria.DevSeed/Paqueteria.DevSeed.csproj"
$portalPolicy = Join-Path $repositoryRoot "apps/web/src/dev/dev-portal-policy.ts"
$portalPage = Join-Path $repositoryRoot "apps/web/src/app/dev/page.dev.tsx"
$dotnet = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) ".dotnet/dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }
$shellCommand = if ($null -ne (Get-Command pwsh -ErrorAction SilentlyContinue)) {
    (Get-Command pwsh).Source
} else {
    Join-Path $PSHOME "powershell.exe"
}

function Assert-Contains([string] $Path, [string] $Text) {
    if (-not (Select-String -LiteralPath $Path -SimpleMatch $Text -Quiet)) {
        throw "$Path does not contain required guard: $Text"
    }
}

function Invoke-Platform([string] $Operation, [string[]] $Extra = @()) {
    $arguments = @($platform, $Operation, "-TimeoutSeconds", $TimeoutSeconds)
    if (-not [string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        $arguments += @("-EnvironmentFile", $EnvironmentFile)
    }
    $arguments += $Extra
    & $shellCommand @arguments
    if ($LASTEXITCODE -ne 0) { throw "dev-platform $Operation failed." }
}

function Assert-ManualEndpoints {
    $api = "http://127.0.0.1:5080"
    $organization = "11111111-1111-1111-1111-111111111111"
    $dispatcherHeaders = @{ Authorization="Bearer local-dispatcher-mfa"; "X-Organization-Id"=$organization }
    $driverHeaders = @{ Authorization="Bearer active-driver"; "X-Organization-Id"=$organization }
    Write-Host "Checking Operations REST..."
    $operations = Invoke-WebRequest "$api/api/v1/operations/dashboard" -Headers $dispatcherHeaders -UseBasicParsing -TimeoutSec 30
    if ($operations.StatusCode -ne 200) { throw "Operations REST is not ready." }
    Write-Host "Checking Driver REST..."
    $driver = Invoke-WebRequest "$api/api/v1/driver/me/stops" -Headers $driverHeaders -UseBasicParsing -TimeoutSec 30
    if ($driver.StatusCode -ne 200) { throw "Driver REST is not ready." }
    $access = Get-Content (Join-Path $repositoryRoot ".local/access.json") -Raw | ConvertFrom-Json
    Write-Host "Checking public tracking page..."
    $tracking = Invoke-WebRequest $access.samples.trackingUrl -UseBasicParsing -TimeoutSec 30
    if ($tracking.StatusCode -ne 200) { throw "Public tracking sample is not ready." }
    try {
        $decoyHeaders = $dispatcherHeaders.Clone()
        $decoyHeaders["X-Organization-Id"] = "33333333-3333-3333-3333-333333333333"
        Write-Host "Checking decoy tenant rejection..."
        Invoke-WebRequest "$api/api/v1/operations/dashboard" -Headers $decoyHeaders -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop | Out-Null
        throw "Decoy tenant was unexpectedly visible."
    }
    catch {
        if ($_.Exception.Message -eq "Decoy tenant was unexpectedly visible.") { throw }
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -notin @(403,404)) { throw "Decoy tenant rejection returned unexpected status $status." }
    }
}

$csproj = Get-Content (Join-Path $PSScriptRoot "Paqueteria.DevSeed/Paqueteria.DevSeed.csproj") -Raw
if ($csproj -match "PackageReference") {
    throw "DevSeed must remain ProjectReference-only."
}
Assert-Contains $portalPolicy 'nodeEnvironment === "development"'
Assert-Contains $portalPolicy 'deploymentClass === "DEV_SYNTHETIC"'
Assert-Contains $portalPolicy 'explicitOptIn === "true"'
Assert-Contains $portalPage "notFound()"
Assert-Contains $platform 'Authentication__Provider = "Mock"'
Assert-Contains $platform 'IdentityBootstrap__Provider = "PostgreSql"'
Assert-Contains $platform 'PAQUETERIA_LOCAL_DEV_SEED_ENABLED = "true"'
Assert-Contains $platform '$applicationDatabaseSuffix = "_dev_platform"'
Assert-Contains $platform '"createdb", "--username"'
Assert-Contains $platform '@("MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER")'
Assert-Contains $platform 'function Get-ListeningProcessId'
Assert-Contains $platform 'Set-WebListenerOwnership'
Assert-Contains $platform 'Stop-OwnedProcessRecord $record.supervisor'
Assert-Contains $platform 'dev-platform-process-identity.ps1'
Assert-Contains $platform 'Test-ProcessIdentityMatch -Record $Record -Process $Process -RunningOnWindows:$runningOnWindows'

$oldEnvironment = $env:DOTNET_ENVIRONMENT
$oldOptIn = $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED
$oldDeploymentClass = $env:PAQUETERIA_DEPLOYMENT_CLASS
$oldSyntheticSeedOptIn = $env:PAQUETERIA_SYNTHETIC_SEED_ENABLED
try {
    $oldErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $env:DOTNET_ENVIRONMENT = "Production"
    $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = "true"
    & $dotnet run --no-restore --project $seedProject -- status *> $null
    if ($LASTEXITCODE -eq 0) { throw "DevSeed accepted Production." }
    & $dotnet run --no-restore --project $seedProject -- tracking 77777777-7777-7777-7777-777777777777 *> $null
    if ($LASTEXITCODE -eq 0) { throw "DevSeed tracking accepted Production." }
    $env:DOTNET_ENVIRONMENT = "Development"
    $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = "false"
    & $dotnet run --no-restore --project $seedProject -- status *> $null
    if ($LASTEXITCODE -eq 0) { throw "DevSeed accepted a missing explicit opt-in." }
    $env:DOTNET_ENVIRONMENT = "DevSynthetic"
    $env:PAQUETERIA_DEPLOYMENT_CLASS = "DEV_SYNTHETIC"
    $env:PAQUETERIA_SYNTHETIC_SEED_ENABLED = "true"
    foreach ($forbiddenCommand in @("bootstrap", "status")) {
        & $dotnet run --no-restore --project $seedProject -- $forbiddenCommand *> $null
        if ($LASTEXITCODE -eq 0) { throw "DevSeed accepted $forbiddenCommand in DevSynthetic." }
    }
    & $dotnet run --no-restore --project $seedProject -- tracking 77777777-7777-7777-7777-777777777777 *> $null
    if ($LASTEXITCODE -eq 0) { throw "DevSeed accepted tracking in DevSynthetic." }
}
finally {
    $ErrorActionPreference = $oldErrorActionPreference
    $env:DOTNET_ENVIRONMENT = $oldEnvironment
    $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = $oldOptIn
    $env:PAQUETERIA_DEPLOYMENT_CLASS = $oldDeploymentClass
    $env:PAQUETERIA_SYNTHETIC_SEED_ENABLED = $oldSyntheticSeedOptIn
}

$newFiles = @(
    $platform,
    $processIdentity,
    $seedProject,
    (Join-Path $PSScriptRoot "Paqueteria.DevSeed/Program.cs"),
    $portalPolicy,
    $portalPage,
    (Join-Path $repositoryRoot "apps/web/src/app/dev/portal.tsx")
)
$forbidden = '(?i)@[a-z0-9.-]+\.[a-z]{2,}|\b[A-Z]{4}\d{6}[A-Z0-9]{8}\b'
foreach ($file in $newFiles) {
    if ((Get-Content $file -Raw) -match $forbidden) {
        throw "Possible real PII found in $file."
    }
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Process ownership regression: $Message" }
}

function Test-ProcessOwnershipIdentity {
    . $processIdentity
    $procRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dev-platform-proc-" + [Guid]::NewGuid().ToString("N"))
    $statDirectory = Join-Path $procRoot "4242"
    New-Item -ItemType Directory -Path $statDirectory -Force | Out-Null
    $statPath = Join-Path $statDirectory "stat"
    $remainder = "S 1 4242 4242 0 -1 4194560 1234 0 0 0 10 5 0 0 20 0 11 0 {0} 123456789 4567 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0"
    $process = [pscustomobject]@{ Id = 4242; StartTime = [DateTime]::UtcNow }
    try {
        # 5. Parsing correctness: comm containing spaces and parentheses must not shift field 22.
        $parsed = ConvertFrom-ProcStat ("4242 (node (dev) server) " + ($remainder -f 987654))
        Assert-True ($null -ne $parsed) "stat with a comm containing spaces did not parse"
        Assert-True ($parsed.pid -eq 4242) "stat pid was not preserved"
        Assert-True ($parsed.comm -eq "node (dev) server") "comm with spaces and parentheses was not preserved"
        Assert-True ($parsed.state -eq "S") "state field was not located after the comm field"
        Assert-True ($parsed.startTimeTicks -eq 987654) "starttime (field 22) was not located after a comm containing spaces"
        $naive = ("4242 (node (dev) server) " + ($remainder -f 987654)) -split ' '
        Assert-True ($naive[21] -ne "987654") "test fixture must make a naive split(' ') land on the wrong field"
        Assert-True ((ConvertFrom-ProcStat ("4242 (dotnet) " + ($remainder -f 5))).startTimeTicks -eq 5) "trivial comm did not parse"

        # 1. Same process remains owned: same PID and same boot-relative starttime.
        Set-Content -LiteralPath $statPath -Value ("4242 (node (dev) server) " + ($remainder -f 987654)) -NoNewline
        Assert-True ((Get-LinuxProcessStartTimeTicks -ProcessId 4242 -ProcRoot $procRoot) -eq 987654) "starttime ticks were not read from the stat file"
        $record = Get-ProcessIdentityRecord -Process $process -ProcRoot $procRoot
        Assert-True ($record.Contains("startTimeTicks") -and $record["startTimeTicks"] -eq 987654) "Linux identity record must carry startTimeTicks"
        Assert-True (-not $record.Contains("startTimeUtc")) "Linux identity record must not depend on wall-clock StartTime"
        $persisted = ([pscustomobject]$record | ConvertTo-Json -Depth 8) | ConvertFrom-Json
        Assert-True (Test-ProcessIdentityMatch -Record $persisted -Process $process -ProcRoot $procRoot) "same PID and same starttime ticks must be owned"
        $steppedClock = [pscustomobject]@{ Id = 4242; StartTime = $process.StartTime.AddHours(-3).AddSeconds(-1.115) }
        Assert-True (Test-ProcessIdentityMatch -Record $persisted -Process $steppedClock -ProcRoot $procRoot) "a wall-clock step must not change Linux ownership"

        # 2. PID reuse rejected: same PID, different boot-relative starttime.
        Set-Content -LiteralPath $statPath -Value ("4242 (node (dev) server) " + ($remainder -f 999999)) -NoNewline
        Assert-True (-not (Test-ProcessIdentityMatch -Record $persisted -Process $process -ProcRoot $procRoot)) "a reused PID with a different starttime must be unowned"

        # 4. Malformed / unreadable proc identity fails closed.
        foreach ($malformed in @("", "garbage", "4242 (node (dev) server", "4242 (node) S 1 2", ("4242 (node) " + ($remainder -f "abc")), ("9999 (node) " + ($remainder -f 987654)))) {
            Set-Content -LiteralPath $statPath -Value $malformed -NoNewline
            Assert-True ($null -eq (Get-LinuxProcessStartTimeTicks -ProcessId 4242 -ProcRoot $procRoot)) "malformed stat '$malformed' must not yield an identity"
            Assert-True (-not (Test-ProcessIdentityMatch -Record $persisted -Process $process -ProcRoot $procRoot)) "malformed stat '$malformed' must be unowned"
        }
        Set-Content -LiteralPath $statPath -Value ("4242 (node (dev) server) " + ($remainder -f 987654)) -NoNewline
        $legacyRecord = [pscustomobject]@{ pid = 4242; startTimeUtc = $process.StartTime.ToString("O") }
        Assert-True (-not (Test-ProcessIdentityMatch -Record $legacyRecord -Process $process -ProcRoot $procRoot)) "a legacy wall-clock record must not be reinterpreted as ticks"
        $badRecord = [pscustomobject]@{ pid = 4242; startTimeTicks = "not-a-number" }
        Assert-True (-not (Test-ProcessIdentityMatch -Record $badRecord -Process $process -ProcRoot $procRoot)) "a non-numeric recorded identity must be unowned"

        # 3. Dead process: PID absent from /proc.
        Remove-Item -LiteralPath $statDirectory -Recurse -Force
        Assert-True ($null -eq (Get-LinuxProcessStartTimeTicks -ProcessId 4242 -ProcRoot $procRoot)) "a missing PID must not yield an identity"
        Assert-True (-not (Test-ProcessIdentityMatch -Record $persisted -Process $process -ProcRoot $procRoot)) "a missing PID must be unowned"
        $failed = $false
        try { Get-ProcessIdentityRecord -Process $process -ProcRoot $procRoot | Out-Null } catch { $failed = $true }
        Assert-True $failed "registering a process without a readable /proc identity must fail"

        # 6. Windows behavior is unchanged: wall-clock StartTime with 1 s tolerance.
        $windowsRecord = Get-ProcessIdentityRecord -Process $process -RunningOnWindows
        Assert-True ($windowsRecord.Contains("startTimeUtc") -and -not $windowsRecord.Contains("startTimeTicks")) "Windows identity record must keep startTimeUtc"
        # Windows records are read by Windows PowerShell, where startTimeUtc stays an ISO-8601 string.
        $windowsPersisted = [pscustomobject]@{ pid = 4242; startTimeUtc = [string]$windowsRecord["startTimeUtc"] }
        Assert-True (Test-ProcessIdentityMatch -Record $windowsPersisted -Process $process -RunningOnWindows) "Windows same StartTime must be owned"
        $drifted = [pscustomobject]@{ Id = 4242; StartTime = $process.StartTime.AddSeconds(2) }
        Assert-True (-not (Test-ProcessIdentityMatch -Record $windowsPersisted -Process $drifted -RunningOnWindows)) "Windows StartTime beyond tolerance must be unowned"
        Assert-True (-not (Test-ProcessIdentityMatch -Record ([pscustomobject]@{ pid = 4242 }) -Process $process -RunningOnWindows)) "Windows record without startTimeUtc must be unowned"
        Assert-True (-not (Test-ProcessIdentityMatch -Record $windowsPersisted -Process $null -RunningOnWindows)) "a missing process must be unowned"
    }
    finally {
        Remove-Item -LiteralPath $procRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Process ownership identity checks passed."
}

Test-ProcessOwnershipIdentity

if ($CI) { $Full = $true }
if ($Full) {
    try {
        Invoke-Platform Bootstrap
        Invoke-Platform Bootstrap
        Invoke-Platform Start
        $status = @(Invoke-Platform Status)
        $status | Write-Host
        $statusText = $status -join "`n"
        foreach ($application in @("api", "worker", "web")) {
            if ($statusText -notmatch "(?m)^\s*$application\s+running\(pid=\d+\)") {
                throw "Status did not report $application as an owned running process."
            }
        }
        Invoke-Platform PrintAccess
        Invoke-Platform Scenario @("-Name", "FreshOrder")
        Invoke-Platform Scenario @("-Name", "ExternalOffer")
        Invoke-Platform Scenario @("-Name", "ManualRoute")
        Assert-ManualEndpoints
        Invoke-Platform Stop
        Invoke-Platform Start
        Invoke-Platform Status
        Invoke-Platform Stop
        Invoke-Platform Reset @("-Force")
    }
    catch {
        $failure = $_
        Write-Host "Redacted local host diagnostics:"
        foreach ($path in Get-ChildItem (Join-Path $repositoryRoot ".local/logs") -Filter "*.err.log" -ErrorAction SilentlyContinue) {
            Write-Host "[$($path.Name)]"
            Get-Content $path.FullName -Tail 80 | ForEach-Object {
                $_ `
                    -replace '(?i)(Host|Database|Username|Password)=([^;\s]+)', '$1=[redacted]' `
                    -replace '(?i)(Bearer\s+)[A-Za-z0-9._~-]+', '$1[redacted]' `
                    -replace '(?i)(secret|token|key)(["'':=\s]+)[A-Za-z0-9+/_.~-]{12,}', '$1$2[redacted]'
            }
        }
        throw $failure
    }
    finally {
        & $shellCommand $platform Stop *> $null
    }
}

$global:LASTEXITCODE = 0
Write-Host "DEV_PLATFORM_TESTS_OK"
