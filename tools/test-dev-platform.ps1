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
$seedProject = Join-Path $PSScriptRoot "Paqueteria.DevSeed/Paqueteria.DevSeed.csproj"
$portalPolicy = Join-Path $repositoryRoot "apps/web/src/dev/dev-portal-policy.ts"
$portalPage = Join-Path $repositoryRoot "apps/web/src/app/dev/page.tsx"
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

$oldEnvironment = $env:DOTNET_ENVIRONMENT
$oldOptIn = $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED
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
}
finally {
    $ErrorActionPreference = $oldErrorActionPreference
    $env:DOTNET_ENVIRONMENT = $oldEnvironment
    $env:PAQUETERIA_LOCAL_DEV_SEED_ENABLED = $oldOptIn
}

$newFiles = @(
    $platform,
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
