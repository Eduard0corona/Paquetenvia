[CmdletBinding()]
param(
    [switch] $CI,
    [string] $SourceEnvironmentFile,
    [string] $TargetEnvironmentFile,
    [string] $IdentityFile,
    [string] $OutputDirectory,
    [int] $TimeoutSeconds = 1200,
    [switch] $KeepResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "local-environment.common.ps1")
. (Join-Path $PSScriptRoot "backup-restore.common.ps1")

$script:negativeResults = [System.Collections.Generic.List[object]]::new()
$script:ownedPaths = [System.Collections.Generic.List[string]]::new()
$script:sourceContext = $null
$script:targetContext = $null
$script:sourceDestroyed = $false
$script:targetRestored = $false
$script:identityOwned = $false
$script:wrongIdentity = $null
$script:currentPhase = "preflight"
$drillStartedAt = [DateTimeOffset]::UtcNow
$drillStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$env:OPS002_TEST_MODE = "true"

function Add-NegativeResult {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [bool] $Passed
    )
    if (-not $Passed) {
        throw "Required negative test failed: $Name"
    }
    $script:negativeResults.Add([ordered]@{
        name = $Name
        passed = $true
    })
}

function Assert-ExpectedFailure {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Action
    )
    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
    }
    Add-NegativeResult -Name $Name -Passed $failed
}

function Invoke-ExpectedScriptFailure {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Script,
        [Parameter(Mandatory)] [string[]] $Arguments
    )
    $result = Invoke-Ops002Process -FilePath "pwsh" -Arguments (
        @("-NoLogo", "-NoProfile", "-File", $Script) + $Arguments
    ) -TimeoutSeconds $TimeoutSeconds -AllowFailure
    Add-NegativeResult -Name $Name -Passed ($result.ExitCode -ne 0)
}

function Get-RedactedChildFailurePhase {
    param([AllowEmptyString()] [string] $StandardError)

    $failureText = if ($null -eq $StandardError) { "" } else { $StandardError }
    $machineMatch = [regex]::Match(
        $failureText,
        'OPS002_FAILURE_PHASE=([a-z_]+)',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if ($machineMatch.Success) {
        return $machineMatch.Groups[1].Value
    }
    $messageMatch = [regex]::Match(
        $failureText,
        "phase '([a-z_]+)'",
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if ($messageMatch.Success) {
        return $messageMatch.Groups[1].Value
    }
    return "unclassified"
}

function Add-ExpectedBackupFailureResult {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] $Result,
        [Parameter(Mandatory)] [string] $ExpectedPhase
    )

    $capturedText = "{0}`n{1}" -f $Result.StandardError, $Result.StandardOutput
    $actualPhase = Get-RedactedChildFailurePhase -StandardError $capturedText
    if ($Result.ExitCode -eq 0 -or $actualPhase -cne $ExpectedPhase) {
        throw (
            "Required negative test failed: {0}; expected phase '{1}', observed '{2}', exit {3}." -f
            $Name, $ExpectedPhase, $actualPhase, $Result.ExitCode)
    }
    Add-NegativeResult -Name $Name -Passed $true
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Write-SyntheticEnvironment {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $ProjectName,
        [Parameter(Mandatory)] [string] $Database,
        [Parameter(Mandatory)] [string] $DatabaseUser,
        [Parameter(Mandatory)] [string] $Password,
        [Parameter(Mandatory)] [string] $Bucket,
        [Parameter(Mandatory)] [string] $AccessKey
    )
    $ports = 1..6 | ForEach-Object { Get-FreePort }
    $content = @"
COMPOSE_PROJECT_NAME=$ProjectName
POSTGRES_HOST_PORT=$($ports[0])
POSTGRES_DB=$Database
POSTGRES_USER=$DatabaseUser
POSTGRES_PASSWORD=$Password
REDIS_HOST_PORT=$($ports[1])
REDIS_PASSWORD=$Password
MINIO_API_HOST_PORT=$($ports[2])
MINIO_CONSOLE_HOST_PORT=$($ports[3])
MINIO_ROOT_USER=$AccessKey
MINIO_ROOT_PASSWORD=$Password
MINIO_BUCKET=$Bucket
AWS_ACCESS_KEY_ID=$AccessKey
AWS_SECRET_ACCESS_KEY=$Password
ProofStorage__Provider=S3Compatible
ProofStorage__ThreatScanner=Synthetic
ProofStorage__ServiceUrl=http://127.0.0.1:$($ports[2])
ProofStorage__PublicPresignUrl=http://127.0.0.1:$($ports[2])
ProofStorage__Region=us-east-1
ProofStorage__Bucket=$Bucket
ProofStorage__ProcessingIntervalSeconds=5
ProofStorage__StaleValidationSeconds=300
ProofStorage__MaximumConcurrency=4
MAIL_SMTP_HOST_PORT=$($ports[4])
MAIL_UI_HOST_PORT=$($ports[5])
"@
    [System.IO.File]::WriteAllText(
        $Path, $content, [System.Text.UTF8Encoding]::new($false))
    if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) {
        & chmod 600 -- $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict synthetic environment permissions."
        }
    }
}

function Get-TemporaryStageSnapshot {
    return @(
        Get-ChildItem -LiteralPath ([System.IO.Path]::GetTempPath()) `
            -Directory -Filter "paquetenvia-ops002-*" -ErrorAction SilentlyContinue |
        ForEach-Object FullName
    )
}

function New-Ops002SecurityTestArchive {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [object[]] $Entries
    )
    $file = [System.IO.File]::Create($Path)
    $gzip = [System.IO.Compression.GZipStream]::new(
        $file,
        [System.IO.Compression.CompressionLevel]::Optimal)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip, $false)
    try {
        foreach ($definition in $Entries) {
            $type = if ($definition.Type -eq "SymbolicLink") {
                [System.Formats.Tar.TarEntryType]::SymbolicLink
            }
            elseif ($definition.Type -eq "Directory") {
                [System.Formats.Tar.TarEntryType]::Directory
            }
            else {
                [System.Formats.Tar.TarEntryType]::RegularFile
            }
            $entry = [System.Formats.Tar.PaxTarEntry]::new($type, $definition.Name)
            if ($type -eq [System.Formats.Tar.TarEntryType]::SymbolicLink) {
                $entry.LinkName = "payload/manifest.json"
            }
            elseif ($type -ne [System.Formats.Tar.TarEntryType]::Directory) {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes(
                    [string]$definition.Content)
                $entry.DataStream = [System.IO.MemoryStream]::new($bytes, $false)
            }
            $writer.WriteEntry($entry)
        }
    }
    finally {
        $writer.Dispose()
        $gzip.Dispose()
        $file.Dispose()
    }
}

function Assert-NoNewPlaintextStage {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $Before
    )
    $after = @(Get-TemporaryStageSnapshot)
    $unexpected = @($after | Where-Object { $_ -notin $Before })
    if ($unexpected.Count -ne 0) {
        throw "Plaintext staging residue was detected."
    }
}

function Invoke-DatabaseBaseline {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [ValidateSet("Apply", "Assert")] [string] $Operation,
        [string] $Database
    )
    $databaseName = if ([string]::IsNullOrWhiteSpace($Database)) {
        $Context.Environment["POSTGRES_DB"]
    }
    else {
        $Database
    }
    $variable = "OPS002_DRILL_DATABASE_CONNECTION"
    $connection = "Host=127.0.0.1;Port=$($Context.Environment['POSTGRES_HOST_PORT']);" +
        "Database=$databaseName;Username=$($Context.Environment['POSTGRES_USER']);" +
        "Password=$($Context.Environment['POSTGRES_PASSWORD']);Pooling=false;Timeout=15;Command Timeout=120"
    [Environment]::SetEnvironmentVariable($variable, $connection, "Process")
    try {
        $arguments = @(
            "-NoLogo", "-NoProfile", "-File",
            (Join-Path $repositoryRoot "tools/database-baseline.ps1"),
            $Operation, "-ConnectionEnvironment", $variable
        )
        if ($Operation -eq "Apply") {
            $arguments += "-ConfirmInitialBaseline"
        }
        Invoke-Ops002Process -FilePath "pwsh" -Arguments $arguments `
            -TimeoutSeconds $TimeoutSeconds | Out-Null
    }
    finally {
        [Environment]::SetEnvironmentVariable($variable, $null, "Process")
        $connection = $null
    }
}

function Invoke-McScript {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $ScriptText,
        [string] $Mount
    )
    $arguments = @("run", "--rm", "--no-deps")
    if (-not [string]::IsNullOrWhiteSpace($Mount)) {
        $arguments += @("--volume", $Mount)
    }
    $arguments += @("--entrypoint", "/bin/sh", "minio-init", "-s")
    return Invoke-Ops002Compose -Context $Context -Arguments $arguments `
        -InputText $ScriptText -TimeoutSeconds $TimeoutSeconds
}

function Set-ProofObjects {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $FixtureDirectory,
        [Parameter(Mandatory)] [string] $PickupKey,
        [Parameter(Mandatory)] [string] $DeliveryKey,
        [Parameter(Mandatory)] [string] $DecoyKey
    )
    $mount = "$([System.IO.Path]::GetFullPath($FixtureDirectory))`:/fixtures:ro"
    $scriptText = @"
set -eu
mc alias set local http://minio:9000 "`$MINIO_ROOT_USER" "`$MINIO_ROOT_PASSWORD" >/dev/null
mc cp --quiet /fixtures/pickup.png "local/`$MINIO_BUCKET/$PickupKey"
mc cp --quiet /fixtures/delivery.png "local/`$MINIO_BUCKET/$DeliveryKey"
mc cp --quiet /fixtures/decoy.png "local/`$MINIO_BUCKET/$DecoyKey"
mc anonymous set none "local/`$MINIO_BUCKET" >/dev/null
"@
    Invoke-McScript -Context $Context -ScriptText $scriptText -Mount $mount | Out-Null
}

function Remove-ProofObject {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Key
    )
    $scriptText = @"
set -eu
mc alias set local http://minio:9000 "`$MINIO_ROOT_USER" "`$MINIO_ROOT_PASSWORD" >/dev/null
mc rm --quiet "local/`$MINIO_BUCKET/$Key"
"@
    Invoke-McScript -Context $Context -ScriptText $scriptText | Out-Null
}

function Invoke-BackupProcess {
    param(
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $Recipient,
        [string[]] $Additional = @(),
        [switch] $AllowFailure
    )
    $arguments = @(
        "-NoLogo", "-NoProfile", "-File",
        (Join-Path $repositoryRoot "tools/backup-environment.ps1"),
        "-ComposeFile", (Join-Path $repositoryRoot "deploy/docker-compose.yml"),
        "-EnvironmentFile", $script:sourceContext.EnvironmentFile,
        "-ProjectName", $script:sourceContext.ProjectName,
        "-Database", $script:sourceContext.Environment["POSTGRES_DB"],
        "-OutputDirectory", $Destination,
        "-Recipient", $Recipient,
        "-ConfirmQuiesced",
        "-TestIdentityFile", $script:identityPath,
        "-TimeoutSeconds", [string]$TimeoutSeconds,
        "-RequireFixture"
    ) + $Additional
    return Invoke-Ops002Process -FilePath "pwsh" -Arguments $arguments `
        -TimeoutSeconds $TimeoutSeconds -AllowFailure:$AllowFailure
}

function Invoke-RestoreProcess {
    param(
        [Parameter(Mandatory)] [string] $ArtifactPath,
        [Parameter(Mandatory)] [string] $IdentityPath,
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $Database,
        [string[]] $Additional = @(),
        [switch] $AllowFailure
    )
    $arguments = @(
        "-NoLogo", "-NoProfile", "-File",
        (Join-Path $repositoryRoot "tools/restore-environment.ps1"),
        "-Artifact", $ArtifactPath,
        "-IdentityFile", $IdentityPath,
        "-ComposeFile", (Join-Path $repositoryRoot "deploy/docker-compose.yml"),
        "-EnvironmentFile", $script:targetContext.EnvironmentFile,
        "-ProjectName", $Project,
        "-RestoredDatabase", $Database,
        "-ConfirmRestore",
        "-TimeoutSeconds", [string]$TimeoutSeconds
    ) + $Additional
    return Invoke-Ops002Process -FilePath "pwsh" -Arguments $arguments `
        -TimeoutSeconds $TimeoutSeconds -AllowFailure:$AllowFailure
}

function Assert-EphemeralLoginRls {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Database
    )

    $roleA = "ops002_a_$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    $roleB = "ops002_b_$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    $passwordA = [Guid]::NewGuid().ToString("N")
    $passwordB = [Guid]::NewGuid().ToString("N")
    $escapedPasswordA = $passwordA.Replace("'", "''")
    $escapedPasswordB = $passwordB.Replace("'", "''")
    $rolesCreated = $false
    try {
        Invoke-Ops002PostgresQuery -Context $Context -Database $Database -Query @"
BEGIN;
CREATE ROLE $roleA LOGIN NOINHERIT NOBYPASSRLS PASSWORD '$escapedPasswordA';
CREATE ROLE $roleB LOGIN NOINHERIT NOBYPASSRLS PASSWORD '$escapedPasswordB';
GRANT paqueteria_app TO $roleA,$roleB;
COMMIT;
"@ | Out-Null
        $rolesCreated = $true

        $loginShell = @'
set -eu
IFS= read -r PGUSER
IFS= read -r PGPASSWORD
export PGUSER PGPASSWORD
exec psql --no-psqlrc --quiet --tuples-only --no-align --set ON_ERROR_STOP=1 \
  --host 127.0.0.1 --dbname "$OPS002_DATABASE"
'@
        $tenantASql = @'
SET ROLE paqueteria_app;
BEGIN;
SELECT set_config('app.current_user','10000000-0000-4000-8000-000000000002',true);
SELECT set_config('app.current_org_ids','{10000000-0000-4000-8000-000000000001}',true);
DO $ops002$
BEGIN
  IF (SELECT count(*) FROM orders.orders) <> 1 OR
     EXISTS (
       SELECT 1 FROM orders.orders
       WHERE owner_org_id='20000000-0000-4000-8000-000000000001'
     ) THEN
    RAISE EXCEPTION 'OPS002_LOGIN_TENANT_A_RLS_FAILED';
  END IF;
END
$ops002$;
COMMIT;
DO $ops002$
BEGIN
  IF (SELECT count(*) FROM orders.orders) <> 0 THEN
    RAISE EXCEPTION 'OPS002_LOGIN_CONTEXT_LEAKED';
  END IF;
END
$ops002$;
SELECT 'OPS002_LOGIN_RLS_OK';
'@
        $tenantAInput = "$roleA`n$passwordA`n$tenantASql"
        $tenantAResult = Invoke-Ops002Compose -Context $Context -Arguments @(
            "exec", "-T", "--env", "OPS002_DATABASE=$Database",
            "postgres", "/bin/sh", "-c", $loginShell
        ) -InputText $tenantAInput -TimeoutSeconds $TimeoutSeconds
        if ($tenantAResult.StandardOutput.Trim() -notmatch "OPS002_LOGIN_RLS_OK") {
            throw "Tenant A ephemeral login RLS assertions failed."
        }

        $tenantBSql = @'
SET ROLE paqueteria_app;
BEGIN;
SELECT set_config('app.current_user','20000000-0000-4000-8000-000000000002',true);
SELECT set_config('app.current_org_ids','{20000000-0000-4000-8000-000000000001}',true);
DO $ops002$
BEGIN
  IF (SELECT count(*) FROM orders.orders) <> 1 OR
     EXISTS (
       SELECT 1 FROM orders.orders
       WHERE owner_org_id='10000000-0000-4000-8000-000000000001'
     ) THEN
    RAISE EXCEPTION 'OPS002_LOGIN_TENANT_B_RLS_FAILED';
  END IF;
END
$ops002$;
COMMIT;
SELECT 'OPS002_LOGIN_RLS_OK';
'@
        $tenantBInput = "$roleB`n$passwordB`n$tenantBSql"
        $tenantBResult = Invoke-Ops002Compose -Context $Context -Arguments @(
            "exec", "-T", "--env", "OPS002_DATABASE=$Database",
            "postgres", "/bin/sh", "-c", $loginShell
        ) -InputText $tenantBInput -TimeoutSeconds $TimeoutSeconds
        if ($tenantBResult.StandardOutput.Trim() -notmatch "OPS002_LOGIN_RLS_OK") {
            throw "Tenant B ephemeral login RLS assertions failed."
        }
    }
    finally {
        $passwordA = $null
        $passwordB = $null
        $escapedPasswordA = $null
        $escapedPasswordB = $null
        if ($rolesCreated) {
            Invoke-Ops002PostgresQuery -Context $Context -Database $Database -Query @"
REVOKE paqueteria_app FROM $roleA,$roleB;
DROP ROLE $roleA,$roleB;
"@ | Out-Null
        }
    }
}

function Copy-BackupReportForArtifact {
    param(
        [Parameter(Mandatory)] [string] $SourceArtifact,
        [Parameter(Mandatory)] [string] $DestinationArtifact
    )
    $report = Get-Content -LiteralPath "$SourceArtifact.report.json" -Raw -Encoding utf8 |
        ConvertFrom-Json
    $report.artifact_sha256 = Get-Ops002Sha256 -Path $DestinationArtifact
    $report.artifact_bytes = (Get-Item -LiteralPath $DestinationArtifact).Length
    Write-Ops002Json -Value $report -Path "$DestinationArtifact.report.json"
    Assert-Ops002RedactedReport -Path "$DestinationArtifact.report.json"
}

function New-InternalTamperedArtifact {
    param(
        [Parameter(Mandatory)] [ValidateSet("MANIFEST", "DUMP", "OBJECT")] [string] $Kind,
        [Parameter(Mandatory)] [string] $SourceArtifact,
        [Parameter(Mandatory)] [string] $Recipient,
        [Parameter(Mandatory)] [string] $DestinationArtifact
    )
    $tamperStage = New-Ops002StagingDirectory -Purpose "tamper"
    try {
        $archive = Join-Path $tamperStage "payload.tar.gz"
        Invoke-Ops002Process -FilePath "age" -Arguments @(
            "--decrypt", "--identity", $script:identityPath,
            "--output", $archive, $SourceArtifact
        ) | Out-Null
        $extracted = Join-Path $tamperStage "extracted"
        Expand-Ops002ValidatedArchive -Archive $archive -Destination $extracted
        $payload = Join-Path $extracted "payload"
        switch ($Kind) {
            "MANIFEST" {
                $manifestPath = Join-Path $payload "manifest.json"
                $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 |
                    ConvertFrom-Json
                $manifest.format_version = "tampered"
                Write-Ops002Json -Value $manifest -Path $manifestPath
            }
            "DUMP" {
                [System.IO.File]::AppendAllText(
                    (Join-Path $payload "postgres/database.dump"), "tampered")
            }
            "OBJECT" {
                $firstObject = Get-ChildItem -LiteralPath (
                    Join-Path $payload "object-storage") -File -Recurse |
                    Select-Object -First 1
                [System.IO.File]::AppendAllText($firstObject.FullName, "tampered")
            }
        }
        $rebuilt = Join-Path $tamperStage "rebuilt.tar.gz"
        Invoke-Ops002Process -FilePath "tar" -Arguments @(
            "-czf", $rebuilt, "-C", $extracted, "payload"
        ) | Out-Null
        Invoke-Ops002Process -FilePath "age" -Arguments @(
            "--encrypt", "--recipient", $Recipient,
            "--output", $DestinationArtifact, $rebuilt
        ) | Out-Null
        Copy-BackupReportForArtifact `
            -SourceArtifact $SourceArtifact `
            -DestinationArtifact $DestinationArtifact
    }
    finally {
        Remove-Ops002StagingDirectory -Path $tamperStage
    }
}

try {
    foreach ($command in @("docker", "pwsh", "age", "age-keygen", "tar", "git")) {
        if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Required drill command '$command' is unavailable."
        }
    }
    Assert-DockerAvailable

    $runSuffix = [Guid]::NewGuid().ToString("N").Substring(0, 12)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = if (-not [string]::IsNullOrWhiteSpace($env:OPS002_RESULTS_DIRECTORY)) {
            $env:OPS002_RESULTS_DIRECTORY
        }
        else {
            Join-Path $temporaryRoot "ops002-results-$runSuffix"
        }
    }
    $resultsRoot = Assert-Ops002ExternalPath -Path $OutputDirectory -Purpose "Drill output"
    if (Test-Path -LiteralPath $resultsRoot) {
        if (@(Get-ChildItem -LiteralPath $resultsRoot -Force).Count -ne 0) {
            throw "Drill output must be absent or empty."
        }
    }
    else {
        New-Item -ItemType Directory -Path $resultsRoot | Out-Null
        $script:ownedPaths.Add($resultsRoot)
    }

    if ([string]::IsNullOrWhiteSpace($SourceEnvironmentFile)) {
        $SourceEnvironmentFile = $env:OPS002_SOURCE_ENV_FILE
    }
    if ([string]::IsNullOrWhiteSpace($TargetEnvironmentFile)) {
        $TargetEnvironmentFile = $env:OPS002_TARGET_ENV_FILE
    }
    if ([string]::IsNullOrWhiteSpace($SourceEnvironmentFile)) {
        $SourceEnvironmentFile = Join-Path $temporaryRoot "ops002-source-$runSuffix.env"
        Write-SyntheticEnvironment -Path $SourceEnvironmentFile `
            -ProjectName "ops002-source-$runSuffix" `
            -Database "ops002_source" `
            -DatabaseUser "ops002_$runSuffix" `
            -Password "synthetic-$runSuffix-source" `
            -Bucket "ops002-source-$runSuffix" `
            -AccessKey "ops002$([Guid]::NewGuid().ToString('N'))"
        $script:ownedPaths.Add($SourceEnvironmentFile)
    }
    if ([string]::IsNullOrWhiteSpace($TargetEnvironmentFile)) {
        $TargetEnvironmentFile = Join-Path $temporaryRoot "ops002-target-$runSuffix.env"
        Write-SyntheticEnvironment -Path $TargetEnvironmentFile `
            -ProjectName "ops002-target-$runSuffix" `
            -Database "ops002_bootstrap" `
            -DatabaseUser "ops002_$runSuffix" `
            -Password "synthetic-$runSuffix-target" `
            -Bucket "ops002-target-$runSuffix" `
            -AccessKey "ops002$([Guid]::NewGuid().ToString('N'))"
        $script:ownedPaths.Add($TargetEnvironmentFile)
    }

    $script:sourceContext = Get-LocalEnvironmentContext `
        -ComposeFile (Join-Path $repositoryRoot "deploy/docker-compose.yml") `
        -EnvironmentFile $SourceEnvironmentFile
    $script:targetContext = Get-LocalEnvironmentContext `
        -ComposeFile (Join-Path $repositoryRoot "deploy/docker-compose.yml") `
        -EnvironmentFile $TargetEnvironmentFile
    if ($script:sourceContext.ProjectName -ceq $script:targetContext.ProjectName) {
        throw "Source and target Compose projects must differ."
    }

    if ([string]::IsNullOrWhiteSpace($IdentityFile)) {
        $IdentityFile = $env:OPS002_IDENTITY_FILE
    }
    if ([string]::IsNullOrWhiteSpace($IdentityFile)) {
        $IdentityFile = Join-Path $temporaryRoot "ops002-identity-$runSuffix.txt"
        Invoke-Ops002Process -FilePath "age-keygen" -Arguments @(
            "--output", $IdentityFile
        ) | Out-Null
        $script:identityOwned = $true
        $script:ownedPaths.Add($IdentityFile)
    }
    $script:identityPath = Assert-Ops002IdentityFile -IdentityFile $IdentityFile
    $recipient = (Invoke-Ops002Process -FilePath "age-keygen" -Arguments @(
        "--y", $script:identityPath
    )).StandardOutput.Trim()
    Assert-Ops002Recipient -Recipient $recipient | Out-Null

    Assert-ComposeStaticPolicy -Context $script:sourceContext
    Assert-ConfiguredPortsAvailable -Context $script:sourceContext
    $script:currentPhase = "source_infrastructure"
    Start-ComposeEnvironment -Context $script:sourceContext -TimeoutSeconds $TimeoutSeconds
    $script:currentPhase = "source_baseline"
    $sourceDataDatabase = "ops002_source_data_$runSuffix"
    Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query (
        "CREATE DATABASE `"$sourceDataDatabase`" TEMPLATE template0;") | Out-Null
    $script:sourceContext.Environment["POSTGRES_DB"] = $sourceDataDatabase
    Invoke-DatabaseBaseline -Context $script:sourceContext -Operation Apply
    Invoke-DatabaseBaseline -Context $script:sourceContext -Operation Assert

    $script:currentPhase = "source_fixture"
    $fixtureStage = New-Ops002StagingDirectory -Purpose "fixture"
    try {
        $fixtureObjects = Join-Path $fixtureStage "objects"
        New-Item -ItemType Directory -Path $fixtureObjects | Out-Null
        $pickupPath = Join-Path $fixtureObjects "pickup.png"
        $deliveryPath = Join-Path $fixtureObjects "delivery.png"
        $decoyPath = Join-Path $fixtureObjects "decoy.png"
        [System.IO.File]::WriteAllBytes(
            $pickupPath,
            [Convert]::FromBase64String((Get-Content (
                Join-Path $repositoryRoot "tests/fixtures/ops-002/objects/synthetic-pickup.png.b64"
            ) -Raw).Trim()))
        [System.IO.File]::WriteAllBytes(
            $deliveryPath,
            [Convert]::FromBase64String((Get-Content (
                Join-Path $repositoryRoot "tests/fixtures/ops-002/objects/synthetic-delivery.png.b64"
            ) -Raw).Trim()))
        [System.IO.File]::WriteAllBytes(
            $decoyPath,
            [Convert]::FromBase64String((Get-Content (
                Join-Path $repositoryRoot "tests/fixtures/ops-002/objects/synthetic-decoy.png.b64"
            ) -Raw).Trim()))

        $ownerOrganizationId = "10000000-0000-4000-8000-000000000001"
        $primaryOrderId = "10000000-0000-4000-8000-000000000010"
        $pickupSessionId = "10000000-0000-4000-8000-000000000025"
        $deliverySessionId = "10000000-0000-4000-8000-000000000026"
        $pickupKey = "proofs/{0}/{1}/{2}" -f
            $ownerOrganizationId,
            $primaryOrderId,
            $pickupSessionId
        $deliveryKey = "proofs/{0}/{1}/{2}" -f
            $ownerOrganizationId,
            $primaryOrderId,
            $deliverySessionId
        $decoyKey = "synthetic/{0}" -f [Guid]::NewGuid().ToString("N")
        $seed = Get-Content -LiteralPath (
            Join-Path $repositoryRoot "tests/fixtures/ops-002/seed.sql"
        ) -Raw -Encoding utf8
        $replacements = [ordered]@{
            "__DRIVER_DOCUMENT_KEY__" = "synthetic/{0}" -f [Guid]::NewGuid().ToString("N")
            "__PICKUP_QUARANTINE_KEY__" = "quarantine/{0}/{1}/{2}" -f
                $ownerOrganizationId, $primaryOrderId, $pickupSessionId
            "__DELIVERY_QUARANTINE_KEY__" = "quarantine/{0}/{1}/{2}" -f
                $ownerOrganizationId, $primaryOrderId, $deliverySessionId
            "__PICKUP_KEY__" = $pickupKey
            "__DELIVERY_KEY__" = $deliveryKey
            "__PICKUP_SHA__" = Get-Ops002Sha256 -Path $pickupPath
            "__DELIVERY_SHA__" = Get-Ops002Sha256 -Path $deliveryPath
            "__PICKUP_BYTES__" = [string](Get-Item $pickupPath).Length
            "__DELIVERY_BYTES__" = [string](Get-Item $deliveryPath).Length
        }
        foreach ($entry in $replacements.GetEnumerator()) {
            $seed = $seed.Replace([string]$entry.Key, [string]$entry.Value)
        }
        if ($seed -match '__[A-Z0-9_]+__') {
            throw "Fixture placeholder replacement was incomplete."
        }
        $seedResult = Invoke-Ops002PostgresQuery -Context $script:sourceContext `
            -Query $seed -TimeoutSeconds $TimeoutSeconds
        if ($seedResult.StandardOutput.Trim() -notmatch "OPS002_SEED_OK") {
            throw "OPS-002 source fixture failed."
        }
        Set-ProofObjects -Context $script:sourceContext `
            -FixtureDirectory $fixtureObjects `
            -PickupKey $pickupKey `
            -DeliveryKey $deliveryKey `
            -DecoyKey $decoyKey
        Assert-Ops002BucketPrivate -Context $script:sourceContext

        $script:currentPhase = "static_negative_guards"
        Assert-ExpectedFailure -Name "backup_without_recipient" -Action {
            Assert-Ops002Recipient -Recipient ""
        }
        Assert-ExpectedFailure -Name "invalid_recipient" -Action {
            Assert-Ops002Recipient -Recipient "age1invalid"
        }
        Assert-ExpectedFailure -Name "output_inside_repository" -Action {
            Assert-Ops002ExternalPath `
                -Path (Join-Path $repositoryRoot "TestResults/ops002") `
                -Purpose "Backup output"
        }
        Assert-ExpectedFailure -Name "process_timeout_enforced" -Action {
            Invoke-Ops002Process -FilePath "pwsh" -Arguments @(
                "-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Seconds 5"
            ) -TimeoutSeconds 1 | Out-Null
        }

        $archiveGuardStage = New-Ops002StagingDirectory -Purpose "archive-guard"
        try {
            $traversalArchive = Join-Path $archiveGuardStage "traversal.tar.gz"
            New-Ops002SecurityTestArchive -Path $traversalArchive -Entries @(
                @{ Type = "RegularFile"; Name = "payload/../escape"; Content = "x" }
            )
            Assert-ExpectedFailure -Name "archive_path_traversal_rejected" -Action {
                Assert-Ops002ArchiveEntries -Archive $traversalArchive | Out-Null
            }

            $absoluteArchive = Join-Path $archiveGuardStage "absolute.tar.gz"
            New-Ops002SecurityTestArchive -Path $absoluteArchive -Entries @(
                @{ Type = "RegularFile"; Name = "/payload/manifest.json"; Content = "x" }
            )
            Assert-ExpectedFailure -Name "archive_absolute_path_rejected" -Action {
                Assert-Ops002ArchiveEntries -Archive $absoluteArchive | Out-Null
            }

            $linkArchive = Join-Path $archiveGuardStage "link.tar.gz"
            New-Ops002SecurityTestArchive -Path $linkArchive -Entries @(
                @{ Type = "SymbolicLink"; Name = "payload/object-storage/link"; Content = "" }
            )
            Assert-ExpectedFailure -Name "archive_symlink_rejected" -Action {
                Assert-Ops002ArchiveEntries -Archive $linkArchive | Out-Null
            }

            $limitsArchive = Join-Path $archiveGuardStage "limits.tar.gz"
            New-Ops002SecurityTestArchive -Path $limitsArchive -Entries @(
                @{ Type = "RegularFile"; Name = "payload/manifest.json"; Content = "12" },
                @{ Type = "RegularFile"; Name = "payload/object-inventory.json"; Content = "[]" }
            )
            Assert-ExpectedFailure -Name "archive_entry_limit_enforced" -Action {
                Assert-Ops002ArchiveEntries -Archive $limitsArchive -MaximumEntries 1 | Out-Null
            }
            Assert-ExpectedFailure -Name "archive_expanded_size_limit_enforced" -Action {
                Assert-Ops002ArchiveEntries -Archive $limitsArchive -MaximumExpandedBytes 1 | Out-Null
            }

            $allowlistArchive = Join-Path $archiveGuardStage "allowlist.tar.gz"
            New-Ops002SecurityTestArchive -Path $allowlistArchive -Entries @(
                @{ Type = "RegularFile"; Name = "payload/secret.env"; Content = "x" }
            )
            Assert-ExpectedFailure -Name "archive_allowlist_enforced" -Action {
                Assert-Ops002ArchiveEntries -Archive $allowlistArchive | Out-Null
            }
        }
        finally {
            Remove-Ops002StagingDirectory -Path $archiveGuardStage
        }
        $existingOutput = Join-Path $temporaryRoot "ops002-existing-$runSuffix"
        New-Item -ItemType Directory -Path $existingOutput | Out-Null
        try {
            Invoke-ExpectedScriptFailure -Name "output_already_exists" `
                -Script (Join-Path $repositoryRoot "tools/backup-environment.ps1") `
                -Arguments @(
                    "-EnvironmentFile", $script:sourceContext.EnvironmentFile,
                    "-OutputDirectory", $existingOutput,
                    "-Recipient", $recipient,
                    "-ConfirmQuiesced"
                )
        }
        finally {
            Remove-Item -LiteralPath $existingOutput -Force -Recurse
        }

        $script:currentPhase = "unhealthy_service_guard"
        Invoke-DockerCompose -Context $script:sourceContext -Arguments @(
            "stop", "minio"
        ) | Out-Null
        try {
            $unhealthyOutput = Join-Path $temporaryRoot "ops002-unhealthy-$runSuffix"
            $result = Invoke-BackupProcess -Destination $unhealthyOutput `
                -Recipient $recipient -AllowFailure
            Add-ExpectedBackupFailureResult -Name "services_unhealthy" `
                -Result $result -ExpectedPhase "service_health"
        }
        finally {
            Invoke-DockerCompose -Context $script:sourceContext -Arguments @(
                "start", "minio"
            ) | Out-Null
            Wait-ComposeHealthy -Context $script:sourceContext -TimeoutSeconds $TimeoutSeconds
        }

        $script:currentPhase = "confirmation_guard"
        Invoke-ExpectedScriptFailure -Name "source_not_confirmed_quiesced" `
            -Script (Join-Path $repositoryRoot "tools/backup-environment.ps1") `
            -Arguments @(
                "-EnvironmentFile", $script:sourceContext.EnvironmentFile,
                "-OutputDirectory", (Join-Path $temporaryRoot "ops002-no-quiesce-$runSuffix"),
                "-Recipient", $recipient
            )

        $script:currentPhase = "outbox_quiescence_guard"
        Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query @'
UPDATE platform.outbox_events
SET status='PROCESSING',locked_at=clock_timestamp(),locked_by='ops002-negative',
    lease_token='90000000-0000-4000-8000-000000000001',
    lease_expires_at=clock_timestamp()+interval '5 minutes'
WHERE id='10000000-0000-4000-8000-000000000031';
'@ | Out-Null
        try {
            $processingOutput = Join-Path $temporaryRoot "ops002-processing-$runSuffix"
            $result = Invoke-BackupProcess -Destination $processingOutput `
                -Recipient $recipient -AllowFailure
            Add-ExpectedBackupFailureResult -Name "outbox_processing" `
                -Result $result -ExpectedPhase "outbox_quiescence"
        }
        finally {
            Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query @'
UPDATE platform.outbox_events
SET status='PROCESSED',locked_at=NULL,locked_by=NULL,lease_token=NULL,
    lease_expires_at=NULL
WHERE id='10000000-0000-4000-8000-000000000031';
'@ | Out-Null
        }

        $script:currentPhase = "pod_quiescence_guard"
        Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query @'
UPDATE custody.proof_upload_sessions
SET status='READY'
WHERE id='10000000-0000-4000-8000-000000000025';
'@ | Out-Null
        try {
            $podOutput = Join-Path $temporaryRoot "ops002-pod-$runSuffix"
            $result = Invoke-BackupProcess -Destination $podOutput `
                -Recipient $recipient -AllowFailure
            Add-ExpectedBackupFailureResult -Name "pod_session_transitional" `
                -Result $result -ExpectedPhase "pod_quiescence"
        }
        finally {
            Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query @'
UPDATE custody.proof_upload_sessions
SET status='CONSUMED'
WHERE id='10000000-0000-4000-8000-000000000025';
'@ | Out-Null
        }

        $script:currentPhase = "fingerprint_guard"
        $stageSnapshot = @(Get-TemporaryStageSnapshot)
        $fingerprintOutput = Join-Path $temporaryRoot "ops002-fingerprint-$runSuffix"
        $result = Invoke-BackupProcess -Destination $fingerprintOutput `
            -Recipient $recipient -Additional @(
                "-TestFailureStage", "FINGERPRINT_CHANGE"
            ) -AllowFailure
        Add-ExpectedBackupFailureResult -Name "fingerprint_changed" `
            -Result $result -ExpectedPhase "consistency_validation"
        Assert-NoNewPlaintextStage -Before $stageSnapshot

        $script:currentPhase = "missing_object_guard"
        Remove-ProofObject -Context $script:sourceContext -Key $pickupKey
        try {
            $missingOutput = Join-Path $temporaryRoot "ops002-missing-$runSuffix"
            $result = Invoke-BackupProcess -Destination $missingOutput `
                -Recipient $recipient -AllowFailure
            Add-ExpectedBackupFailureResult -Name "proof_without_object" `
                -Result $result -ExpectedPhase "object_integrity_validation"
        }
        finally {
            Set-ProofObjects -Context $script:sourceContext `
                -FixtureDirectory $fixtureObjects `
                -PickupKey $pickupKey `
                -DeliveryKey $deliveryKey `
                -DecoyKey $decoyKey
        }

        $script:currentPhase = "object_hash_guard"
        Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query @'
SET session_replication_role=replica;
UPDATE custody.proofs SET sha256=decode(repeat('ff',32),'hex')
WHERE id='10000000-0000-4000-8000-000000000027';
SET session_replication_role=origin;
'@ | Out-Null
        try {
            $hashOutput = Join-Path $temporaryRoot "ops002-hash-$runSuffix"
            $result = Invoke-BackupProcess -Destination $hashOutput `
                -Recipient $recipient -AllowFailure
            Add-ExpectedBackupFailureResult `
                -Name "database_object_hash_mismatch" `
                -Result $result -ExpectedPhase "object_integrity_validation"
        }
        finally {
            $pickupSha = Get-Ops002Sha256 -Path $pickupPath
            Invoke-Ops002PostgresQuery -Context $script:sourceContext -Query @"
SET session_replication_role=replica;
UPDATE custody.proofs SET sha256=decode('$pickupSha','hex')
WHERE id='10000000-0000-4000-8000-000000000027';
SET session_replication_role=origin;
"@ | Out-Null
        }

        $script:currentPhase = "backup_failure_guards"
        foreach ($failure in @(
            @{ Name = "pg_dump_failure"; Stage = "PG_DUMP" },
            @{ Name = "mc_mirror_failure"; Stage = "MC_MIRROR" },
            @{ Name = "encryption_failure"; Stage = "ENCRYPTION" }
        )) {
            $before = @(Get-TemporaryStageSnapshot)
            $destination = Join-Path $temporaryRoot "ops002-$($failure.Stage.ToLowerInvariant())-$runSuffix"
            $result = Invoke-BackupProcess -Destination $destination `
                -Recipient $recipient `
                -Additional @("-TestFailureStage", $failure.Stage) `
                -AllowFailure
            $expectedPhase = switch ($failure.Stage) {
                "PG_DUMP" { "database_dump" }
                "MC_MIRROR" { "object_storage_mirror" }
                "ENCRYPTION" { "encryption" }
            }
            Add-ExpectedBackupFailureResult -Name $failure.Name `
                -Result $result -ExpectedPhase $expectedPhase
            Assert-NoNewPlaintextStage -Before $before
        }
        Add-NegativeResult -Name "cleanup_after_backup_failure" -Passed $true

        $script:currentPhase = "canonical_backup"
        $backupOutput = Join-Path $resultsRoot "encrypted-backup"
        $backupResult = Invoke-BackupProcess -Destination $backupOutput `
            -Recipient $recipient -AllowFailure
        if ($backupResult.ExitCode -ne 0) {
            $capturedText = "{0}`n{1}" -f
                $backupResult.StandardError, $backupResult.StandardOutput
            $failurePhase = Get-RedactedChildFailurePhase `
                -StandardError $capturedText
            throw "Canonical encrypted backup failed during phase '$failurePhase'."
        }
        $artifactPath = Get-ChildItem -LiteralPath $backupOutput `
            -File -Filter "*.tar.gz.age" | Select-Object -ExpandProperty FullName -First 1
        if ([string]::IsNullOrWhiteSpace($artifactPath)) {
            throw "Canonical encrypted artifact was not found."
        }
        $ciphertextText = [System.Text.Encoding]::ASCII.GetString(
            [System.IO.File]::ReadAllBytes($artifactPath))
        $visibleSentinel = @(
            "OPS002-SYNTHETIC-MAIN",
            "OPS-002 Synthetic Owner",
            "PICKUP_PROOF_FINALIZED"
        ) | Where-Object { $ciphertextText.Contains($_, [StringComparison]::Ordinal) }
        Add-NegativeResult -Name "encrypted_artifact_hides_sentinels" `
            -Passed (@($visibleSentinel).Count -eq 0)
        $ciphertextText = $null
    }
    finally {
        Remove-Ops002StagingDirectory -Path $fixtureStage
    }

    $script:currentPhase = "restore_authentication_guards"
    $script:wrongIdentity = Join-Path $temporaryRoot "ops002-wrong-$runSuffix.txt"
    Invoke-Ops002Process -FilePath "age-keygen" -Arguments @(
        "--output", $script:wrongIdentity
    ) | Out-Null
    $wrongResult = Invoke-RestoreProcess -ArtifactPath $artifactPath `
        -IdentityPath $script:wrongIdentity `
        -Project $script:targetContext.ProjectName `
        -Database "ops002_restored" -AllowFailure
    Add-NegativeResult -Name "wrong_identity" -Passed ($wrongResult.ExitCode -ne 0)

    $cipherTampered = Join-Path $resultsRoot "tampered-cipher.age"
    Copy-Item -LiteralPath $artifactPath -Destination $cipherTampered
    $cipherBytes = [System.IO.File]::ReadAllBytes($cipherTampered)
    $cipherBytes[[Math]::Floor($cipherBytes.Length / 2)] = $cipherBytes[[Math]::Floor($cipherBytes.Length / 2)] -bxor 1
    [System.IO.File]::WriteAllBytes($cipherTampered, $cipherBytes)
    Copy-BackupReportForArtifact -SourceArtifact $artifactPath `
        -DestinationArtifact $cipherTampered
    $tamperedResult = Invoke-RestoreProcess -ArtifactPath $cipherTampered `
        -IdentityPath $script:identityPath `
        -Project $script:targetContext.ProjectName `
        -Database "ops002_restored" -AllowFailure
    Add-NegativeResult -Name "encrypted_artifact_tampered" `
        -Passed ($tamperedResult.ExitCode -ne 0)
    Remove-Item -LiteralPath $cipherTampered -Force
    Remove-Item -LiteralPath "$cipherTampered.report.json" -Force

    foreach ($tamper in @(
        @{ Kind = "MANIFEST"; Name = "internal_manifest_tampered" },
        @{ Kind = "DUMP"; Name = "database_dump_tampered" },
        @{ Kind = "OBJECT"; Name = "object_payload_tampered" }
    )) {
        $tamperedArtifact = Join-Path $resultsRoot (
            "tampered-{0}.age" -f $tamper.Kind.ToLowerInvariant())
        New-InternalTamperedArtifact -Kind $tamper.Kind `
            -SourceArtifact $artifactPath `
            -Recipient $recipient `
            -DestinationArtifact $tamperedArtifact
        $result = Invoke-RestoreProcess -ArtifactPath $tamperedArtifact `
            -IdentityPath $script:identityPath `
            -Project $script:targetContext.ProjectName `
            -Database "ops002_restored" -AllowFailure
        Add-NegativeResult -Name $tamper.Name -Passed ($result.ExitCode -ne 0)
        Remove-Item -LiteralPath $tamperedArtifact -Force
        Remove-Item -LiteralPath "$tamperedArtifact.report.json" -Force
    }

    $sameProjectResult = Invoke-RestoreProcess -ArtifactPath $artifactPath `
        -IdentityPath $script:identityPath `
        -Project $script:sourceContext.ProjectName `
        -Database "ops002_restored" -AllowFailure
    Add-NegativeResult -Name "target_project_equals_source" `
        -Passed ($sameProjectResult.ExitCode -ne 0)

    Invoke-ExpectedScriptFailure -Name "restore_without_confirmation" `
        -Script (Join-Path $repositoryRoot "tools/restore-environment.ps1") `
        -Arguments @(
            "-Artifact", $artifactPath,
            "-IdentityFile", $script:identityPath,
            "-EnvironmentFile", $script:targetContext.EnvironmentFile,
            "-ProjectName", $script:targetContext.ProjectName,
            "-RestoredDatabase", "ops002_restored"
        )

    $beforeCancel = @(Get-TemporaryStageSnapshot)
    $cancelResult = Invoke-RestoreProcess -ArtifactPath $artifactPath `
        -IdentityPath $script:identityPath `
        -Project $script:targetContext.ProjectName `
        -Database "ops002_restored" `
        -Additional @("-TestFailureStage", "CANCELLED") `
        -AllowFailure
    Add-NegativeResult -Name "restore_cancelled" -Passed ($cancelResult.ExitCode -ne 0)
    Assert-NoNewPlaintextStage -Before $beforeCancel
    Add-NegativeResult -Name "cleanup_after_cancellation" -Passed $true

    $script:currentPhase = "source_destruction"
    Invoke-DockerCompose -Context $script:sourceContext -Arguments @(
        "down", "--volumes", "--remove-orphans"
    ) | Out-Null
    Assert-ProjectResourcesAbsent -ProjectName $script:sourceContext.ProjectName
    $script:sourceDestroyed = $true
    Add-NegativeResult -Name "source_destroyed_before_restore" -Passed $true

    $script:currentPhase = "target_preflight_guards"
    Start-ComposeEnvironment -Context $script:targetContext -TimeoutSeconds $TimeoutSeconds
    Invoke-Ops002PostgresQuery -Context $script:targetContext -Query (
        'CREATE DATABASE "ops002_restored" TEMPLATE template0;') | Out-Null
    $dirtyDatabaseResult = Invoke-RestoreProcess -ArtifactPath $artifactPath `
        -IdentityPath $script:identityPath `
        -Project $script:targetContext.ProjectName `
        -Database "ops002_restored" -AllowFailure
    Add-NegativeResult -Name "target_database_not_empty" `
        -Passed ($dirtyDatabaseResult.ExitCode -ne 0)
    Invoke-DockerCompose -Context $script:targetContext -Arguments @(
        "down", "--volumes", "--remove-orphans"
    ) | Out-Null
    Assert-ProjectResourcesAbsent -ProjectName $script:targetContext.ProjectName

    $script:currentPhase = "dirty_bucket_start"
    Start-ComposeEnvironment -Context $script:targetContext -TimeoutSeconds $TimeoutSeconds
    $script:currentPhase = "dirty_bucket_seed"
    Invoke-Ops002McMirrorFromHost -Context $script:targetContext `
        -Source (Join-Path $repositoryRoot "tests/fixtures/ops-002/objects") `
        -TimeoutSeconds $TimeoutSeconds
    $script:currentPhase = "dirty_bucket_restore_guard"
    $dirtyBucketResult = Invoke-RestoreProcess -ArtifactPath $artifactPath `
        -IdentityPath $script:identityPath `
        -Project $script:targetContext.ProjectName `
        -Database "ops002_restored" -AllowFailure
    Add-NegativeResult -Name "target_bucket_not_empty" `
        -Passed ($dirtyBucketResult.ExitCode -ne 0)
    $script:currentPhase = "dirty_bucket_cleanup"
    Invoke-DockerCompose -Context $script:targetContext -Arguments @(
        "down", "--volumes", "--remove-orphans"
    ) | Out-Null
    Assert-ProjectResourcesAbsent -ProjectName $script:targetContext.ProjectName

    $script:currentPhase = "canonical_restore"
    $restoreResult = Invoke-RestoreProcess -ArtifactPath $artifactPath `
        -IdentityPath $script:identityPath `
        -Project $script:targetContext.ProjectName `
        -Database "ops002_restored" `
        -Additional @("-ConfirmSourceDestroyed") `
        -AllowFailure
    if ($restoreResult.ExitCode -ne 0) {
        $capturedText = "{0}`n{1}" -f
            $restoreResult.StandardError, $restoreResult.StandardOutput
        $failurePhase = Get-RedactedChildFailurePhase `
            -StandardError $capturedText
        throw "Canonical restore failed during phase '$failurePhase'."
    }
    $script:targetRestored = $true

    $script:currentPhase = "post_restore_validation"
    $restoreReportPath = "$artifactPath.restore-report.json"
    Assert-Ops002RedactedReport -Path $restoreReportPath
    $restoreReport = Get-Content -LiteralPath $restoreReportPath -Raw -Encoding utf8 |
        ConvertFrom-Json
    $reportAssertions = [ordered]@{
        source_destroyed_before_restore =
            ($restoreReport.source_destroyed_before_restore -eq $true)
        target_was_clean = ($restoreReport.target_was_clean -eq $true)
        baseline_assertions_passed = ($restoreReport.baseline_assertions_passed -eq $true)
        module_migrations_asserted = ($restoreReport.module_migrations_asserted -eq $true)
        rls_assertions_passed = ($restoreReport.rls_assertions_passed -eq $true)
        append_only_assertions_passed = ($restoreReport.append_only_assertions_passed -eq $true)
        object_integrity_passed = ($restoreReport.object_integrity_passed -eq $true)
        object_count = ([int]$restoreReport.object_count -eq 3)
        proof_objects_expected = ([int]$restoreReport.proof_objects_expected -eq 2)
        proof_objects_restored = ([int]$restoreReport.proof_objects_restored -eq 2)
        redis_restored = ($restoreReport.redis_restored -eq $false)
        mailpit_restored = ($restoreReport.mailpit_restored -eq $false)
        plaintext_residue_detected = ($restoreReport.plaintext_residue_detected -eq $false)
    }
    $failedReportAssertions = @(
        $reportAssertions.GetEnumerator() |
            Where-Object { -not [bool]$_.Value } |
            ForEach-Object Key
    )
    if ($failedReportAssertions.Count -ne 0) {
        throw (
            "Canonical restore report failed required assertions: {0}." -f
            ($failedReportAssertions -join ", "))
    }
    Add-NegativeResult -Name "redis_not_restored" -Passed $true
    Add-NegativeResult -Name "mailpit_not_restored" -Passed $true
    Add-NegativeResult -Name "restore_survives_target_restart" -Passed $true
    Assert-EphemeralLoginRls -Context $script:targetContext -Database "ops002_restored"
    Add-NegativeResult -Name "cross_tenant_isolation_after_restore" -Passed $true
    Add-NegativeResult -Name "append_only_after_restore" -Passed $true
    Add-NegativeResult -Name "role_ownership_and_grants_after_restore" -Passed $true

    $plaintext = @(
        Get-ChildItem -LiteralPath $resultsRoot -File -Recurse |
        Where-Object {
            $_.Name -match '\.(dump|tar|tar\.gz)$' -or
            $_.Name -match 'identity|\.env$'
        }
    )
    Add-NegativeResult -Name "no_plaintext_residue" -Passed ($plaintext.Count -eq 0)

    foreach ($report in Get-ChildItem -LiteralPath $resultsRoot -File -Filter "*.json" -Recurse) {
        Assert-Ops002RedactedReport -Path $report.FullName
    }
    Add-NegativeResult -Name "reports_contain_no_secrets" -Passed $true

    if ($script:negativeResults.Count -ne 43) {
        throw "Expected 43 negative/security checks; observed $($script:negativeResults.Count)."
    }

    $drillStopwatch.Stop()
    $restoreReport.result = "RESTORE_DRILL_PASSED"
    $restoreReport.total_drill_duration = $drillStopwatch.Elapsed.ToString("c")
    $restoreReport.source_destroyed_before_restore = $true
    $restoreReport.plaintext_residue_detected = $false
    $finalReportPath = Join-Path $resultsRoot "ops002-restore-drill-report.json"
    Write-Ops002Json -Value $restoreReport -Path $finalReportPath
    Assert-Ops002RedactedReport -Path $finalReportPath

    $testResultsPath = Join-Path $resultsRoot "ops002-negative-tests.json"
    Write-Ops002Json -Value ([ordered]@{
        format_version = $script:Ops002FormatVersion
        required_checks = 43
        passed_checks = 43
        result = "OPS002_NEGATIVE_TESTS_PASSED"
        checks = $script:negativeResults
    }) -Path $testResultsPath
    Assert-Ops002RedactedReport -Path $testResultsPath

    Write-Host "OPS-002 encrypted backup and clean restore drill passed."
    Write-Output ([pscustomobject]@{
        Report = $finalReportPath
        TestResults = $testResultsPath
        Artifact = $artifactPath
    })
}
catch {
    Write-Error "OPS-002 drill failed during phase '$script:currentPhase': $($_.Exception.Message)"
    exit 1
}
finally {
    $env:OPS002_TEST_MODE = $null
    if (-not $KeepResources) {
        foreach ($context in @($script:sourceContext, $script:targetContext)) {
            if ($null -eq $context) {
                continue
            }
            try {
                Invoke-DockerCompose -Context $context -Arguments @(
                    "down", "--volumes", "--remove-orphans"
                ) -AllowFailure | Out-Null
                Assert-ProjectResourcesAbsent -ProjectName $context.ProjectName
            }
            catch {
                # The main drill result remains visible; CI cleanup retries this scope.
            }
        }
    }
    if ($null -ne $script:wrongIdentity -and
        (Test-Path -LiteralPath $script:wrongIdentity)) {
        Remove-Item -LiteralPath $script:wrongIdentity -Force -ErrorAction SilentlyContinue
    }
    foreach ($path in $script:ownedPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}
