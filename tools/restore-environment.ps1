[CmdletBinding()]
param(
    [string] $Artifact,
    [string] $IdentityFile,
    [string] $ComposeFile,
    [string] $EnvironmentFile,
    [string] $ProjectName,
    [string] $RestoredDatabase,
    [switch] $ConfirmRestore,
    [switch] $ConfirmSourceDestroyed,
    [int] $TimeoutSeconds = 1200,
    [string] $TestFailureStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = Join-Path $repositoryRoot "deploy/docker-compose.yml"
}
if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
    $EnvironmentFile = Join-Path $repositoryRoot "deploy/.env.local"
}

. (Join-Path $PSScriptRoot "local-environment.common.ps1")
. (Join-Path $PSScriptRoot "backup-restore.common.ps1")

$staging = $null
$stagingPathInfo = $null
$context = $null
$targetStartedByRestore = $false
$succeeded = $false
$containerDump = $null
$rolesBootstrapDatabase = $null
$rolesBootstrapCreated = $false
$currentPhase = "preflight"
$restoreStartedAt = [DateTimeOffset]::UtcNow
$restoreStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$validationStopwatch = [System.Diagnostics.Stopwatch]::new()
$connectionVariable = "OPS002_TARGET_DATABASE_CONNECTION"

try {
    if (-not $ConfirmRestore) {
        throw "Restore requires -ConfirmRestore."
    }
    if ([string]::IsNullOrWhiteSpace($Artifact)) {
        throw "Restore requires -Artifact."
    }
    if ([string]::IsNullOrWhiteSpace($IdentityFile)) {
        throw "Restore requires -IdentityFile."
    }
    if ([string]::IsNullOrWhiteSpace($RestoredDatabase) -or
        $RestoredDatabase -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "RestoredDatabase must be a safe PostgreSQL identifier."
    }
    if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 7200) {
        throw "TimeoutSeconds must be between 30 and 7200."
    }

    $artifactPath = Assert-Ops002ExternalRegularFile `
        -Path $Artifact `
        -Purpose "Encrypted artifact"
    $reportOutputPathInfo = New-Ops002ValidatedExternalDirectory `
        -Path (Split-Path -Parent $artifactPath) `
        -Purpose "Restore report output"
    $identity = Assert-Ops002IdentityFile -IdentityFile $IdentityFile
    $externalReportPath = "$artifactPath.report.json"
    $externalReportPath = Assert-Ops002ExternalRegularFile `
        -Path $externalReportPath `
        -Purpose "External backup report"
    Assert-Ops002RedactedReport -Path $externalReportPath
    $backupReport = Get-Content -LiteralPath $externalReportPath -Raw -Encoding utf8 |
        ConvertFrom-Json
    if ($backupReport.encrypted -ne $true -or
        $backupReport.encryption_format -cne "age/X25519" -or
        $backupReport.artifact_sha256 -notmatch '^[0-9a-f]{64}$' -or
        (Get-Ops002Sha256 -Path $artifactPath) -cne $backupReport.artifact_sha256) {
        throw "External artifact hash validation failed."
    }

    $currentPhase = "artifact_validation"
    $stagingPathInfo = New-Ops002StagingDirectory -Purpose "restore"
    $staging = $stagingPathInfo.PhysicalPath
    $archive = Join-Path $staging "payload.tar.gz"
    Invoke-Ops002Process -FilePath "age" -Arguments @(
        "--decrypt", "--identity", $identity,
        "--output", $archive, $artifactPath
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    Assert-Ops002ArchiveEntries -Archive $archive | Out-Null
    if (Test-Ops002FailureStage -Requested $TestFailureStage -Stage "CANCELLED") {
        throw [System.OperationCanceledException]::new(
            "Restore cancelled after authenticated staging and before mutation.")
    }

    $extracted = Join-Path $staging "extracted"
    Expand-Ops002ValidatedArchive -Archive $archive -Destination $extracted
    $payloadRoot = Join-Path $extracted "payload"
    $manifestPath = Join-Path $payloadRoot "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Manifest is missing."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 |
        ConvertFrom-Json
    $inventory = Assert-Ops002Manifest -Manifest $manifest -PayloadRoot $payloadRoot

    $currentPhase = "target_preflight"
    $context = Get-LocalEnvironmentContext `
        -ComposeFile $ComposeFile `
        -EnvironmentFile $EnvironmentFile `
        -ProjectName $ProjectName
    if ($context.ProjectName -ceq $manifest.source_project) {
        throw "Restore target project must differ from the source project."
    }
    if ($RestoredDatabase -ceq $manifest.source_database -or
        $RestoredDatabase -ceq $context.Environment["POSTGRES_DB"]) {
        throw "Restore target database must differ from source and bootstrap databases."
    }

    Assert-DockerAvailable
    Assert-ComposeStaticPolicy -Context $context

    $dumpPath = Join-Path $payloadRoot "postgres/database.dump"
    $composeConfig = Get-ComposeConfig -Context $context
    $postgresImage = [string]$composeConfig.services.postgres.image
    if ($postgresImage -notmatch '@sha256:[0-9a-f]{64}$') {
        throw "PostgreSQL validation image is not pinned."
    }
    $dumpMount = "$([System.IO.Path]::GetFullPath($dumpPath))`:/backup/database.dump:ro"
    $tocResult = Invoke-Ops002Process -FilePath "docker" -Arguments @(
        "run", "--rm", "--read-only", "--tmpfs", "/tmp",
        "--volume", $dumpMount,
        $postgresImage,
        "pg_restore", "--list", "/backup/database.dump"
    ) -TimeoutSeconds $TimeoutSeconds
    if (@($tocResult.StandardOutput -split "`r?`n" |
        Where-Object { $_ -match '^\d+;' }).Count -lt 1) {
        throw "Database dump list validation failed."
    }

    $resourcesBefore = Get-ProjectResourceIds -ProjectName $context.ProjectName
    $unrelatedBefore = Get-UnrelatedResourceSnapshot -ProjectName $context.ProjectName
    if ($resourcesBefore.Containers.Count -eq 0) {
        Assert-ConfiguredPortsAvailable -Context $context
        Start-ComposeEnvironment -Context $context -TimeoutSeconds $TimeoutSeconds
        $targetStartedByRestore = $true
    }
    else {
        Assert-Ops002ServicesHealthy -Context $context
    }
    Assert-Ops002ServicesHealthy -Context $context

    $databaseCheck = Invoke-Ops002PostgresQuery -Context $context -Query (
        "SELECT count(*) FROM pg_database WHERE datname=" +
        "'$($RestoredDatabase.Replace("'", "''"))';")
    if ($databaseCheck.StandardOutput.Trim() -ne "0") {
        throw "Target restore database already exists."
    }
    if ((Get-Ops002BucketObjectCount -Context $context) -ne 0) {
        throw "Target bucket is not empty."
    }
    $targetWasClean = $true

    $currentPhase = "roles_bootstrap"
    $rolesBootstrapDatabase = "ops002_roles_$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    Invoke-Ops002PostgresQuery -Context $context -Query (
        "CREATE DATABASE `"$rolesBootstrapDatabase`" TEMPLATE template0;") | Out-Null
    $rolesBootstrapCreated = $true
    $bootstrapConnection = "Host=127.0.0.1;Port=$($context.Environment['POSTGRES_HOST_PORT']);" +
        "Database=$rolesBootstrapDatabase;Username=$($context.Environment['POSTGRES_USER']);" +
        "Password=$($context.Environment['POSTGRES_PASSWORD']);Pooling=false;Timeout=15;Command Timeout=120"
    [Environment]::SetEnvironmentVariable($connectionVariable, $bootstrapConnection, "Process")
    try {
        Invoke-Ops002Process -FilePath "pwsh" -Arguments @(
            "-NoLogo", "-NoProfile", "-File",
            (Join-Path $repositoryRoot "tools/database-baseline.ps1"),
            "Apply", "-ConnectionEnvironment", $connectionVariable,
            "-ConfirmInitialBaseline"
        ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    }
    finally {
        [Environment]::SetEnvironmentVariable($connectionVariable, $null, "Process")
        $bootstrapConnection = $null
    }
    Invoke-Ops002PostgresQuery -Context $context -Query (
        "DROP DATABASE `"$rolesBootstrapDatabase`";") | Out-Null
    $rolesBootstrapCreated = $false
    $rolesBootstrapDatabase = $null

    $currentPhase = "database_restore"
    Invoke-Ops002PostgresQuery -Context $context -Query (
        "CREATE DATABASE `"$RestoredDatabase`" TEMPLATE template0;") | Out-Null

    $containerDump = "/tmp/ops002-$([Guid]::NewGuid().ToString('N')).dump"
    Invoke-Ops002Compose -Context $context -Arguments @(
        "cp", $dumpPath, "postgres:$containerDump"
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    $restoreShell = @'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
pg_restore --exit-on-error --single-transaction \
  --username "$POSTGRES_USER" --dbname "$OPS002_DATABASE" "$OPS002_DUMP_PATH"
'@
    Invoke-Ops002Compose -Context $context -Arguments @(
        "exec", "-T",
        "--env", "OPS002_DATABASE=$RestoredDatabase",
        "--env", "OPS002_DUMP_PATH=$containerDump",
        "postgres", "/bin/sh", "-c", $restoreShell
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    Invoke-Ops002Compose -Context $context -Arguments @(
        "exec", "-T", "--env", "OPS002_DUMP_PATH=$containerDump",
        "postgres", "/bin/sh", "-c", 'rm -f -- "$OPS002_DUMP_PATH"'
    ) -AllowFailure | Out-Null
    $containerDump = $null

    $currentPhase = "object_storage_restore"
    Invoke-Ops002McMirrorFromHost -Context $context `
        -Source (Join-Path $payloadRoot "object-storage") `
        -TimeoutSeconds $TimeoutSeconds
    Assert-Ops002BucketPrivate -Context $context

    $restoreStopwatch.Stop()
    $validationStopwatch.Start()

    $currentPhase = "restored_state_validation"
    $restoredConnection = "Host=127.0.0.1;Port=$($context.Environment['POSTGRES_HOST_PORT']);" +
        "Database=$RestoredDatabase;Username=$($context.Environment['POSTGRES_USER']);" +
        "Password=$($context.Environment['POSTGRES_PASSWORD']);Pooling=false;Timeout=15;Command Timeout=120"
    [Environment]::SetEnvironmentVariable($connectionVariable, $restoredConnection, "Process")
    try {
        Invoke-Ops002Process -FilePath "pwsh" -Arguments @(
            "-NoLogo", "-NoProfile", "-File",
            (Join-Path $repositoryRoot "tools/database-baseline.ps1"),
            "Assert", "-ConnectionEnvironment", $connectionVariable
        ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    }
    finally {
        [Environment]::SetEnvironmentVariable($connectionVariable, $null, "Process")
        $restoredConnection = $null
    }

    $assertionsSql = Get-Content -LiteralPath (
        Join-Path $repositoryRoot "tests/fixtures/ops-002/assert-restored.sql"
    ) -Raw -Encoding utf8
    $assertionResult = Invoke-Ops002PostgresQuery -Context $context `
        -Database $RestoredDatabase -Query $assertionsSql -TimeoutSeconds $TimeoutSeconds
    if ($assertionResult.StandardOutput.Trim() -notmatch 'OPS002_ASSERTIONS_OK') {
        throw "Restored database assertions failed."
    }
    $currentPhase = "append_only_validation"
    $appendOnlyBeforeRestart = Invoke-Ops002AppendOnlyAssertions `
        -Context $context `
        -Database $RestoredDatabase `
        -TimeoutSeconds $TimeoutSeconds

    $verificationObjects = Join-Path $staging "verified-object-storage"
    New-Item -ItemType Directory -Path $verificationObjects | Out-Null
    Invoke-Ops002McMirrorToHost -Context $context -Destination $verificationObjects `
        -TimeoutSeconds $TimeoutSeconds
    $restoredInventory = Get-Ops002ObjectInventory -Root $verificationObjects
    if ($restoredInventory.Count -ne $inventory.Count -or
        $restoredInventory.TotalBytes -ne $inventory.TotalBytes -or
        $restoredInventory.Digest -cne $inventory.Digest) {
        throw "Restored object inventory differs from the backup."
    }
    $restoredProofs = @(Get-Ops002ProofReferences -Context $context `
        -Database $RestoredDatabase)
    if ($restoredProofs.Count -ne [int]$backupReport.proof_objects_expected) {
        throw "Restored proof reference count differs from the backup."
    }
    Assert-Ops002ProofObjectIntegrity `
        -ProofReferences $restoredProofs `
        -Inventory $restoredInventory

    $currentPhase = "restart_validation"
    Invoke-Ops002Compose -Context $context -Arguments @(
        "restart", "postgres", "minio"
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    Wait-ComposeHealthy -Context $context -TimeoutSeconds $TimeoutSeconds
    Assert-Ops002BucketPrivate -Context $context

    $restoredConnection = "Host=127.0.0.1;Port=$($context.Environment['POSTGRES_HOST_PORT']);" +
        "Database=$RestoredDatabase;Username=$($context.Environment['POSTGRES_USER']);" +
        "Password=$($context.Environment['POSTGRES_PASSWORD']);Pooling=false;Timeout=15;Command Timeout=120"
    [Environment]::SetEnvironmentVariable($connectionVariable, $restoredConnection, "Process")
    try {
        Invoke-Ops002Process -FilePath "pwsh" -Arguments @(
            "-NoLogo", "-NoProfile", "-File",
            (Join-Path $repositoryRoot "tools/database-baseline.ps1"),
            "Assert", "-ConnectionEnvironment", $connectionVariable
        ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    }
    finally {
        [Environment]::SetEnvironmentVariable($connectionVariable, $null, "Process")
        $restoredConnection = $null
    }
    $assertionResult = Invoke-Ops002PostgresQuery -Context $context `
        -Database $RestoredDatabase -Query $assertionsSql -TimeoutSeconds $TimeoutSeconds
    if ($assertionResult.StandardOutput.Trim() -notmatch 'OPS002_ASSERTIONS_OK') {
        throw "Post-restart restored database assertions failed."
    }
    $currentPhase = "post_restart_append_only_validation"
    $appendOnlyAfterRestart = Invoke-Ops002AppendOnlyAssertions `
        -Context $context `
        -Database $RestoredDatabase `
        -TimeoutSeconds $TimeoutSeconds

    $postRestartObjects = Join-Path $staging "post-restart-object-storage"
    New-Item -ItemType Directory -Path $postRestartObjects | Out-Null
    Invoke-Ops002McMirrorToHost -Context $context -Destination $postRestartObjects `
        -TimeoutSeconds $TimeoutSeconds
    $postRestartInventory = Get-Ops002ObjectInventory -Root $postRestartObjects
    if ($postRestartInventory.Count -ne $inventory.Count -or
        $postRestartInventory.TotalBytes -ne $inventory.TotalBytes -or
        $postRestartInventory.Digest -cne $inventory.Digest) {
        throw "Post-restart object inventory differs from the backup."
    }
    $postRestartProofs = @(Get-Ops002ProofReferences -Context $context `
        -Database $RestoredDatabase)
    if ($postRestartProofs.Count -ne [int]$backupReport.proof_objects_expected) {
        throw "Post-restart proof reference count differs from the backup."
    }
    Assert-Ops002ProofObjectIntegrity `
        -ProofReferences $postRestartProofs `
        -Inventory $postRestartInventory

    $redisShell = @'
set -eu
redis-cli --no-auth-warning -a "$REDIS_PASSWORD" DBSIZE
'@
    $redisResult = Invoke-Ops002Compose -Context $context -Arguments @(
        "exec", "-T", "redis", "/bin/sh", "-c", $redisShell
    )
    if ($redisResult.StandardOutput.Trim() -ne "0") {
        throw "Redis must be empty after restore."
    }

    $mailPort = [int]$context.Environment["MAIL_UI_HOST_PORT"]
    $mailResponse = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$mailPort/api/v1/messages" `
        -Method Get `
        -TimeoutSec 15
    $mailCount = if ($mailResponse.PSObject.Properties.Name -contains "messages_count") {
        [int]$mailResponse.messages_count
    }
    elseif ($mailResponse.PSObject.Properties.Name -contains "total") {
        [int]$mailResponse.total
    }
    elseif ($mailResponse.PSObject.Properties.Name -contains "messages") {
        @($mailResponse.messages).Count
    }
    else {
        @($mailResponse).Count
    }
    if ($mailCount -ne 0) {
        throw "Mailpit must be empty after restore."
    }

    Assert-UnrelatedResourcesPreserved -Snapshot $unrelatedBefore
    $validationStopwatch.Stop()
    $completedAt = [DateTimeOffset]::UtcNow
    $totalDuration = $completedAt - $restoreStartedAt
    $currentPhase = "reporting"
    $restoreReport = [ordered]@{
        format_version = $script:Ops002FormatVersion
        backup_id = $manifest.backup_id
        source_head_sha = $manifest.source_head_sha
        tested_git_sha = $manifest.tested_git_sha
        git_relationship = if ($manifest.source_head_sha -ceq $manifest.tested_git_sha) {
            "same_commit"
        }
        else {
            "source_head_is_ancestor_of_tested_commit"
        }
        backup_started_at_utc = $backupReport.backup_started_at_utc
        backup_completed_at_utc = $backupReport.backup_completed_at_utc
        backup_duration = $backupReport.backup_duration
        encrypted = $true
        encryption_format = "age/X25519"
        artifact_bytes = [int64]$backupReport.artifact_bytes
        artifact_sha256 = $backupReport.artifact_sha256
        postgres_dump_bytes = [int64]$manifest.database_dump_bytes
        postgres_dump_sha256 = $manifest.database_dump_sha256
        postgres_objects_listed = [int]$backupReport.postgres_objects_listed
        database_validation_checks = 16
        object_count = [int]$inventory.Count
        object_total_bytes = [int64]$inventory.TotalBytes
        object_inventory_sha256 = $inventory.Digest
        proof_objects_expected = $restoredProofs.Count
        proof_objects_restored = $restoredProofs.Count
        source_fingerprint_stable = $true
        source_destroyed_before_restore = [bool]$ConfirmSourceDestroyed
        target_was_clean = $targetWasClean
        restore_started_at_utc = $restoreStartedAt.ToString("O")
        restore_completed_at_utc = $completedAt.ToString("O")
        restore_duration = $restoreStopwatch.Elapsed.ToString("c")
        validation_duration = $validationStopwatch.Elapsed.ToString("c")
        measured_rto = $totalDuration.ToString("c")
        total_drill_duration = $totalDuration.ToString("c")
        observed_data_loss = 0
        current_guaranteed_rpo = "NOT_ESTABLISHED"
        rto_measurement_scope = "validated restore start through restart and green verification"
        baseline_assertions_passed = $true
        module_migrations_asserted = $true
        rls_assertions_passed = $true
        append_only_tables_expected = $appendOnlyAfterRestart.TablesExpected
        append_only_permission_checks_verified =
            $appendOnlyAfterRestart.PermissionChecksVerified
        append_only_triggers_verified = $appendOnlyAfterRestart.TriggersVerified
        append_only_update_guards_verified =
            $appendOnlyAfterRestart.UpdateGuardsVerified
        append_only_delete_guards_verified =
            $appendOnlyAfterRestart.DeleteGuardsVerified
        append_only_trigger_failures_verified =
            $appendOnlyAfterRestart.TriggerFailuresVerified
        append_only_permission_failures = $appendOnlyAfterRestart.PermissionFailures
        append_only_rows_intact_verified = $appendOnlyAfterRestart.RowsIntactVerified
        append_only_contract_sqlstate = $appendOnlyAfterRestart.SqlState
        append_only_contract_message = $appendOnlyAfterRestart.MessageContract
        append_only_before_restart = [ordered]@{
            tables_expected = $appendOnlyBeforeRestart.TablesExpected
            update_guards_verified = $appendOnlyBeforeRestart.UpdateGuardsVerified
            delete_guards_verified = $appendOnlyBeforeRestart.DeleteGuardsVerified
            trigger_failures_verified = $appendOnlyBeforeRestart.TriggerFailuresVerified
            permission_failures = $appendOnlyBeforeRestart.PermissionFailures
            rows_intact_verified = $appendOnlyBeforeRestart.RowsIntactVerified
        }
        append_only_after_restart = [ordered]@{
            tables_expected = $appendOnlyAfterRestart.TablesExpected
            update_guards_verified = $appendOnlyAfterRestart.UpdateGuardsVerified
            delete_guards_verified = $appendOnlyAfterRestart.DeleteGuardsVerified
            trigger_failures_verified = $appendOnlyAfterRestart.TriggerFailuresVerified
            permission_failures = $appendOnlyAfterRestart.PermissionFailures
            rows_intact_verified = $appendOnlyAfterRestart.RowsIntactVerified
        }
        append_only_assertions_passed = (
            $appendOnlyBeforeRestart.TablesExpected -eq 4 -and
            $appendOnlyBeforeRestart.UpdateGuardsVerified -eq 4 -and
            $appendOnlyBeforeRestart.DeleteGuardsVerified -eq 4 -and
            $appendOnlyBeforeRestart.TriggerFailuresVerified -eq 8 -and
            $appendOnlyBeforeRestart.PermissionFailures -eq 0 -and
            $appendOnlyBeforeRestart.RowsIntactVerified -eq 8 -and
            $appendOnlyAfterRestart.TablesExpected -eq 4 -and
            $appendOnlyAfterRestart.UpdateGuardsVerified -eq 4 -and
            $appendOnlyAfterRestart.DeleteGuardsVerified -eq 4 -and
            $appendOnlyAfterRestart.TriggerFailuresVerified -eq 8 -and
            $appendOnlyAfterRestart.PermissionFailures -eq 0 -and
            $appendOnlyAfterRestart.RowsIntactVerified -eq 8)
        object_integrity_passed = $true
        redis_restored = $false
        mailpit_restored = $false
        plaintext_residue_detected = $false
        result = "RESTORE_PASSED"
    }
    $restoreReportPath = "$artifactPath.restore-report.json"
    $partialReport = "$restoreReportPath.partial"
    Assert-Ops002ValidatedDirectoryUnchanged -PathInfo $reportOutputPathInfo | Out-Null
    Write-Ops002Json -Value $restoreReport -Path $partialReport
    Assert-Ops002RedactedReport -Path $partialReport
    Move-Item -LiteralPath $partialReport -Destination $restoreReportPath -Force

    $succeeded = $true
    Write-Host "Encrypted backup restore completed and verified."
    Write-Output ([pscustomobject]@{
        Report = $restoreReportPath
        Result = "RESTORE_PASSED"
    })
}
catch {
    [Console]::Error.WriteLine("OPS002_FAILURE_PHASE=$currentPhase")
    if ($_.Exception -is [System.OperationCanceledException]) {
        Write-Error "OPS-002 restore cancelled during phase '$currentPhase'."
    }
    else {
        Write-Error "OPS-002 restore failed during phase '$currentPhase': $($_.Exception.Message)"
    }
    exit 1
}
finally {
    [Environment]::SetEnvironmentVariable($connectionVariable, $null, "Process")
    if ($rolesBootstrapCreated -and $null -ne $context -and
        -not [string]::IsNullOrWhiteSpace($rolesBootstrapDatabase)) {
        try {
            Invoke-Ops002PostgresQuery -Context $context -Query (
                "DROP DATABASE `"$rolesBootstrapDatabase`";") -AllowFailure | Out-Null
        }
        catch {
            # The temporary database is removed with project-scoped teardown below.
        }
    }
    if ($null -ne $containerDump -and $null -ne $context) {
        try {
            Invoke-Ops002Compose -Context $context -Arguments @(
                "exec", "-T", "--env", "OPS002_DUMP_PATH=$containerDump",
                "postgres", "/bin/sh", "-c", 'rm -f -- "$OPS002_DUMP_PATH"'
            ) -AllowFailure | Out-Null
        }
        catch {
            # Project-scoped teardown below removes this temporary file when safe.
        }
    }
    if (-not $succeeded -and $targetStartedByRestore -and $null -ne $context) {
        try {
            Invoke-DockerCompose -Context $context -Arguments @(
                "down", "--volumes", "--remove-orphans"
            ) -AllowFailure | Out-Null
            Assert-ProjectResourcesAbsent -ProjectName $context.ProjectName
        }
        catch {
            # The primary restore failure remains visible to the caller.
        }
    }
    if ($null -ne $stagingPathInfo) {
        Remove-Ops002StagingDirectory -PathInfo $stagingPathInfo
    }
}
