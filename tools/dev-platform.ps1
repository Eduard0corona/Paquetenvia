[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet("Doctor", "Bootstrap", "Start", "Status", "Logs", "Stop", "Seed", "Scenario", "Reset", "PrintAccess")]
    [string] $Command,

    [ValidateSet("All", "Api", "Worker", "Web")]
    [string] $Component = "All",

    [ValidateSet("FreshOrder", "ExternalOffer", "ManualRoute")]
    [string] $Name = "FreshOrder",

    [int] $TimeoutSeconds = 240,
    [string] $EnvironmentFile,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$localRoot = Join-Path $repositoryRoot ".local"
$statePath = Join-Path $localRoot "state.json"
$runtimePath = Join-Path $localRoot "runtime.json"
$accessPath = Join-Path $localRoot "access.json"
$pidRoot = Join-Path $localRoot "pids"
$logRoot = Join-Path $localRoot "logs"
$environmentFileWasProvided = -not [string]::IsNullOrWhiteSpace($EnvironmentFile)
$resolvedEnvironmentFile = if (-not $environmentFileWasProvided) {
    Join-Path $repositoryRoot "deploy/.env.local"
} else {
    [System.IO.Path]::GetFullPath($EnvironmentFile)
}
$environmentExample = Join-Path $repositoryRoot "deploy/.env.example"
$localEnvironment = Join-Path $PSScriptRoot "local-environment.ps1"
$devSeedProject = Join-Path $PSScriptRoot "Paqueteria.DevSeed/Paqueteria.DevSeed.csproj"
$organizationId = "11111111-1111-1111-1111-111111111111"
$apiUrl = "http://127.0.0.1:5080"
$workerUrl = "http://127.0.0.1:5081"
$webUrl = "http://127.0.0.1:3000"
$applicationDatabaseSuffix = "_dev_platform"
$requiredSdk = "10.0.101"
$runningOnWindows = $env:OS -eq "Windows_NT"

. (Join-Path $PSScriptRoot "local-environment.common.ps1")
. (Join-Path $PSScriptRoot "dev-platform-process-identity.ps1")

function Test-RepositorySdk([string] $Observed) {
    try {
        $requested = [version]$requiredSdk
        $resolved = [version]$Observed
        return $resolved.Major -eq $requested.Major -and
            $resolved.Minor -eq $requested.Minor -and
            [math]::Floor($resolved.Build / 100) -eq [math]::Floor($requested.Build / 100) -and
            $resolved.Build -ge $requested.Build
    }
    catch { return $false }
}

function Get-RepositoryDotnet {
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $pathCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path $userProfile ".dotnet/dotnet.exe"),
        (Join-Path $userProfile ".dotnet/dotnet"),
        $(if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { Join-Path $env:DOTNET_ROOT "dotnet.exe" }),
        $(if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { Join-Path $env:DOTNET_ROOT "dotnet" }),
        $(if ($null -ne $pathCommand) { $pathCommand.Path })
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_) }
    foreach ($candidate in $candidates) {
        try {
            $observedSdk = (& $candidate --version 2>$null).Trim()
            if ($LASTEXITCODE -eq 0 -and (Test-RepositorySdk $observedSdk)) { return $candidate }
        }
        catch { continue }
    }
    throw "The repository requires .NET SDK $requiredSdk, but it was not found."
}

$dotnetCommand = Get-RepositoryDotnet

function Get-RepositoryPnpm {
    $direct = Get-Command pnpm -ErrorAction SilentlyContinue
    if ($null -ne $direct) {
        return [pscustomobject]@{ FilePath = $direct.Path; Prefix = @() }
    }
    $corepack = Get-Command corepack -ErrorAction SilentlyContinue
    if ($null -ne $corepack) {
        return [pscustomobject]@{ FilePath = $corepack.Path; Prefix = @("pnpm") }
    }
    throw "pnpm is unavailable. Install the repository version or enable Corepack."
}

$pnpmCommand = Get-RepositoryPnpm

function Ensure-Directories {
    foreach ($path in @($localRoot, $pidRoot, $logRoot)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

function Write-JsonFile([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Read-JsonFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
}

function New-LocalSecret {
    $bytes = [byte[]]::new(32)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Ensure-EnvironmentFile {
    if (-not (Test-Path -LiteralPath $resolvedEnvironmentFile)) {
        if ($environmentFileWasProvided) {
            throw "Environment file not found: $resolvedEnvironmentFile"
        }
        Copy-Item -LiteralPath $environmentExample -Destination $resolvedEnvironmentFile
    }
}

function Get-EnvironmentContext {
    Ensure-EnvironmentFile
    return Get-LocalEnvironmentContext `
        -ComposeFile (Join-Path $repositoryRoot "deploy/docker-compose.yml") `
        -EnvironmentFile $resolvedEnvironmentFile
}

function Get-RuntimeState {
    Ensure-Directories
    $runtime = Read-JsonFile $runtimePath
    if ($null -eq $runtime) {
        $runtime = [pscustomobject]@{
            appPassword = New-LocalSecret
            workerPassword = New-LocalSecret
        }
        Write-JsonFile $runtimePath $runtime
    }
    return $runtime
}

function Get-ApplicationDatabaseName($Context) {
    $name = "$($Context.Environment["POSTGRES_DB"])$applicationDatabaseSuffix"
    if ($name.Length -gt 63 -or $name -notmatch '^[a-zA-Z_][a-zA-Z0-9_]*$') {
        throw "The derived development database name is not a valid PostgreSQL identifier."
    }
    return $name
}

function Ensure-ApplicationDatabase {
    $context = Get-EnvironmentContext
    $database = Get-ApplicationDatabaseName $context
    $adminUser = $context.Environment["POSTGRES_USER"]
    $query = "SELECT 1 FROM pg_database WHERE datname = '$database';"
    $probe = Invoke-DockerCompose `
        -Context $context `
        -Arguments @("exec", "-T", "postgres", "psql", "--username", $adminUser, "--dbname", "postgres", "--tuples-only", "--no-align", "--command", $query) `
        -CaptureOutput
    if ($probe.Output.Trim() -eq "1") { return }
    Invoke-DockerCompose `
        -Context $context `
        -Arguments @("exec", "-T", "postgres", "createdb", "--username", $adminUser, "--owner", $adminUser, "--template", "template0", $database) | Out-Null
    Write-Host "Created the isolated local application database '$database'."
}

function Get-Connections {
    $context = Get-EnvironmentContext
    $runtime = Get-RuntimeState
    $hostName = "127.0.0.1"
    $port = $context.Environment["POSTGRES_HOST_PORT"]
    $database = Get-ApplicationDatabaseName $context
    $adminUser = $context.Environment["POSTGRES_USER"]
    $adminPassword = $context.Environment["POSTGRES_PASSWORD"]
    return [pscustomobject]@{
        Context = $context
        Runtime = $runtime
        Admin = "Host=$hostName;Port=$port;Database=$database;Username=$adminUser;Password=$adminPassword;Include Error Detail=false"
        App = "Host=$hostName;Port=$port;Database=$database;Username=paqueteria_local_app;Password=$($runtime.appPassword);Include Error Detail=false"
        Worker = "Host=$hostName;Port=$port;Database=$database;Username=paqueteria_local_worker;Password=$($runtime.workerPassword);Include Error Detail=false"
    }
}

function Invoke-Checked([string] $FilePath, [string[]] $Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DevSeed([string] $Operation) {
    $connections = Get-Connections
    $previous = @{}
    foreach ($name in @(
        "DOTNET_ENVIRONMENT", "PAQUETERIA_LOCAL_DEV_SEED_ENABLED",
        "PAQUETERIA_LOCAL_ADMIN_CONNECTION", "PAQUETERIA_LOCAL_APP_PASSWORD",
        "PAQUETERIA_LOCAL_WORKER_PASSWORD")) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    try {
        $env:DOTNET_ENVIRONMENT = "Development"
        $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = "true"
        $env:PAQUETERIA_LOCAL_ADMIN_CONNECTION = $connections.Admin
        $env:PAQUETERIA_LOCAL_APP_PASSWORD = $connections.Runtime.appPassword
        $env:PAQUETERIA_LOCAL_WORKER_PASSWORD = $connections.Runtime.workerPassword
        Invoke-Checked $dotnetCommand @("run", "--project", $devSeedProject, "--", $Operation)
    }
    finally {
        foreach ($name in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process")
        }
    }
}

function Invoke-Doctor {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw "PowerShell 7 is required; current engine is $($PSVersionTable.PSVersion). Install pwsh and retry."
    }
    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($tool in @("docker", "node")) {
        if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) { $failures.Add($tool) }
    }
    if ($failures.Count -gt 0) { throw "Missing required tools: $($failures -join ', ')." }
    $observedSdk = (& $dotnetCommand --version).Trim()
    if (-not (Test-RepositorySdk $observedSdk)) {
        throw "Expected .NET SDK feature band 10.0.1xx at or above $requiredSdk but resolved $observedSdk."
    }
    $expectedNode = (Get-Content (Join-Path $repositoryRoot ".nvmrc") -Raw).Trim().TrimStart('v')
    $observedNode = (& node --version).Trim().TrimStart('v')
    if ($observedNode -ne $expectedNode) { throw "Expected Node $expectedNode but resolved $observedNode." }
    $packageManager = (Get-Content (Join-Path $repositoryRoot "apps/web/package.json") -Raw | ConvertFrom-Json).packageManager
    $expectedPnpm = $packageManager.Split('@')[-1]
    Push-Location (Join-Path $repositoryRoot "apps/web")
    try { $observedPnpm = (& $pnpmCommand.FilePath @($pnpmCommand.Prefix) --version).Trim() }
    finally { Pop-Location }
    if ($observedPnpm -ne $expectedPnpm) { throw "Expected pnpm $expectedPnpm but resolved $observedPnpm." }
    if (-not (Test-Path -LiteralPath $resolvedEnvironmentFile -PathType Leaf)) {
        throw "Local environment file is missing: $resolvedEnvironmentFile. Bootstrap may synthesize it after Doctor passes."
    }
    Assert-DockerAvailable
    $context = Get-EnvironmentContext
    Assert-ComposeStaticPolicy -Context $context
    $existing = Get-ProjectResourceIds -ProjectName $context.ProjectName
    if ($existing.Containers.Count -eq 0) { Assert-ConfiguredPortsAvailable -Context $context }
    $dirty = @(git status --short --untracked-files=normal | Where-Object { $_ -notmatch '^\?\? deploy/\.env\.local$' })
    if ($LASTEXITCODE -ne 0) { throw "Git worktree could not be inspected." }
    if ($dirty.Count -gt 0) { Write-Warning "Git worktree has $($dirty.Count) tracked or untracked entries." }
    Write-Host "Doctor passed: repository SDK, Docker Compose, Node and pnpm are available."
}

function Invoke-Bootstrap {
    Ensure-Directories
    Ensure-EnvironmentFile
    Invoke-Doctor
    Invoke-Checked $dotnetCommand @("restore", (Join-Path $repositoryRoot "Paqueteria.sln"), "--locked-mode")
    Invoke-Checked $dotnetCommand @("restore", $devSeedProject)
    Push-Location (Join-Path $repositoryRoot "apps/web")
    try { Invoke-Checked $pnpmCommand.FilePath (@($pnpmCommand.Prefix) + @("install", "--frozen-lockfile")) }
    finally { Pop-Location }
    & $localEnvironment Up -EnvironmentFile $resolvedEnvironmentFile -TimeoutSeconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) { throw "FND-002 local environment failed to start." }
    Ensure-ApplicationDatabase
    $connections = Get-Connections
    $previousAdmin = [Environment]::GetEnvironmentVariable("PAQUETERIA_LOCAL_ADMIN_CONNECTION", "Process")
    $previousPath = $env:PATH
    try {
        $env:PAQUETERIA_LOCAL_ADMIN_CONNECTION = $connections.Admin
        $env:PATH = "$(Split-Path -Parent $dotnetCommand)$([IO.Path]::PathSeparator)$previousPath"
        & (Join-Path $PSScriptRoot "database-baseline.ps1") Apply `
            -ConnectionEnvironment PAQUETERIA_LOCAL_ADMIN_CONNECTION `
            -ConfirmInitialBaseline
        if ($LASTEXITCODE -ne 0) { throw "DBA-001 baseline or module migration application failed." }
    }
    finally {
        [Environment]::SetEnvironmentVariable("PAQUETERIA_LOCAL_ADMIN_CONNECTION", $previousAdmin, "Process")
        $env:PATH = $previousPath
    }
    Invoke-DevSeed bootstrap
    $access = [pscustomobject]@{
        organization = [pscustomobject]@{ id = $organizationId; name = "Synthetic Local Organization" }
        profiles = @(
            [pscustomobject]@{ name = "dispatcher"; credential = "local-dispatcher-mfa"; role = "DISPATCHER"; mfa = $true },
            [pscustomobject]@{ name = "driver"; credential = "active-driver"; role = "DRIVER"; mfa = $false },
            [pscustomobject]@{ name = "external-driver"; credential = "external-driver"; role = "DRIVER"; mfa = $false }
        )
        urls = [pscustomobject]@{ dev = "$webUrl/dev"; operations = "$webUrl/ops/dashboard"; driver = "$webUrl/driver/stops" }
    }
    $existingAccess = Read-JsonFile $accessPath
    if ($null -ne $existingAccess -and $null -ne $existingAccess.psobject.Properties["samples"]) {
        $access | Add-Member -NotePropertyName samples -NotePropertyValue $existingAccess.samples
    }
    $seedRequired = $null -eq $access.psobject.Properties["samples"]
    Write-JsonFile $accessPath $access
    Write-JsonFile $statePath ([pscustomobject]@{ bootstrappedAt = [DateTimeOffset]::UtcNow; baseline = (git rev-parse HEAD).Trim() })
    Invoke-Start
    try {
        if ($seedRequired) { Invoke-SeedScenarios } else { Write-Host "Seed already current; no functional duplicates created." }
    }
    finally { Invoke-Stop }
    Write-Host "Bootstrap complete. Deterministic prerequisites and least-privilege runtime logins are ready."
}

function Set-HostConfiguration([string] $Kind) {
    $connections = Get-Connections
    $env:DOTNET_ENVIRONMENT = "Development"
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ConnectionStrings__Paqueteria = if ($Kind -eq "Worker") { $connections.Worker } else { $connections.App }
    $env:ConnectionStrings__PaqueteriaWorker = $connections.Worker
    $env:Authentication__Provider = "Mock"
    $env:IdentityBootstrap__Provider = "PostgreSql"
    $env:Tenancy__Provider = "PostgreSql"
    $env:Locations__Provider = "PostgreSql"
    $env:Locations__GeocodingProvider = "Mock"
    $env:Locations__PiiProtector = "Mock"
    $env:Pricing__Provider = "PostgreSql"
    $env:Orders__Provider = "PostgreSql"
    $env:PublicTracking__Provider = "PostgreSql"
    $env:Drivers__Provider = "PostgreSql"
    $env:Dispatch__Provider = "PostgreSql"
    $env:Routing__Provider = "PostgreSql"
    $env:OperationsDashboard__Provider = "PostgreSql"
    $env:Realtime__Provider = "SignalR"
    $env:Realtime__Backplane = "InProcess"
    $env:Realtime__AllowedOrigins__0 = $webUrl
    $env:Http__Cors__AllowedOrigins__0 = $webUrl
    $env:Realtime__OutboxDispatcher__Provider = "PostgreSql"
    $env:Realtime__OutboxDispatcher__WorkerId = "local-api"
    $env:ProofStorage__Provider = "S3Compatible"
    $env:ProofStorage__ThreatScanner = "Synthetic"
    $env:Notifications__Provider = "PostgreSql"
    $env:Notifications__WorkerId = "local-worker"
    $env:PublicTracking__AllowedOrigins__0 = $webUrl
    $env:AWS_ACCESS_KEY_ID = $connections.Context.Environment["AWS_ACCESS_KEY_ID"]
    $env:AWS_SECRET_ACCESS_KEY = $connections.Context.Environment["AWS_SECRET_ACCESS_KEY"]
    foreach ($key in @("ServiceUrl", "PublicPresignUrl", "Region", "Bucket")) {
        $envName = "ProofStorage__$key"
        [Environment]::SetEnvironmentVariable($envName, $connections.Context.Environment["ProofStorage__$key"], "Process")
    }
    $env:Drivers__Eligibility__PolicyVersion = "LOCAL-SYNTHETIC-v1"
    $env:Drivers__Eligibility__NonExpiringDocumentTypes__0 = "IDENTITY"
    foreach ($vehicleType in @("MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER")) {
        $prefix = "Drivers__Eligibility__VehicleCapacity__$vehicleType"
        [Environment]::SetEnvironmentVariable(
            "Drivers__Eligibility__RequiredDocumentTypesByVehicleType__$vehicleType`__0", "IDENTITY", "Process")
        [Environment]::SetEnvironmentVariable("$prefix`__MaximumPackageCount", "10", "Process")
        [Environment]::SetEnvironmentVariable("$prefix`__MaximumTotalWeightGrams", "50000", "Process")
        [Environment]::SetEnvironmentVariable("$prefix`__MaximumSinglePackageWeightGrams", "10000", "Process")
        [Environment]::SetEnvironmentVariable("$prefix`__MaximumLengthMillimeters", "2000", "Process")
        [Environment]::SetEnvironmentVariable("$prefix`__MaximumWidthMillimeters", "2000", "Process")
        [Environment]::SetEnvironmentVariable("$prefix`__MaximumHeightMillimeters", "2000", "Process")
    }
}

function Clear-WebSensitiveEnvironment {
    foreach ($name in @(
        "ConnectionStrings__Paqueteria",
        "ConnectionStrings__PaqueteriaWorker",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "PAQUETERIA_LOCAL_ADMIN_CONNECTION",
        "PAQUETERIA_LOCAL_APP_CONNECTION",
        "PAQUETERIA_LOCAL_APP_PASSWORD",
        "PAQUETERIA_LOCAL_WORKER_PASSWORD")) {
        [Environment]::SetEnvironmentVariable($name, $null, "Process")
    }
}

function ConvertTo-PosixShellLiteral([string] $Value) {
    $quote = [string][char]39
    $doubleQuote = [string][char]34
    $replacement = "$quote$doubleQuote$quote$doubleQuote$quote"
    return "$quote$($Value.Replace($quote, $replacement))$quote"
}

function Test-OwnedProcess($Record, $Process) {
    if ($null -eq $Record -or $null -eq $Process -or
        $null -eq $Record.psobject.Properties["processPath"]) {
        return $false
    }
    try {
        $startMatches = Test-ProcessIdentityMatch -Record $Record -Process $Process -RunningOnWindows:$runningOnWindows
        $actualPath = [System.IO.Path]::GetFullPath($Process.Path)
        $expectedPath = [System.IO.Path]::GetFullPath($Record.processPath)
        $comparer = if ($runningOnWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
        return $startMatches -and
            $Record.repositoryRoot -eq $repositoryRoot -and
            ($comparer.Equals($actualPath, $expectedPath) -or
                $comparer.Equals([IO.Path]::GetFileName($actualPath), [IO.Path]::GetFileName($expectedPath)))
    }
    catch { return $false }
}

function Start-OwnedProcess(
    [string] $Name,
    [string] $FilePath,
    [string[]] $Arguments,
    [string] $ProcessWorkingDirectory = $repositoryRoot
) {
    $pidPath = Join-Path $pidRoot "$($Name.ToLowerInvariant()).json"
    if (Test-Path $pidPath) {
        $record = Read-JsonFile $pidPath
        $existing = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if (Test-OwnedProcess $record $existing) {
            return
        }
        Remove-Item -LiteralPath $pidPath -Force
    }
    $outputPath = Join-Path $logRoot "$($Name.ToLowerInvariant()).out.log"
    $errorPath = Join-Path $logRoot "$($Name.ToLowerInvariant()).err.log"
    $expectedProcessPath = [System.IO.Path]::GetFullPath($FilePath)
    if ($runningOnWindows) {
        $start = @{
            FilePath = $FilePath
            PassThru = $true
            RedirectStandardOutput = $outputPath
            RedirectStandardError = $errorPath
            WorkingDirectory = $ProcessWorkingDirectory
            WindowStyle = "Hidden"
        }
        if ($Arguments.Count -gt 0) { $start.ArgumentList = $Arguments }
        $process = Start-Process @start
    }
    else {
        $command = "exec " + ((@($FilePath) + $Arguments | ForEach-Object {
            ConvertTo-PosixShellLiteral $_
        }) -join " ") +
            " >> " + (ConvertTo-PosixShellLiteral $outputPath) +
            " 2>> " + (ConvertTo-PosixShellLiteral $errorPath)
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.FileName = "/bin/sh"
        $start.WorkingDirectory = $ProcessWorkingDirectory
        $start.UseShellExecute = $false
        $start.ArgumentList.Add("-c")
        $start.ArgumentList.Add($command)
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { throw "Failed to start $Name." }
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    $record = [ordered]@{
        name = $Name
        pid = $process.Id
    }
    $record += Get-ProcessIdentityRecord -Process $process -RunningOnWindows:$runningOnWindows
    $record["processPath"] = $expectedProcessPath
    $record["repositoryRoot"] = $repositoryRoot
    Write-JsonFile $pidPath ([pscustomobject]$record)
}

function Get-ListeningProcessId([int] $Port) {
    if ($runningOnWindows) {
        $connections = @(Get-NetTCPConnection -LocalAddress "127.0.0.1" -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
        $processIds = @($connections | Select-Object -ExpandProperty OwningProcess -Unique)
    }
    else {
        $processIds = @()
        $socketTool = Get-Command ss -ErrorAction SilentlyContinue
        if ($null -ne $socketTool) {
            $listeners = @(& $socketTool.Source -ltnp 2>$null | Where-Object { $_ -match "127\.0\.0\.1:$Port\b" })
            $processIds = @($listeners | ForEach-Object {
                [regex]::Matches($_, 'pid=(\d+)') | ForEach-Object { [int]$_.Groups[1].Value }
            } | Select-Object -Unique)
        }
        if ($processIds.Count -eq 0) {
            $lsof = Get-Command lsof -ErrorAction SilentlyContinue
            if ($null -ne $lsof) {
                $processIds = @(& $lsof.Source -nP "-iTCP:$Port" -sTCP:LISTEN -t 2>$null |
                    Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ } | Select-Object -Unique)
            }
        }
    }
    if ($processIds.Count -ne 1) {
        throw "Expected exactly one process listening on 127.0.0.1:$Port; found $($processIds.Count)."
    }
    return [int]$processIds[0]
}

function Set-WebListenerOwnership {
    $pidPath = Join-Path $pidRoot "web.json"
    $supervisorRecord = Read-JsonFile $pidPath
    $listenerPid = Get-ListeningProcessId 3000
    $listener = Get-Process -Id $listenerPid -ErrorAction Stop
    $webWorkingDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "apps/web"))
    if ($runningOnWindows) {
        $details = Get-CimInstance Win32_Process -Filter "ProcessId=$listenerPid" -ErrorAction Stop
        $identityText = [string]$details.CommandLine
        $belongsToCheckout = $identityText.Replace('\', '/').Contains($webWorkingDirectory.Replace('\', '/'))
    }
    else {
        $readlink = Get-Command readlink -ErrorAction Stop
        $actualWorkingDirectory = [string](& $readlink.Source -f "/proc/$listenerPid/cwd")
        $belongsToCheckout = $actualWorkingDirectory -eq $webWorkingDirectory
    }
    if (-not $belongsToCheckout) {
        throw "The process listening on 127.0.0.1:3000 does not belong to this checkout."
    }
    $record = [ordered]@{
        name = "Web"
        pid = $listenerPid
    }
    $record += Get-ProcessIdentityRecord -Process $listener -RunningOnWindows:$runningOnWindows
    $record["processPath"] = [System.IO.Path]::GetFullPath($listener.Path)
    $record["repositoryRoot"] = $repositoryRoot
    if ($null -ne $supervisorRecord -and $supervisorRecord.pid -ne $listenerPid) {
        $supervisor = Get-Process -Id $supervisorRecord.pid -ErrorAction SilentlyContinue
        if (Test-OwnedProcess $supervisorRecord $supervisor) {
            $record["supervisor"] = $supervisorRecord
        }
    }
    Write-JsonFile $pidPath ([pscustomobject]$record)
}

function Wait-Http([string] $Uri, [string] $Label) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return }
        } catch { Start-Sleep -Milliseconds 500 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "$Label did not become ready at $Uri."
}

function Invoke-Start {
    if (-not (Test-Path $accessPath)) { Invoke-Bootstrap }
    Ensure-Directories
    $apiProject = Join-Path $repositoryRoot "src/Paqueteria.Api/Paqueteria.Api.csproj"
    $workerProject = Join-Path $repositoryRoot "src/Paqueteria.Worker/Paqueteria.Worker.csproj"
    Invoke-Checked $dotnetCommand @("build", $apiProject, "--no-restore", "--configuration", "Debug")
    Invoke-Checked $dotnetCommand @("build", $workerProject, "--no-restore", "--configuration", "Debug")
    $apiHost = Join-Path $repositoryRoot $(if ($runningOnWindows) {
        "src/Paqueteria.Api/bin/Debug/net10.0/Paqueteria.Api.exe"
    } else { "src/Paqueteria.Api/bin/Debug/net10.0/Paqueteria.Api" })
    $workerHost = Join-Path $repositoryRoot $(if ($runningOnWindows) {
        "src/Paqueteria.Worker/bin/Debug/net10.0/Paqueteria.Worker.exe"
    } else { "src/Paqueteria.Worker/bin/Debug/net10.0/Paqueteria.Worker" })
    $node = Get-Command node -ErrorAction Stop
    $nextCli = Join-Path $repositoryRoot "apps/web/node_modules/next/dist/bin/next"
    Set-HostConfiguration Api
    $env:ASPNETCORE_URLS = $apiUrl
    Start-OwnedProcess Api $apiHost @()
    Set-HostConfiguration Worker
    $env:ASPNETCORE_URLS = $workerUrl
    Start-OwnedProcess Worker $workerHost @()
    Clear-WebSensitiveEnvironment
    $env:NODE_ENV = "development"
    $env:PAQUETERIA_DEV_PORTAL_ENABLED = "true"
    $env:NEXT_PUBLIC_API_BASE_URL = $apiUrl
    # The web CSP connect-src must list the signed-upload storage origin.
    $presignUrl = $env:ProofStorage__PublicPresignUrl
    $env:PAQUETERIA_CSP_CONNECT_SOURCES = if ([string]::IsNullOrWhiteSpace($presignUrl)) { $null } else {
        ([Uri]$presignUrl).GetLeftPart([UriPartial]::Authority)
    }
    Start-OwnedProcess `
        -Name Web `
        -FilePath $node.Path `
        -Arguments @($nextCli, "dev", "--hostname", "127.0.0.1", "--port", "3000") `
        -ProcessWorkingDirectory (Join-Path $repositoryRoot "apps/web")
    try {
        Wait-Http "$apiUrl/health/live" "API live"
        Wait-Http "$apiUrl/health/ready" "API ready"
        Wait-Http "$workerUrl/health/live" "Worker live"
        Wait-Http "$workerUrl/health/ready" "Worker ready"
        Wait-Http "$webUrl/health" "Web"
        Set-WebListenerOwnership
    }
    catch {
        Write-Host "Startup failed. Inspect $logRoot/api.err.log, worker.err.log or web.err.log."
        throw
    }
    Write-Host "Paquetenvia local ready"
    Write-Host "Web=$webUrl API=$apiUrl Worker=$workerUrl"
    Write-Host "Operations=$webUrl/ops/dashboard Driver=$webUrl/driver/stops Dev=$webUrl/dev"
    Write-Host "Mailpit=http://127.0.0.1:8025 MinIO=http://127.0.0.1:9001"
}

function Get-OwnedProcessStatus([string] $Name) {
    $record = Read-JsonFile (Join-Path $pidRoot "$($Name.ToLowerInvariant()).json")
    if ($null -eq $record) { return "stopped" }
    $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return "stopped" }
    if (-not (Test-OwnedProcess $record $process)) { return "unowned" }
    return "running(pid=$($record.pid))"
}

function Invoke-Status {
    Write-Host "Infrastructure:"
    try {
        $context = Get-EnvironmentContext
        $states = Get-ComposeServiceState -Context $context
        foreach ($name in @("postgres", "redis", "minio", "mailpit")) {
            $service = $states | Where-Object { $_.Service -eq $name } | Select-Object -First 1
            $status = if ($null -eq $service) { "stopped" } elseif ($service.Health) { $service.Health } else { $service.State }
            Write-Host ("  {0,-10} {1}" -f $name, $status)
        }
    } catch { Write-Host "  unavailable" }
    Write-Host "Applications:"
    Write-Host "  api       $(Get-OwnedProcessStatus Api)"
    Write-Host "  worker    $(Get-OwnedProcessStatus Worker)"
    Write-Host "  web       $(Get-OwnedProcessStatus Web)"
    $state = Read-JsonFile $statePath
    $databaseStatus = if ($null -eq $state) { "not bootstrapped" } else { "baseline applied; migrations current; seed current" }
    Write-Host "Database: $databaseStatus"
    Write-Host "dev=$webUrl/dev operations=$webUrl/ops/dashboard driver=$webUrl/driver/stops api=$apiUrl"
}

function Stop-ProcessTree([int] $RootPid) {
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$RootPid" -ErrorAction SilentlyContinue)
    foreach ($child in $children) { Stop-ProcessTree -RootPid $child.ProcessId }
    Stop-Process -Id $RootPid -Force -ErrorAction SilentlyContinue
}

function Stop-OwnedProcessRecord($Record) {
    if ($null -eq $Record) { return }
    $process = Get-Process -Id $Record.pid -ErrorAction SilentlyContinue
    if (-not (Test-OwnedProcess $Record $process)) { return }
    if ($runningOnWindows) {
        Stop-ProcessTree -RootPid $Record.pid
    }
    else {
        Stop-Process -Id $Record.pid -ErrorAction SilentlyContinue
        Wait-Process -Id $Record.pid -Timeout 5 -ErrorAction SilentlyContinue
        if (Get-Process -Id $Record.pid -ErrorAction SilentlyContinue) {
            Stop-Process -Id $Record.pid -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-Stop {
    foreach ($name in @("Web", "Worker", "Api")) {
        $path = Join-Path $pidRoot "$($name.ToLowerInvariant()).json"
        $record = Read-JsonFile $path
        if ($null -eq $record) { continue }
        if ($null -ne $record.psobject.Properties["supervisor"]) {
            Stop-OwnedProcessRecord $record.supervisor
        }
        Stop-OwnedProcessRecord $record
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Host processes stopped. FND-002 containers, volumes and seed were preserved."
}

function Invoke-Logs {
    $names = if ($Component -eq "All") { @("api", "worker", "web") } else { @($Component.ToLowerInvariant()) }
    foreach ($name in $names) {
        foreach ($suffix in @("out", "err")) {
            $path = Join-Path $logRoot "$name.$suffix.log"
            if (Test-Path $path) { Write-Host "[$name/$suffix]"; Get-Content $path -Tail 100 }
        }
    }
}

function Invoke-FreshOrder {
    Wait-Http "$apiUrl/health/ready" "API ready"
    $headers = @{ Authorization = "Bearer local-dispatcher-mfa"; "X-Organization-Id" = $organizationId }
    $nonce = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $quoteHeaders = $headers.Clone(); $quoteHeaders["Idempotency-Key"] = "local-fresh-quote-$nonce"
    $quoteBody = @{
        client_account_id = $null
        origin = @{ address_text="Synthetic Origin"; contact_name="Synthetic Sender"; phone="+520000000001"; lat=24.78; lng=-107.42; references="LOCAL SYNTHETIC" }
        destination = @{ address_text="Synthetic Destination"; contact_name="Synthetic Receiver"; phone="+520000000002"; lat=24.79; lng=-107.41; references="LOCAL SYNTHETIC" }
        service_type="SAME_DAY"; consolidated_route=$false
        packages=@(@{ description="Synthetic parcel"; weight_grams=1000; declared_value_cents=0; length_mm=100; width_mm=100; height_mm=100 })
    } | ConvertTo-Json -Depth 8
    $quote = Invoke-RestMethod -Method Post -Uri "$apiUrl/api/v1/quotes" -Headers $quoteHeaders -ContentType "application/json" -Body $quoteBody
    $orderHeaders = $headers.Clone(); $orderHeaders["Idempotency-Key"] = "local-fresh-order-$nonce"
    $orderBody = @{
        quote_id=$quote.id; payer_type="SENDER"
        acceptance=@{ terms_version="local-synthetic-v1"; privacy_version="local-synthetic-v1"; accepted_at=[DateTimeOffset]::UtcNow.ToString("O"); acceptance_channel="API" }
    } | ConvertTo-Json -Depth 6
    $order = Invoke-RestMethod -Method Post -Uri "$apiUrl/api/v1/orders" -Headers $orderHeaders -ContentType "application/json" -Body $orderBody
    $context = Get-EnvironmentContext
    $query = "SELECT count(*)||':'||COALESCE(string_agg(n.status,','),'none') FROM notifications.notifications n JOIN platform.outbox_events e ON e.id=n.source_event_id WHERE e.aggregate_id='$($order.id)';"
    $database = Get-ApplicationDatabaseName $context
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $result = Invoke-DockerCompose -Context $context -Arguments @("exec", "-T", "postgres", "psql", "-U", $context.Environment["POSTGRES_USER"], "-d", $database, "-Atc", $query) -CaptureOutput
        if ($result.Output -notmatch '^0:') { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($result.Output -match '^0:') { throw "NTF-001 did not create a notification for FreshOrder." }
    [pscustomobject]@{
        publicId = $order.public_id
        state = $order.status
        operationsUrl = "$webUrl/ops/dashboard"
        trackingUrl = $null
        notifications = $result.Output
    } | ConvertTo-Json
}

function Invoke-ExternalOffer {
    Wait-Http "$apiUrl/health/ready" "API ready"
    $nonce = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $order = New-SeedOrder "external-offer-$nonce"
    $order = Move-SeedOrder $order "external-offer-$nonce" "CONFIRMED" 1
    $order = Move-SeedOrder $order "external-offer-$nonce" "READY_FOR_PICKUP" 2
    $expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(30).ToString("O")
    $offer = Invoke-ApiPost "/api/v1/external-offers" "local-dispatcher-mfa" "local-external-offer-create-$nonce" @{
        order_id = $order.id
        commission_cents = 12500
        expires_at = $expiresAt
        eligible_constraints = @{ vehicle_types = @("MOTORCYCLE") }
    }
    $driverHeaders = @{
        Authorization = "Bearer external-driver"
        "X-Organization-Id" = $organizationId
    }
    $page = Invoke-RestMethod -Method Get -Uri "$apiUrl/api/v1/driver/me/external-offers" -Headers $driverHeaders
    $visible = @($page.items) | Where-Object { $_.id -eq $offer.id }
    if ($visible.Count -ne 1 -or $visible[0].commission.amount_cents -ne 12500 -or [string]::IsNullOrWhiteSpace($visible[0].expires_at)) {
        throw "ExternalOffer was not visible to the synthetic eligible external driver."
    }
    $accepted = Invoke-ApiPost "/api/v1/external-offers/$($offer.id)/accept" "external-driver" "local-external-offer-accept-$nonce" @{}
    $context = Get-EnvironmentContext
    $database = Get-ApplicationDatabaseName $context
    $query = "SELECT count(*)||':'||min(a.assignment_type)||':'||min(a.cost_cents) FROM dispatch.assignments a WHERE a.order_id='$($order.id)';"
    $result = Invoke-DockerCompose -Context $context -Arguments @(
        "exec", "-T", "postgres", "psql", "-U", $context.Environment["POSTGRES_USER"],
        "-d", $database, "-Atc", $query) -CaptureOutput
    if ($result.Output.Trim() -ne "1:EXTERNAL:12500") {
        throw "ExternalOffer acceptance did not create exactly one EXTERNAL assignment at the offered commission."
    }
    [pscustomobject]@{
        publicId = $order.public_id
        offerId = $offer.id
        commissionCents = $visible[0].commission.amount_cents
        expiresAt = $visible[0].expires_at
        assignmentId = $accepted.id
        assignment = $result.Output.Trim()
        operationsUrl = "$webUrl/ops/dashboard"
        driverUrl = "$webUrl/driver/stops"
    } | ConvertTo-Json
}

function Invoke-ManualRoute {
    Wait-Http "$apiUrl/health/ready" "API ready"
    $nonce = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $alias = "manual-route-$nonce"
    $orders = @()
    foreach ($suffix in @("a", "b", "c")) {
        $orderAlias = "$alias-$suffix"
        $order = New-SeedOrder $orderAlias
        $order = Move-SeedOrder $order $orderAlias "CONFIRMED" 1
        $order = Move-SeedOrder $order $orderAlias "READY_FOR_PICKUP" 2
        Assign-SeedOrder $order $orderAlias
        $orders += $order
    }

    $route = Invoke-ManualRouteRequest "create-route" "Post" "/api/v1/routes" "local-$alias-create-v1" @{
        driver_id = "55555555-5555-5555-5555-555555555551"
        city_id = "44444444-4444-4444-4444-444444444441"
        service_area_id = $null
        scheduled_for = [DateOnly]::FromDateTime([DateTime]::UtcNow.AddDays(1)).ToString("yyyy-MM-dd")
    }
    foreach ($index in 0..2) {
        $route = Invoke-ManualRouteRequest "add-stop[$index]" "Post" "/api/v1/routes/$($route.id)/stops" "local-$alias-add-$index-v1" @{
            order_id = $orders[$index].id
            expected_version = $route.version
        }
    }
    if ($route.stops.Count -ne 3 -or $route.assignment_cost_cents_total -ne 3000) {
        throw "ManualRoute did not create three OWN stops with the expected 3000-cent cost."
    }

    $reversedStopIds = @($route.stops | Sort-Object sequence -Descending | ForEach-Object { $_.id })
    $route = Invoke-ManualRouteRequest "reorder" "Put" "/api/v1/routes/$($route.id)/stops/order" "local-$alias-reorder-v1" @{
        expected_version=$route.version
        stop_ids=$reversedStopIds
    }
    if ((@($route.stops | Sort-Object sequence | ForEach-Object { $_.id }) -join ',') -ne ($reversedStopIds -join ',')) {
        throw "ManualRoute reorder did not persist the requested contiguous sequence."
    }

    $removedStopId = $route.stops[1].id
    $route = Invoke-ManualRouteRequest "remove" "Delete" "/api/v1/routes/$($route.id)/stops/$removedStopId`?expected_version=$($route.version)" "local-$alias-remove-v1"
    if ($route.stops.Count -ne 2 -or $route.assignment_cost_cents_total -ne 2000 -or
        (@($route.stops | Sort-Object sequence | ForEach-Object { $_.sequence }) -join ',') -ne '1,2') {
        throw "ManualRoute remove did not compact stops or preserve the expected 2000-cent cost."
    }

    $detail = Invoke-RestMethod -Method Get -Uri "$apiUrl/api/v1/routes/$($route.id)" -Headers @{
        Authorization = "Bearer local-dispatcher-mfa"
        "X-Organization-Id" = $organizationId
    }
    if ($detail.version -ne $route.version -or $detail.stops.Count -ne 2) {
        throw "ManualRoute authoritative REST recovery did not match the final mutation response."
    }

    $context = Get-EnvironmentContext
    $database = Get-ApplicationDatabaseName $context
    $query = "SELECT count(*)||':'||COALESCE(min(status),'none') FROM platform.outbox_events WHERE aggregate_type='Route' AND aggregate_id='$($route.id)' AND topic='routes.route-changed';"
    $outbox = Invoke-DockerCompose -Context $context -Arguments @(
        "exec", "-T", "postgres", "psql", "-U", $context.Environment["POSTGRES_USER"],
        "-d", $database, "-Atc", $query) -CaptureOutput
    if ($outbox.Output.Trim() -notmatch '^[1-9][0-9]*:(PENDING|CLAIMED|PROCESSED)$') {
        throw "ManualRoute did not produce RouteChanged transactional outbox evidence."
    }

    [pscustomobject]@{
        routeId = $route.id
        version = $route.version
        stopCount = $route.stops.Count
        assignmentCostCentsTotal = $route.assignment_cost_cents_total
        orderPublicIds = @($orders | ForEach-Object { $_.public_id })
        routeChangedOutbox = $outbox.Output.Trim()
        operationsUrl = "$webUrl/ops/routes?routeId=$($route.id)"
    } | ConvertTo-Json -Depth 5
}

function Invoke-ManualRouteRequest(
    [string] $Operation,
    [ValidateSet("Post", "Put", "Delete")] [string] $Method,
    [string] $Path,
    [string] $IdempotencyKey,
    $Body = $null
) {
    $headers = @{
        Authorization = "Bearer local-dispatcher-mfa"
        "X-Organization-Id" = $organizationId
        "Idempotency-Key" = $IdempotencyKey
    }
    try {
        $parameters = @{
            Method = $Method
            Uri = "$apiUrl$Path"
            Headers = $headers
        }
        if ($null -ne $Body) {
            $parameters.ContentType = "application/json"
            $parameters.Body = $Body | ConvertTo-Json -Depth 10
        }
        return Invoke-RestMethod @parameters
    }
    catch {
        $status = if ($null -ne $_.Exception.Response) {
            [int]$_.Exception.Response.StatusCode
        } else {
            0
        }
        $problem = $null
        if (-not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
            try { $problem = $_.ErrorDetails.Message | ConvertFrom-Json } catch { $problem = $null }
        }
        $code = if ($null -ne $problem -and $null -ne $problem.psobject.Properties["code"]) {
            [string]$problem.code
        } else {
            "UNAVAILABLE"
        }
        $title = if ($null -ne $problem -and $null -ne $problem.psobject.Properties["title"]) {
            [string]$problem.title
        } else {
            "UNAVAILABLE"
        }
        $code = ($code -replace '[^A-Za-z0-9_.-]', '?')
        $title = ($title -replace '[^A-Za-z0-9 ._-]', '?')
        if ($code.Length -gt 80) { $code = $code.Substring(0, 80) }
        if ($title.Length -gt 120) { $title = $title.Substring(0, 120) }
        throw "ManualRoute operation=$Operation status=$status code=$code title=$title"
    }
}

function Invoke-ApiPost(
    [string] $Path,
    [string] $Credential,
    [string] $IdempotencyKey,
    $Body
) {
    $headers = @{
        Authorization = "Bearer $Credential"
        "X-Organization-Id" = $organizationId
        "Idempotency-Key" = $IdempotencyKey
    }
    return Invoke-RestMethod `
        -Method Post `
        -Uri "$apiUrl$Path" `
        -Headers $headers `
        -ContentType "application/json" `
        -Body ($Body | ConvertTo-Json -Depth 10)
}

function New-SeedOrder([string] $Alias) {
    $quote = Invoke-ApiPost "/api/v1/quotes" "local-dispatcher-mfa" "local-seed-$Alias-quote-v1" @{
        client_account_id = $null
        origin = @{ address_text="Synthetic $Alias Origin"; contact_name="Synthetic Sender"; phone="+520000000001"; lat=24.78; lng=-107.42; references="LOCAL SYNTHETIC" }
        destination = @{ address_text="Synthetic $Alias Destination"; contact_name="Synthetic Receiver"; phone="+520000000002"; lat=24.79; lng=-107.41; references="LOCAL SYNTHETIC" }
        service_type="SAME_DAY"; consolidated_route=$false
        packages=@(@{ description="Synthetic $Alias parcel"; weight_grams=1000; declared_value_cents=0; length_mm=100; width_mm=100; height_mm=100 })
    }
    return Invoke-ApiPost "/api/v1/orders" "local-dispatcher-mfa" "local-seed-$Alias-order-v1" @{
        quote_id=$quote.id; payer_type="SENDER"
        # AI05-INPUT-LIMITS: the server accepts accepted_at only within [now - 72 h, now + 5 min].
        acceptance=@{ terms_version="local-synthetic-v1"; privacy_version="local-synthetic-v1"; accepted_at=[DateTimeOffset]::UtcNow.ToString("O"); acceptance_channel="API" }
    }
}

function Move-SeedOrder($Order, [string] $Alias, [string] $Status, [int] $ExpectedVersion) {
    return Invoke-ApiPost "/api/v1/orders/$($Order.id)/transitions" "local-dispatcher-mfa" "local-seed-$Alias-$($Status.ToLowerInvariant())-v1" @{
        target_status=$Status; reason="LOCAL_SYNTHETIC_SEED"; expected_version=$ExpectedVersion; metadata=@{}
    }
}

function Assign-SeedOrder($Order, [string] $Alias) {
    [void](Invoke-ApiPost "/api/v1/orders/$($Order.id)/assignments" "local-dispatcher-mfa" "local-seed-$Alias-assignment-v1" @{
        driver_id="55555555-5555-5555-5555-555555555551"; assignment_type="OWN"; cost_cents=1000; route_id=$null
    })
}

function Complete-SeedProof($Order, [string] $Alias, [string] $ProofType) {
    $bytes = [byte[]](137,80,78,71,13,10,26,10)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $hasher.ComputeHash($bytes) } finally { $hasher.Dispose() }
    $sha256 = ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    $operation = $ProofType.ToLowerInvariant().Replace('_','-')
    $upload = Invoke-ApiPost "/api/v1/orders/$($Order.id)/proof-upload-sessions" "active-driver" "local-seed-$Alias-$operation-upload-v1" @{
        proof_type=$ProofType; content_type="image/png"; size_bytes=$bytes.Length; sha256=$sha256
    }
    $uploadHeaders = @{}
    foreach ($property in $upload.required_headers.psobject.Properties) {
        $uploadHeaders[$property.Name] = [string]$property.Value
    }
    Invoke-WebRequest -Method Put -Uri $upload.upload_url -Headers $uploadHeaders -Body $bytes -UseBasicParsing | Out-Null
    # OPS-003-SERVER-72H-REJECTION: a capture older than 72 hours is OFFLINE_OPERATION_EXPIRED, so the
    # synthetic capture happens now; it is fixed before the retries so every attempt sends the same request.
    $capturedAt = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            [void](Invoke-ApiPost "/api/v1/orders/$($Order.id)/proofs" "active-driver" "local-seed-$Alias-$operation-finalize-v1" @{
                upload_session_id=$upload.id; proof_type=$ProofType; captured_at=$capturedAt; sha256=$sha256
            })
            return
        }
        catch {
            Start-Sleep -Seconds 1
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Synthetic $ProofType proof did not become ready for $Alias."
}

function Invoke-SeedScenarios {
    Wait-Http "$apiUrl/health/ready" "API ready"
    $dispatch = New-SeedOrder "dispatch"
    $dispatch = Move-SeedOrder $dispatch "dispatch" "CONFIRMED" 1
    $dispatch = Move-SeedOrder $dispatch "dispatch" "READY_FOR_PICKUP" 2

    $assigned = New-SeedOrder "assigned"
    $assigned = Move-SeedOrder $assigned "assigned" "CONFIRMED" 1
    $assigned = Move-SeedOrder $assigned "assigned" "READY_FOR_PICKUP" 2
    Assign-SeedOrder $assigned "assigned"

    $transit = New-SeedOrder "transit"
    $transit = Move-SeedOrder $transit "transit" "CONFIRMED" 1
    $transit = Move-SeedOrder $transit "transit" "READY_FOR_PICKUP" 2
    Assign-SeedOrder $transit "transit"
    $transit = Move-SeedOrder $transit "transit" "AT_PICKUP" 4
    Complete-SeedProof $transit "transit" "PICKUP_PHOTO"
    $transit = Move-SeedOrder $transit "transit" "PICKED_UP" 5
    $transit = Move-SeedOrder $transit "transit" "IN_TRANSIT" 6

    $delivered = New-SeedOrder "delivered"
    $delivered = Move-SeedOrder $delivered "delivered" "CONFIRMED" 1
    $delivered = Move-SeedOrder $delivered "delivered" "READY_FOR_PICKUP" 2
    Assign-SeedOrder $delivered "delivered"
    $delivered = Move-SeedOrder $delivered "delivered" "AT_PICKUP" 4
    Complete-SeedProof $delivered "delivered" "PICKUP_PHOTO"
    $delivered = Move-SeedOrder $delivered "delivered" "PICKED_UP" 5
    $delivered = Move-SeedOrder $delivered "delivered" "IN_TRANSIT" 6
    $delivered = Move-SeedOrder $delivered "delivered" "DELIVERING" 7
    Complete-SeedProof $delivered "delivered" "DELIVERY_PHOTO"
    $delivered = Move-SeedOrder $delivered "delivered" "DELIVERED" 8

    $connections = Get-Connections
    $previousEnvironment = $env:DOTNET_ENVIRONMENT
    $previousOptIn = $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED
    $previousAppConnection = $env:PAQUETERIA_LOCAL_APP_CONNECTION
    try {
        $env:DOTNET_ENVIRONMENT = "Development"
        $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = "true"
        $env:PAQUETERIA_LOCAL_APP_CONNECTION = $connections.App
        $trackingOutput = @(& $dotnetCommand run --project $devSeedProject -- tracking $delivered.id)
        if ($LASTEXITCODE -ne 0) { throw "Tracking sample issuance failed." }
        $trackingToken = $trackingOutput | Where-Object { $_ -match '^[A-Za-z0-9_-]{40,128}$' } | Select-Object -Last 1
        if ([string]::IsNullOrWhiteSpace($trackingToken)) { throw "Tracking sample issuance returned no token." }
    }
    finally {
        $env:DOTNET_ENVIRONMENT = $previousEnvironment
        $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = $previousOptIn
        $env:PAQUETERIA_LOCAL_APP_CONNECTION = $previousAppConnection
    }

    $access = Read-JsonFile $accessPath
    $access | Add-Member -NotePropertyName samples -NotePropertyValue ([pscustomobject]@{
        dispatch = $dispatch.public_id
        assigned = $assigned.public_id
        transit = $transit.public_id
        delivered = $delivered.public_id
        trackingUrl = "$webUrl/track/$trackingToken"
    }) -Force
    Write-JsonFile $accessPath $access
    Write-Host "Seed current: dispatch, assigned, in-transit and delivered synthetic orders."
}

function Invoke-PrintAccess {
    $access = Read-JsonFile $accessPath
    if ($null -eq $access) { throw "Bootstrap has not produced local access metadata." }
    Write-Host "Organization: $($access.organization.name) ($($access.organization.id))"
    foreach ($profile in $access.profiles) {
        Write-Host "$($profile.name): Bearer $($profile.credential) role=$($profile.role) mfa=$($profile.mfa)"
    }
    Write-Host "Dev: $($access.urls.dev)"
    Write-Host "Operations: $($access.urls.operations)"
    Write-Host "Driver: $($access.urls.driver)"
    Write-Host "Mailpit: http://127.0.0.1:8025"
    Write-Host "MinIO: http://127.0.0.1:9001"
    if ($null -ne $access.psobject.Properties["samples"]) {
        Write-Host "Tracking/history sample: $($access.samples.trackingUrl)"
    }
}

function Invoke-Reset {
    if (-not $Force) {
        $answer = Read-Host "Reset deletes only Paquetenvia local resources and .local state. Type RESET to continue"
        if ($answer -cne "RESET") { throw "Reset cancelled; no resources were deleted." }
    }
    Invoke-Stop
    Ensure-EnvironmentFile
    & $localEnvironment Reset -EnvironmentFile $resolvedEnvironmentFile -Force
    if ($LASTEXITCODE -ne 0) { throw "Scoped FND-002 reset failed." }
    if (Test-Path $localRoot) {
        $resolvedLocalRoot = [System.IO.Path]::GetFullPath($localRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
        foreach ($entry in Get-ChildItem -LiteralPath $resolvedLocalRoot -Force) {
            if ($entry.Name -eq ".gitignore") { continue }
            $resolvedEntry = [System.IO.Path]::GetFullPath($entry.FullName)
            if (-not $resolvedEntry.StartsWith("$resolvedLocalRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean a path outside .local: $resolvedEntry"
            }
            Remove-Item -LiteralPath $resolvedEntry -Recurse -Force
        }
    }
    Write-Host "Scoped reset complete. deploy/.env.local was retained for repeatable local bootstrap."
}

try {
    switch ($Command) {
        Doctor { Invoke-Doctor }
        Bootstrap { Invoke-Bootstrap }
        Start { Invoke-Start }
        Status { Invoke-Status }
        Logs { Invoke-Logs }
        Stop { Invoke-Stop }
        Seed { Invoke-SeedScenarios }
        Scenario {
            if ($Name -eq "FreshOrder") { Invoke-FreshOrder }
            elseif ($Name -eq "ExternalOffer") { Invoke-ExternalOffer }
            elseif ($Name -eq "ManualRoute") { Invoke-ManualRoute }
            else { throw "Unknown scenario '$Name'. Expected FreshOrder, ExternalOffer or ManualRoute." }
        }
        Reset { Invoke-Reset }
        PrintAccess { Invoke-PrintAccess }
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
