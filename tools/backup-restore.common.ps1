Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:Ops002FormatVersion = "paquetenvia-backup-v1"
$script:Ops002MaximumArchiveEntries = 10000
$script:Ops002MaximumExpandedBytes = 10GB

function Get-Ops002RepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Get-Ops002FullPath {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [string] $BasePath = (Get-Location).Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Test-Ops002PathWithin {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Parent
    )

    $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $root = [System.IO.Path]::GetFullPath($Parent).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $comparison = if ($IsWindows -or $env:OS -eq "Windows_NT") {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }

    return $candidate.Equals($root, $comparison) -or
        $candidate.StartsWith(
            $root + [System.IO.Path]::DirectorySeparatorChar,
            $comparison) -or
        $candidate.StartsWith(
            $root + [System.IO.Path]::AltDirectorySeparatorChar,
            $comparison)
}

function Assert-Ops002ExternalPath {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Purpose
    )

    $resolved = Get-Ops002FullPath -Path $Path
    $repositoryRoot = Get-Ops002RepositoryRoot
    if (Test-Ops002PathWithin -Path $resolved -Parent $repositoryRoot) {
        throw "$Purpose must be outside the repository."
    }

    return $resolved
}

function Assert-Ops002IdentityFile {
    param([Parameter(Mandatory)] [string] $IdentityFile)

    $resolved = Assert-Ops002ExternalPath -Path $IdentityFile -Purpose "Identity file"
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Identity file was not found."
    }

    if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) {
        $mode = (Get-Item -LiteralPath $resolved).UnixFileMode
        $forbidden = [System.IO.UnixFileMode]::GroupRead -bor
            [System.IO.UnixFileMode]::GroupWrite -bor
            [System.IO.UnixFileMode]::GroupExecute -bor
            [System.IO.UnixFileMode]::OtherRead -bor
            [System.IO.UnixFileMode]::OtherWrite -bor
            [System.IO.UnixFileMode]::OtherExecute
        if (($mode -band $forbidden) -ne 0) {
            throw "Identity file permissions must deny all group and other access."
        }
    }

    return $resolved
}

function Assert-Ops002Recipient {
    param([Parameter(Mandatory)] [string] $Recipient)

    if ([string]::IsNullOrWhiteSpace($Recipient) -or
        $Recipient -cnotmatch '^age1[023456789acdefghjklmnpqrstuvwxyz]{58}$') {
        throw "Recipient must be one valid public age X25519 recipient."
    }

    return $Recipient
}

function New-Ops002StagingDirectory {
    param([Parameter(Mandatory)] [ValidatePattern('^[a-z0-9-]+$')] [string] $Purpose)

    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $repositoryRoot = Get-Ops002RepositoryRoot
    if (Test-Ops002PathWithin -Path $temporaryRoot -Parent $repositoryRoot) {
        throw "System temporary directory must be outside the repository."
    }

    $path = Join-Path $temporaryRoot (
        "paquetenvia-ops002-{0}-{1}" -f $Purpose, [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null

    if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) {
        & chmod 700 -- $path
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            throw "Unable to restrict staging directory permissions."
        }
    }

    return [System.IO.Path]::GetFullPath($path)
}

function Remove-Ops002StagingDirectory {
    param([Parameter(Mandatory)] [string] $Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $leaf = Split-Path -Leaf $resolved
    if (-not (Test-Ops002PathWithin -Path $resolved -Parent $temporaryRoot) -or
        $leaf -notmatch '^paquetenvia-ops002-[a-z0-9-]+-[0-9a-f]{32}$') {
        throw "Refusing to clean an unrecognized staging path."
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $resolved) {
        throw "Plaintext staging cleanup failed."
    }
}

function Get-Ops002Sha256 {
    param([Parameter(Mandatory)] [string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Ops002TextSha256 {
    param([Parameter(Mandatory)] [string] $Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    try {
        return [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Invoke-Ops002Process {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $Arguments = @(),
        [AllowEmptyString()] [string] $InputText,
        [int] $TimeoutSeconds = 300,
        [switch] $AllowFailure
    )

    $command = Get-Command $FilePath -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required executable '$FilePath' is unavailable."
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $command.Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $PSBoundParameters.ContainsKey("InputText")
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Unable to start required executable '$FilePath'."
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if ($PSBoundParameters.ContainsKey("InputText")) {
            $process.StandardInput.Write($InputText)
            $process.StandardInput.Close()
        }

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
            }
            catch {
                # The process may have exited between the timeout and Kill.
            }
            throw "Operation timed out after $TimeoutSeconds seconds."
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        $result = [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutput
            StandardError = $standardError
        }
        if ($result.ExitCode -ne 0 -and -not $AllowFailure) {
            throw "External operation failed with exit code $($result.ExitCode)."
        }

        return $result
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-Ops002Compose {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [AllowEmptyString()] [string] $InputText,
        [int] $TimeoutSeconds = 300,
        [switch] $AllowFailure
    )

    $allArguments = @($Context.DockerArguments) + $Arguments
    $parameters = @{
        FilePath = "docker"
        Arguments = $allArguments
        TimeoutSeconds = $TimeoutSeconds
        AllowFailure = $AllowFailure
    }
    if ($PSBoundParameters.ContainsKey("InputText")) {
        $parameters.InputText = $InputText
    }

    return Invoke-Ops002Process @parameters
}

function Invoke-Ops002PostgresQuery {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Query,
        [string] $Database,
        [int] $TimeoutSeconds = 120,
        [switch] $AllowFailure
    )

    $targetDatabase = if ([string]::IsNullOrWhiteSpace($Database)) {
        $Context.Environment["POSTGRES_DB"]
    }
    else {
        $Database
    }
    if ($targetDatabase -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$') {
        throw "Database name contains unsupported characters."
    }

    $shell = @'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
exec psql --no-psqlrc --quiet --tuples-only --no-align --set ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" --dbname "$OPS002_DATABASE"
'@
    $arguments = @(
        "exec", "-T",
        "--env", "OPS002_DATABASE=$targetDatabase",
        "postgres", "/bin/sh", "-c", $shell
    )
    return Invoke-Ops002Compose -Context $Context -Arguments $arguments `
        -InputText $Query -TimeoutSeconds $TimeoutSeconds -AllowFailure:$AllowFailure
}

function Assert-Ops002ServicesHealthy {
    param([Parameter(Mandatory)] $Context)

    Wait-ComposeHealthy -Context $Context -TimeoutSeconds 30
    $states = @(Get-ComposeServiceState -Context $Context)
    foreach ($required in @("postgres", "minio")) {
        $service = $states | Where-Object { $_.Service -eq $required } | Select-Object -First 1
        if ($null -eq $service -or
            $service.State -ne "running" -or
            $service.Health -ne "healthy") {
            throw "Required persistent service is unhealthy."
        }
    }
}

function Get-Ops002Fingerprint {
    param([Parameter(Mandatory)] $Context)

    $query = @'
SELECT encode(extensions.digest(convert_to(jsonb_build_object(
  'orders', (SELECT jsonb_build_array(count(*),coalesce(max(version),0),coalesce(max(updated_at),'epoch'::timestamptz)) FROM orders.orders),
  'events', (SELECT jsonb_build_array(count(*),coalesce(max(aggregate_version),0),coalesce(max(occurred_at),'epoch'::timestamptz)) FROM orders.order_events),
  'acceptances', (SELECT jsonb_build_array(count(*),coalesce(max(recorded_at_server),'epoch'::timestamptz)) FROM orders.order_acceptances),
  'assignments', (SELECT jsonb_build_array(count(*),coalesce(max(created_at),'epoch'::timestamptz),md5(coalesce(string_agg(status,',' ORDER BY id),''))) FROM dispatch.assignments),
  'proofs', (SELECT jsonb_build_array(count(*),coalesce(sum(size_bytes),0),coalesce(max(created_at),'epoch'::timestamptz)) FROM custody.proofs),
  'pod_sessions', (SELECT jsonb_build_array(count(*),coalesce(max(updated_at),'epoch'::timestamptz),md5(coalesce(string_agg(status,',' ORDER BY id),''))) FROM custody.proof_upload_sessions),
  'tracking', (SELECT jsonb_build_array(count(*),coalesce(max(created_at),'epoch'::timestamptz)) FROM orders.public_tracking_tokens),
  'audit', (SELECT jsonb_build_array(count(*),coalesce(max(occurred_at),'epoch'::timestamptz)) FROM platform.audit_logs),
  'outbox', (SELECT jsonb_build_array(count(*),coalesce(max(created_at),'epoch'::timestamptz),md5(coalesce(string_agg(status,',' ORDER BY id),''))) FROM platform.outbox_events),
  'location_outbox', (SELECT jsonb_build_array(count(*),coalesce(max(created_at),'epoch'::timestamptz),md5(coalesce(string_agg(status,',' ORDER BY id),''))) FROM platform.location_outbox_events)
)::text,'UTF8'),'sha256'),'hex');
'@
    $result = Invoke-Ops002PostgresQuery -Context $Context -Query $query
    $fingerprint = $result.StandardOutput.Trim()
    if ($fingerprint -notmatch '^[0-9a-f]{64}$') {
        throw "Unable to calculate the source consistency fingerprint."
    }

    return $fingerprint
}

function Assert-Ops002Quiescence {
    param([Parameter(Mandatory)] $Context)

    $query = @'
SELECT
  (SELECT count(*) FROM platform.outbox_events WHERE status='PROCESSING')::text || '|' ||
  (SELECT count(*) FROM platform.location_outbox_events WHERE status='PROCESSING')::text || '|' ||
  (SELECT count(*) FROM custody.proof_upload_sessions WHERE status IN ('CREATED','UPLOADED','VALIDATING','READY'))::text;
'@
    $result = Invoke-Ops002PostgresQuery -Context $Context -Query $query
    $counts = $result.StandardOutput.Trim().Split("|")
    if ($counts.Count -ne 3 -or
        @($counts | Where-Object { $_ -notmatch '^\d+$' }).Count -ne 0) {
        throw "Unable to verify quiescence."
    }
    if ([int64]$counts[0] -ne 0 -or [int64]$counts[1] -ne 0) {
        throw "OUTBOX_PROCESSING_DURING_BACKUP"
    }
    if ([int64]$counts[2] -ne 0) {
        throw "POD_SESSION_TRANSITIONAL_DURING_BACKUP"
    }
}

function Get-Ops002MigrationHistories {
    param([Parameter(Mandatory)] $Context)

    $query = @'
SELECT jsonb_build_object(
  '__ef_migrations_history_custody',
    (SELECT count(*) FROM platform.__ef_migrations_history_custody),
  '__ef_migrations_history_dispatch',
    (SELECT count(*) FROM platform.__ef_migrations_history_dispatch),
  '__ef_migrations_history_drivers',
    (SELECT count(*) FROM platform.__ef_migrations_history_drivers),
  '__ef_migrations_history_identity',
    (SELECT count(*) FROM platform.__ef_migrations_history_identity),
  '__ef_migrations_history_locations',
    (SELECT count(*) FROM platform.__ef_migrations_history_locations),
  '__ef_migrations_history_orders',
    (SELECT count(*) FROM platform.__ef_migrations_history_orders),
  '__ef_migrations_history_organizations',
    (SELECT count(*) FROM platform.__ef_migrations_history_organizations),
  '__ef_migrations_history_pricing',
    (SELECT count(*) FROM platform.__ef_migrations_history_pricing)
)::text;
'@
    $result = Invoke-Ops002PostgresQuery -Context $Context -Query $query
    $text = $result.StandardOutput.Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "Migration histories were not found."
    }
    return $text | ConvertFrom-Json
}

function Get-Ops002DatabaseVersions {
    param([Parameter(Mandatory)] $Context)

    $query = @'
SELECT current_setting('server_version') || '|' ||
       (SELECT extversion FROM pg_extension WHERE extname='postgis');
'@
    $result = Invoke-Ops002PostgresQuery -Context $Context -Query $query
    $parts = $result.StandardOutput.Trim().Split("|")
    if ($parts.Count -ne 2) {
        throw "Unable to read PostgreSQL/PostGIS versions."
    }
    return [pscustomobject]@{
        PostgreSql = $parts[0]
        PostGis = $parts[1]
    }
}

function Get-Ops002ProofReferences {
    param(
        [Parameter(Mandatory)] $Context,
        [string] $Database
    )

    $query = @'
COPY (
  SELECT
    encode(convert_to(owner_org_id::text,'UTF8'),'hex'),
    encode(convert_to(order_id::text,'UTF8'),'hex'),
    encode(convert_to(upload_session_id::text,'UTF8'),'hex'),
    encode(convert_to(object_key,'UTF8'),'hex'),
    encode(sha256,'hex'),
    size_bytes::text
  FROM custody.proofs
  ORDER BY object_key
) TO STDOUT WITH (FORMAT text,DELIMITER E'\t');
'@
    $result = Invoke-Ops002PostgresQuery -Context $Context -Query $query `
        -Database $Database
    $references = [System.Collections.Generic.List[object]]::new()
    foreach ($line in ($result.StandardOutput -split "`r?`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $parts = $line.Split("`t")
        if ($parts.Count -ne 6 -or
            $parts[4] -notmatch '^[0-9a-f]{64}$' -or
            $parts[5] -notmatch '^\d+$') {
            throw "A proof object reference is invalid."
        }
        try {
            $ownerOrganizationId = [System.Text.Encoding]::UTF8.GetString(
                [Convert]::FromHexString($parts[0]))
            $orderId = [System.Text.Encoding]::UTF8.GetString(
                [Convert]::FromHexString($parts[1]))
            $sessionId = [System.Text.Encoding]::UTF8.GetString(
                [Convert]::FromHexString($parts[2]))
            $key = [System.Text.Encoding]::UTF8.GetString(
                [Convert]::FromHexString($parts[3]))
        }
        catch {
            throw "A proof object reference is invalid."
        }
        $references.Add([pscustomobject]@{
            OwnerOrganizationId = $ownerOrganizationId
            OrderId = $orderId
            SessionId = $sessionId
            Key = $key
            Sha256 = $parts[4]
            Bytes = [int64]$parts[5]
        })
    }
    return @($references)
}

function Get-Ops002ObjectInventory {
    param([Parameter(Mandatory)] [string] $Root)

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root)
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse) {
        if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Object mirror contains a symbolic link."
        }
        $fullPath = [System.IO.Path]::GetFullPath($file.FullName)
        if (-not (Test-Ops002PathWithin -Path $fullPath -Parent $resolvedRoot)) {
            throw "Object mirror escaped its staging root."
        }
        $relative = [System.IO.Path]::GetRelativePath($resolvedRoot, $fullPath).Replace('\', '/')
        if ($relative.StartsWith("/") -or $relative.Split("/") -contains "..") {
            throw "Object mirror contains an unsafe key."
        }
        $items.Add([pscustomobject]@{
            key = $relative
            bytes = [int64]$file.Length
            sha256 = Get-Ops002Sha256 -Path $fullPath
        })
    }

    $ordered = @($items | Sort-Object key)
    $digestText = ($ordered | ForEach-Object {
        "{0}`t{1}`t{2}" -f $_.key, $_.bytes, $_.sha256
    }) -join "`n"
    return [pscustomobject]@{
        Items = $ordered
        Count = $ordered.Count
        TotalBytes = [int64](($ordered | Measure-Object -Property bytes -Sum).Sum ?? 0)
        Digest = Get-Ops002TextSha256 -Text $digestText
    }
}

function Assert-Ops002ProofObjectIntegrity {
    param(
        [Parameter(Mandatory)] [object[]] $ProofReferences,
        [Parameter(Mandatory)] $Inventory
    )

    $byKey = @{}
    foreach ($item in $Inventory.Items) {
        $byKey[$item.key] = $item
    }
    foreach ($proof in $ProofReferences) {
        $expectedKey = "proofs/{0}/{1}/{2}" -f
            $proof.OwnerOrganizationId,
            $proof.OrderId,
            $proof.SessionId
        if ($proof.Key -cne $expectedKey) {
            throw "PROOF_OBJECT_TENANT_PATH_MISMATCH"
        }
        if (-not $byKey.ContainsKey($proof.Key)) {
            throw "PROOF_OBJECT_MISSING"
        }
        $item = $byKey[$proof.Key]
        if ($item.sha256 -cne $proof.Sha256 -or $item.bytes -ne $proof.Bytes) {
            throw "PROOF_OBJECT_HASH_MISMATCH"
        }
    }
}

function Assert-Ops002BucketPrivate {
    param([Parameter(Mandatory)] $Context)

    $shell = @'
set -eu
mc alias set local http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null
mc anonymous get "local/$MINIO_BUCKET"
'@
    $result = Invoke-Ops002Compose -Context $Context -Arguments @(
        "run", "--rm", "--no-deps", "--entrypoint", "/bin/sh",
        "minio-init", "-c", $shell
    )
    if (($result.StandardOutput + $result.StandardError) -notmatch '(?i)private') {
        throw "Object storage bucket is not private."
    }
}

function Get-Ops002HostContainerUserArguments {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        return @()
    }

    $uid = (Invoke-Ops002Process -FilePath "id" -Arguments @("-u")).StandardOutput.Trim()
    $gid = (Invoke-Ops002Process -FilePath "id" -Arguments @("-g")).StandardOutput.Trim()
    if ($uid -notmatch '^\d+$' -or $gid -notmatch '^\d+$') {
        throw "Unable to resolve the host user for secure object staging."
    }
    return @("--user", "${uid}:${gid}")
}

function Invoke-Ops002McMirrorToHost {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Destination,
        [int] $TimeoutSeconds = 300
    )

    $resolved = [System.IO.Path]::GetFullPath($Destination)
    $mount = "$resolved`:/backup"
    $shell = @'
set -eu
export MC_CONFIG_DIR=/tmp/.mc
mc alias set local http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null
mc mirror --quiet --overwrite --remove "local/$MINIO_BUCKET" /backup
'@
    $arguments = @(
        "run", "--rm", "--no-deps"
    ) + @(Get-Ops002HostContainerUserArguments) + @(
        "--volume", $mount,
        "--entrypoint", "/bin/sh",
        "minio-init", "-c", $shell
    )
    Invoke-Ops002Compose -Context $Context -Arguments $arguments `
        -TimeoutSeconds $TimeoutSeconds | Out-Null
}

function Invoke-Ops002McMirrorFromHost {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Source,
        [int] $TimeoutSeconds = 300
    )

    $resolved = [System.IO.Path]::GetFullPath($Source)
    $mount = "$resolved`:/backup:ro"
    $shell = @'
set -eu
mc alias set local http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null
test "$(mc find "local/$MINIO_BUCKET" --type f --print x | wc -l)" -eq 0
mc mirror --quiet --overwrite /backup "local/$MINIO_BUCKET"
mc anonymous set none "local/$MINIO_BUCKET" >/dev/null
'@
    Invoke-Ops002Compose -Context $Context -Arguments @(
        "run", "--rm", "--no-deps",
        "--volume", $mount,
        "--entrypoint", "/bin/sh",
        "minio-init", "-c", $shell
    ) -TimeoutSeconds $TimeoutSeconds | Out-Null
}

function Get-Ops002BucketObjectCount {
    param([Parameter(Mandatory)] $Context)

    $shell = @'
set -eu
mc alias set local http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null
mc find "local/$MINIO_BUCKET" --type f --print x | wc -l
'@
    $result = Invoke-Ops002Compose -Context $Context -Arguments @(
        "run", "--rm", "--no-deps", "--entrypoint", "/bin/sh",
        "minio-init", "-c", $shell
    )
    $value = $result.StandardOutput.Trim()
    if ($value -notmatch '^\d+$') {
        throw "Unable to count target bucket objects."
    }
    return [int64]$value
}

function Assert-Ops002ArchiveEntries {
    param(
        [Parameter(Mandatory)] [string] $Archive,
        [int] $MaximumEntries = $script:Ops002MaximumArchiveEntries,
        [int64] $MaximumExpandedBytes = $script:Ops002MaximumExpandedBytes
    )

    $stream = [System.IO.File]::OpenRead($Archive)
    $gzip = [System.IO.Compression.GZipStream]::new(
        $stream,
        [System.IO.Compression.CompressionMode]::Decompress)
    $reader = [System.Formats.Tar.TarReader]::new($gzip, $false)
    $entryCount = 0
    $expanded = [int64]0
    try {
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            $entryCount++
            if ($entryCount -gt $MaximumEntries) {
                throw "Archive entry count is outside the allowed range."
            }
            if ($entry.EntryType -notin @(
                [System.Formats.Tar.TarEntryType]::Directory,
                [System.Formats.Tar.TarEntryType]::RegularFile,
                [System.Formats.Tar.TarEntryType]::V7RegularFile
            )) {
                throw "Archive links and special entries are not allowed."
            }

            $normalized = $entry.Name.Replace('\', '/')
            if ($normalized.StartsWith("/") -or
                $normalized -match '^[A-Za-z]:' -or
                $normalized.Split("/") -contains "..") {
                throw "Archive contains an unsafe path."
            }
            $entryPath = $normalized.TrimEnd("/")
            $isDirectory = $entry.EntryType -eq
                [System.Formats.Tar.TarEntryType]::Directory
            $isAllowedDirectory = $isDirectory -and (
                $entryPath -in @(
                    "payload",
                    "payload/postgres",
                    "payload/object-storage"
                ) -or
                $entryPath -match '^payload/object-storage/.+')
            $isAllowedFile = -not $isDirectory -and (
                $entryPath -in @(
                    "payload/manifest.json",
                    "payload/object-inventory.json",
                    "payload/postgres/database.dump"
                ) -or
                $entryPath -match '^payload/object-storage/.+')
            if (-not $isAllowedDirectory -and -not $isAllowedFile) {
                throw "Archive contains an entry outside the payload allowlist."
            }

            $expanded += [int64]$entry.Length
            if ($expanded -gt $MaximumExpandedBytes) {
                throw "Archive expanded size exceeds the configured limit."
            }
        }
    }
    finally {
        $reader.Dispose()
        $gzip.Dispose()
        $stream.Dispose()
    }
    if ($entryCount -eq 0) {
        throw "Archive entry count is outside the allowed range."
    }

    return [pscustomobject]@{
        EntryCount = $entryCount
        ExpandedBytes = $expanded
    }
}

function Expand-Ops002ValidatedArchive {
    param(
        [Parameter(Mandatory)] [string] $Archive,
        [Parameter(Mandatory)] [string] $Destination
    )

    Assert-Ops002ArchiveEntries -Archive $Archive | Out-Null
    New-Item -ItemType Directory -Path $Destination -ErrorAction Stop | Out-Null
    Invoke-Ops002Process -FilePath "tar" -Arguments @(
        "-xzf", $Archive, "-C", $Destination, "--no-same-owner", "--no-same-permissions"
    ) | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $Destination -Recurse -Force) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Extracted payload contains a link."
        }
        if (-not (Test-Ops002PathWithin -Path $item.FullName -Parent $Destination)) {
            throw "Extracted payload escaped staging."
        }
    }
}

function Assert-Ops002Manifest {
    param(
        [Parameter(Mandatory)] $Manifest,
        [Parameter(Mandatory)] [string] $PayloadRoot
    )

    $required = @(
        "format_version", "backup_id", "created_at_utc", "completed_at_utc",
        "source_git_sha", "normative_version", "postgresql_version", "postgis_version",
        "database_dump_sha256", "database_dump_bytes", "object_count",
        "object_total_bytes", "object_inventory_sha256", "migration_histories",
        "consistency_fingerprint_before", "consistency_fingerprint_after", "quiesced",
        "source_project", "source_database"
    )
    foreach ($name in $required) {
        if ($Manifest.psobject.Properties.Name -notcontains $name) {
            throw "Manifest is missing a required field."
        }
    }
    if ($Manifest.format_version -cne $script:Ops002FormatVersion -or
        $Manifest.quiesced -ne $true -or
        $Manifest.source_git_sha -notmatch '^[0-9a-f]{40}$' -or
        $Manifest.database_dump_sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Manifest.object_inventory_sha256 -notmatch '^[0-9a-f]{64}$' -or
        $Manifest.consistency_fingerprint_before -cne
            $Manifest.consistency_fingerprint_after) {
        throw "Manifest validation failed."
    }

    $dump = Join-Path $PayloadRoot "postgres/database.dump"
    $inventoryPath = Join-Path $PayloadRoot "object-inventory.json"
    $objects = Join-Path $PayloadRoot "object-storage"
    if (-not (Test-Path -LiteralPath $dump -PathType Leaf) -or
        -not (Test-Path -LiteralPath $inventoryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $objects -PathType Container)) {
        throw "Payload structure is incomplete."
    }
    if ((Get-Item -LiteralPath $dump).Length -ne [int64]$Manifest.database_dump_bytes -or
        (Get-Ops002Sha256 -Path $dump) -cne $Manifest.database_dump_sha256) {
        throw "Database dump integrity validation failed."
    }

    $inventory = Get-Ops002ObjectInventory -Root $objects
    if ($inventory.Count -ne [int]$Manifest.object_count -or
        $inventory.TotalBytes -ne [int64]$Manifest.object_total_bytes -or
        $inventory.Digest -cne $Manifest.object_inventory_sha256) {
        throw "Object payload integrity validation failed."
    }

    $declared = @(Get-Content -LiteralPath $inventoryPath -Raw -Encoding utf8 |
        ConvertFrom-Json)
    if ($declared.Count -ne $inventory.Count) {
        throw "Object inventory entry count is inconsistent."
    }
    for ($index = 0; $index -lt $declared.Count; $index++) {
        if ($declared[$index].key -cne $inventory.Items[$index].key -or
            [int64]$declared[$index].bytes -ne $inventory.Items[$index].bytes -or
            $declared[$index].sha256 -cne $inventory.Items[$index].sha256) {
            throw "Object inventory integrity validation failed."
        }
    }
    return $inventory
}

function Assert-Ops002RedactedReport {
    param([Parameter(Mandatory)] [string] $Path)

    $text = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    $forbiddenNames = @(
        "object_key", "bucket_name", "database_name", "hostname", "port",
        "username", "connection_string", "password", "access_key",
        "private_identity", "tracking_token", "signed_url", "address", "payload"
    )
    foreach ($name in $forbiddenNames) {
        if ($text -match ('(?i)"' + [regex]::Escape($name) + '"')) {
            throw "Redacted report contains a forbidden field."
        }
    }
    if ($text -match '(?i)AGE-SECRET-KEY-|postgres(?:ql)?://|AKIA[0-9A-Z]{16}' -or
        $text -match '[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}') {
        throw "Redacted report contains secret or identifier-shaped data."
    }
}

function Write-Ops002Json {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)] [string] $Path
    )

    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(
        $Path,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}

function Test-Ops002FailureStage {
    param(
        [string] $Requested,
        [Parameter(Mandatory)] [string] $Stage
    )

    if ([string]::IsNullOrWhiteSpace($Requested)) {
        return $false
    }
    if ($env:OPS002_TEST_MODE -cne "true") {
        throw "Test failure injection is disabled."
    }
    return $Requested -ceq $Stage
}
