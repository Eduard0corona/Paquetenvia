[CmdletBinding()]
param(
    [string] $ComposeFile,
    [string] $EnvironmentFile,
    [string] $ProjectName,
    [string] $Database,
    [string] $OutputDirectory,
    [string] $Recipient,
    [switch] $ConfirmQuiesced,
    [string] $TestIdentityFile,
    [int] $TimeoutSeconds = 900,
    [string] $TestFailureStage,
    [switch] $RequireFixture
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
$context = $null
$output = $null
$outputCreated = $false
$artifactPath = $null
$partialArtifact = $null
$containerDump = $null
$succeeded = $false
$currentPhase = "preflight"
$startedAt = [DateTimeOffset]::UtcNow
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

try {
    if (-not $ConfirmQuiesced) {
        throw "Backup requires -ConfirmQuiesced."
    }
    Assert-Ops002Recipient -Recipient $Recipient | Out-Null
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        throw "Backup requires -OutputDirectory."
    }
    if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 7200) {
        throw "TimeoutSeconds must be between 30 and 7200."
    }
    if (-not [string]::IsNullOrWhiteSpace($Database) -and
        $Database -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        throw "Database must be a safe PostgreSQL identifier."
    }
    if ($RequireFixture -and $env:OPS002_TEST_MODE -cne "true") {
        throw "Fixture validation is available only to the restore drill."
    }

    $output = Assert-Ops002ExternalPath -Path $OutputDirectory -Purpose "Backup output"
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (Test-Ops002PathWithin -Path $output -Parent $temporaryRoot) {
        # Encrypted output may be in RUNNER_TEMP, but it must not be the plaintext
        # staging directory itself. A dedicated child is acceptable.
        $leaf = Split-Path -Leaf $output
        if ($leaf -match '^paquetenvia-ops002-(backup|restore|tamper)-') {
            throw "Backup output cannot be a plaintext staging directory."
        }
    }
    if (Test-Path -LiteralPath $output) {
        throw "Backup output already exists."
    }
    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    $outputCreated = $true

    $currentPhase = "source_validation"
    $context = Get-LocalEnvironmentContext `
        -ComposeFile $ComposeFile `
        -EnvironmentFile $EnvironmentFile `
        -ProjectName $ProjectName
    if (-not [string]::IsNullOrWhiteSpace($Database)) {
        $context.Environment["POSTGRES_DB"] = $Database
    }
    $currentPhase = "docker_preflight"
    Assert-DockerAvailable
    $currentPhase = "compose_policy"
    Assert-ComposeStaticPolicy -Context $context
    $currentPhase = "service_health"
    Assert-Ops002ServicesHealthy -Context $context
    $currentPhase = "bucket_privacy"
    Assert-Ops002BucketPrivate -Context $context
    $currentPhase = "quiescence"
    Assert-Ops002Quiescence -Context $context

    $identity = $null
    if (-not [string]::IsNullOrWhiteSpace($TestIdentityFile)) {
        $identity = Assert-Ops002IdentityFile -IdentityFile $TestIdentityFile
    }

    $currentPhase = "source_fingerprint"
    $fingerprintBefore = Get-Ops002Fingerprint -Context $context
    $currentPhase = "database_versions"
    $versions = Get-Ops002DatabaseVersions -Context $context
    $currentPhase = "migration_histories"
    $histories = Get-Ops002MigrationHistories -Context $context
    if (@($histories.psobject.Properties).Count -ne 8 -or
        @($histories.psobject.Properties.Value | Where-Object { [int]$_ -lt 1 }).Count -ne 0) {
        throw "Expected all eight module migration histories."
    }

    $currentPhase = "fixture_validation"
    if ($RequireFixture) {
        $fixtureResult = Invoke-Ops002PostgresQuery -Context $context -Query @'
SELECT
  (SELECT count(*) FROM organizations.organizations)::text || '|' ||
  (SELECT count(*) FROM identity.users)::text || '|' ||
  (SELECT count(*) FROM orders.orders)::text || '|' ||
  (SELECT count(*) FROM orders.order_events)::text || '|' ||
  (SELECT count(*) FROM orders.order_acceptances)::text || '|' ||
  (SELECT count(*) FROM dispatch.assignments)::text || '|' ||
  (SELECT count(*) FROM custody.proofs)::text || '|' ||
  (SELECT count(*) FROM orders.public_tracking_tokens)::text || '|' ||
  (SELECT count(*) FROM platform.audit_logs)::text || '|' ||
  (SELECT count(*) FROM platform.outbox_events)::text;
'@
        $fixtureCounts = $fixtureResult.StandardOutput.Trim().Split("|")
        $minimums = @(2, 2, 2, 9, 1, 1, 2, 1, 2, 2)
        if ($fixtureCounts.Count -ne $minimums.Count) {
            throw "OPS-002 fixture is incomplete."
        }
        for ($index = 0; $index -lt $minimums.Count; $index++) {
            if ($fixtureCounts[$index] -notmatch '^\d+$' -or
                [int]$fixtureCounts[$index] -lt $minimums[$index]) {
                throw "OPS-002 fixture is incomplete."
            }
        }
    }

    $currentPhase = "database_dump"
    $staging = New-Ops002StagingDirectory -Purpose "backup"
    $payloadRoot = Join-Path $staging "payload"
    $postgresRoot = Join-Path $payloadRoot "postgres"
    $objectsRoot = Join-Path $payloadRoot "object-storage"
    New-Item -ItemType Directory -Path $postgresRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $objectsRoot -Force | Out-Null

    if (Test-Ops002FailureStage -Requested $TestFailureStage -Stage "PG_DUMP") {
        throw "Injected pg_dump failure."
    }

    $currentPhase = "database_dump_creation"
    $containerDump = "/tmp/ops002-$([Guid]::NewGuid().ToString('N')).dump"
    $dumpShell = @'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
pg_dump --format=custom --username "$POSTGRES_USER" --dbname "$OPS002_DATABASE" \
  --file "$OPS002_DUMP_PATH"
test -s "$OPS002_DUMP_PATH"
pg_restore --list "$OPS002_DUMP_PATH"
'@
    $dumpResult = Invoke-Ops002Compose -Context $context -Arguments @(
        "exec", "-T",
        "--env", "OPS002_DATABASE=$($context.Environment['POSTGRES_DB'])",
        "--env", "OPS002_DUMP_PATH=$containerDump",
        "postgres", "/bin/sh", "-c", $dumpShell
    ) -TimeoutSeconds $TimeoutSeconds
    $toc = $dumpResult.StandardOutput
    $currentPhase = "database_dump_toc_validation"
    foreach ($requiredPattern in @(
        '(?m) SCHEMA - custody ',
        '(?m) TABLE orders orders ',
        '(?m) TABLE DATA orders orders ',
        '(?m) TABLE custody proofs ',
        '(?m) TABLE DATA custody proofs ',
        '(?m) FUNCTION security ',
        '(?m) POLICY orders orders .*_tenant ',
        '(?m) TABLE platform __ef_migrations_history_'
    )) {
        if ($toc -notmatch $requiredPattern) {
            throw "Database dump is missing required schema or data entries."
        }
    }
    $postgresObjectsListed = @($toc -split "`r?`n" |
        Where-Object { $_ -match '^\d+;' }).Count
    if ($postgresObjectsListed -lt 1) {
        throw "Database dump object list is empty."
    }

    $dumpPath = Join-Path $postgresRoot "database.dump"
    $currentPhase = "database_dump_copy"
    Invoke-Ops002Compose -Context $context -Arguments @(
        "cp", "postgres:$containerDump", $dumpPath
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    if (-not (Test-Path -LiteralPath $dumpPath -PathType Leaf) -or
        (Get-Item -LiteralPath $dumpPath).Length -le 0) {
        throw "Database dump was not copied to staging."
    }
    Invoke-Ops002Compose -Context $context -Arguments @(
        "exec", "-T", "--env", "OPS002_DUMP_PATH=$containerDump",
        "postgres", "/bin/sh", "-c", 'rm -f -- "$OPS002_DUMP_PATH"'
    ) -AllowFailure | Out-Null
    $containerDump = $null

    $currentPhase = "object_storage_mirror"
    if (Test-Ops002FailureStage -Requested $TestFailureStage -Stage "MC_MIRROR") {
        throw "Injected mc mirror failure."
    }
    Assert-Ops002BucketPrivate -Context $context
    Invoke-Ops002McMirrorToHost -Context $context -Destination $objectsRoot `
        -TimeoutSeconds $TimeoutSeconds
    Assert-Ops002BucketPrivate -Context $context

    $inventory = Get-Ops002ObjectInventory -Root $objectsRoot
    $proofReferences = @(Get-Ops002ProofReferences -Context $context)
    $currentPhase = "object_integrity_validation"
    Assert-Ops002ProofObjectIntegrity `
        -ProofReferences $proofReferences `
        -Inventory $inventory

    $currentPhase = "consistency_validation"
    $fingerprintAfter = Get-Ops002Fingerprint -Context $context
    if (Test-Ops002FailureStage -Requested $TestFailureStage -Stage "FINGERPRINT_CHANGE") {
        $fingerprintAfter = "0" * 64
    }
    if ($fingerprintBefore -cne $fingerprintAfter) {
        throw "SOURCE_CHANGED_DURING_BACKUP"
    }

    $dumpBytes = (Get-Item -LiteralPath $dumpPath).Length
    $dumpSha = Get-Ops002Sha256 -Path $dumpPath
    Write-Ops002Json -Value $inventory.Items `
        -Path (Join-Path $payloadRoot "object-inventory.json")

    $completedAt = [DateTimeOffset]::UtcNow
    $sourceSha = (Invoke-Ops002Process -FilePath "git" -Arguments @(
        "-C", $repositoryRoot, "rev-parse", "HEAD"
    )).StandardOutput.Trim()
    if ($sourceSha -notmatch '^[0-9a-f]{40}$') {
        throw "Unable to resolve source Git SHA."
    }
    $backupId = "ops002-{0}-{1}" -f
        $startedAt.ToString("yyyyMMddTHHmmssZ"),
        ([Guid]::NewGuid().ToString("N").Substring(0, 12))
    $currentPhase = "bundle_packaging"
    $manifest = [ordered]@{
        format_version = $script:Ops002FormatVersion
        backup_id = $backupId
        created_at_utc = $startedAt.ToString("O")
        completed_at_utc = $completedAt.ToString("O")
        source_git_sha = $sourceSha
        normative_version = "v0.6"
        postgresql_version = $versions.PostgreSql
        postgis_version = $versions.PostGis
        database_dump_sha256 = $dumpSha
        database_dump_bytes = [int64]$dumpBytes
        object_count = [int]$inventory.Count
        object_total_bytes = [int64]$inventory.TotalBytes
        object_inventory_sha256 = $inventory.Digest
        migration_histories = $histories
        consistency_fingerprint_before = $fingerprintBefore
        consistency_fingerprint_after = $fingerprintAfter
        quiesced = $true
        source_project = $context.ProjectName
        source_database = $context.Environment["POSTGRES_DB"]
    }
    $manifestPath = Join-Path $payloadRoot "manifest.json"
    Write-Ops002Json -Value $manifest -Path $manifestPath

    $archive = Join-Path $staging "payload.tar.gz"
    Invoke-Ops002Process -FilePath "tar" -Arguments @(
        "-czf", $archive, "-C", $staging, "payload"
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
    Assert-Ops002ArchiveEntries -Archive $archive | Out-Null

    $currentPhase = "encryption"
    if (Test-Ops002FailureStage -Requested $TestFailureStage -Stage "ENCRYPTION") {
        throw "Injected encryption failure."
    }
    $artifactName = "paquetenvia-backup-v1-{0}-{1}.tar.gz.age" -f
        $completedAt.ToString("yyyyMMddTHHmmssZ"),
        ([Guid]::NewGuid().ToString("N").Substring(0, 12))
    $artifactPath = Join-Path $output $artifactName
    $partialArtifact = Join-Path $output (".$artifactName.partial")
    $currentPhase = "encryption_write"
    $ageResult = Invoke-Ops002Process -FilePath "age" -Arguments @(
        "--encrypt", "--recipient", $Recipient,
        "--output", $partialArtifact, $archive
    ) -TimeoutSeconds $TimeoutSeconds -AllowFailure
    if ($ageResult.ExitCode -ne 0) {
        $ageError = $ageResult.StandardError
        $currentPhase = if ($ageError -match '(?i)recipient') {
            "encryption_recipient"
        }
        elseif ($ageError -match '(?i)input|read') {
            "encryption_input"
        }
        elseif ($ageError -match '(?i)output|write|permission|exists') {
            "encryption_output"
        }
        elseif ($ageError -match '(?i)usage|flag|option|argument') {
            "encryption_cli"
        }
        else {
            "encryption_process"
        }
        throw "age encryption command failed."
    }
    if (-not (Test-Path -LiteralPath $partialArtifact -PathType Leaf) -or
        (Get-Item -LiteralPath $partialArtifact).Length -le 0) {
        throw "Encrypted artifact was not produced."
    }

    if ($null -ne $identity) {
        $currentPhase = "controlled_decryption"
        $verificationArchive = Join-Path $staging "verification.tar.gz"
        Invoke-Ops002Process -FilePath "age" -Arguments @(
            "--decrypt", "--identity", $identity,
            "--output", $verificationArchive, $partialArtifact
        ) -TimeoutSeconds $TimeoutSeconds | Out-Null
        if ((Get-Ops002Sha256 -Path $verificationArchive) -cne
            (Get-Ops002Sha256 -Path $archive)) {
            throw "Controlled decryption verification failed."
        }
        Assert-Ops002ArchiveEntries -Archive $verificationArchive | Out-Null
        Remove-Item -LiteralPath $verificationArchive -Force
    }

    $currentPhase = "artifact_publication"
    Move-Item -LiteralPath $partialArtifact -Destination $artifactPath
    $partialArtifact = $null
    $artifactSha = Get-Ops002Sha256 -Path $artifactPath
    $artifactBytes = (Get-Item -LiteralPath $artifactPath).Length
    $artifactCompletedAt = [DateTimeOffset]::UtcNow
    $stopwatch.Stop()

    $currentPhase = "reporting"
    $report = [ordered]@{
        format_version = $script:Ops002FormatVersion
        backup_id = $backupId
        source_git_sha = $sourceSha
        backup_started_at_utc = $startedAt.ToString("O")
        backup_completed_at_utc = $artifactCompletedAt.ToString("O")
        backup_duration = $stopwatch.Elapsed.ToString("c")
        encrypted = $true
        encryption_format = "age/X25519"
        artifact_bytes = [int64]$artifactBytes
        artifact_sha256 = $artifactSha
        postgres_dump_bytes = [int64]$dumpBytes
        postgres_dump_sha256 = $dumpSha
        postgres_objects_listed = $postgresObjectsListed
        database_validation_checks = 8
        object_count = [int]$inventory.Count
        object_total_bytes = [int64]$inventory.TotalBytes
        object_inventory_sha256 = $inventory.Digest
        proof_objects_expected = $proofReferences.Count
        proof_objects_restored = 0
        source_fingerprint_stable = $true
        source_destroyed_before_restore = $false
        target_was_clean = $false
        restore_started_at_utc = $null
        restore_completed_at_utc = $null
        restore_duration = $null
        validation_duration = $null
        total_drill_duration = $null
        observed_data_loss = 0
        current_guaranteed_rpo = "NOT_ESTABLISHED"
        rto_measurement_scope = "NOT_MEASURED_DURING_BACKUP"
        baseline_assertions_passed = $false
        module_migrations_asserted = $false
        rls_assertions_passed = $false
        append_only_assertions_passed = $false
        object_integrity_passed = $true
        redis_restored = $false
        mailpit_restored = $false
        plaintext_residue_detected = $false
        result = "BACKUP_PASSED"
    }
    $reportPath = "$artifactPath.report.json"
    $partialReport = "$reportPath.partial"
    Write-Ops002Json -Value $report -Path $partialReport
    Assert-Ops002RedactedReport -Path $partialReport
    Move-Item -LiteralPath $partialReport -Destination $reportPath

    $succeeded = $true
    Write-Host "Encrypted backup completed."
    Write-Output ([pscustomobject]@{
        Artifact = $artifactPath
        Report = $reportPath
        ArtifactSha256 = $artifactSha
    })
}
catch {
    $failurePhase = $currentPhase
    if ($currentPhase -ceq "quiescence") {
        if ($_.Exception.Message -ceq "OUTBOX_PROCESSING_DURING_BACKUP") {
            $failurePhase = "outbox_quiescence"
        }
        elseif ($_.Exception.Message -ceq "POD_SESSION_TRANSITIONAL_DURING_BACKUP") {
            $failurePhase = "pod_quiescence"
        }
    }
    [Console]::Error.WriteLine("OPS002_FAILURE_PHASE=$failurePhase")
    Write-Error "OPS-002 backup failed during phase '$failurePhase': $($_.Exception.Message)"
    exit 1
}
finally {
    if ($null -ne $containerDump) {
        try {
            if ($null -ne $context) {
                Invoke-Ops002Compose -Context $context -Arguments @(
                    "exec", "-T", "--env", "OPS002_DUMP_PATH=$containerDump",
                    "postgres", "/bin/sh", "-c", 'rm -f -- "$OPS002_DUMP_PATH"'
                ) -AllowFailure | Out-Null
            }
        }
        catch {
            # The main failure remains authoritative; container cleanup is retried by project teardown.
        }
    }
    if ($null -ne $staging) {
        Remove-Ops002StagingDirectory -Path $staging
    }
    if ($null -ne $partialArtifact -and (Test-Path -LiteralPath $partialArtifact)) {
        Remove-Item -LiteralPath $partialArtifact -Force -ErrorAction SilentlyContinue
    }
    if (-not $succeeded -and $null -ne $artifactPath -and
        (Test-Path -LiteralPath $artifactPath)) {
        Remove-Item -LiteralPath $artifactPath -Force -ErrorAction SilentlyContinue
    }
    if (-not $succeeded -and $outputCreated -and $null -ne $output -and
        (Test-Path -LiteralPath $output)) {
        $validatedOutput = Assert-Ops002ExternalPath `
            -Path $output `
            -Purpose "Backup output cleanup"
        if ($validatedOutput -ceq [System.IO.Path]::GetFullPath($output)) {
            Remove-Item -LiteralPath $validatedOutput -Recurse -Force `
                -ErrorAction SilentlyContinue
        }
    }
}
