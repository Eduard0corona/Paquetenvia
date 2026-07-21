[CmdletBinding()]
param(
    [string] $ComposeFile,
    [string] $EnvironmentFile,
    [string] $ProjectName,
    [int] $TimeoutSeconds = 300,
    [switch] $CI,
    [switch] $KeepResources
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

function Assert-Equal {
    param(
        [Parameter(Mandatory)] [string] $Expected,
        [AllowEmptyString()] [string] $Actual,
        [Parameter(Mandatory)] [string] $Description
    )

    if ($Actual.Trim() -ne $Expected) {
        throw "$Description failed. Expected '$Expected', received '$($Actual.Trim())'."
    }
}

function Invoke-PostgresQuery {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Query
    )

    $result = Invoke-DockerCompose -Context $Context -Arguments @(
        "exec", "-T", "postgres",
        "psql",
        "--username", $Context.Environment["POSTGRES_USER"],
        "--dbname", $Context.Environment["POSTGRES_DB"],
        "--tuples-only", "--no-align", "--set", "ON_ERROR_STOP=1",
        "--command", $Query
    ) -CaptureOutput
    return $result.Output.Trim()
}

function Invoke-RedisCommand {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Command,
        [hashtable] $Variables = @{}
    )

    $arguments = @("exec", "-T")
    foreach ($entry in $Variables.GetEnumerator()) {
        $arguments += @("--env", "$($entry.Key)=$($entry.Value)")
    }
    $arguments += @(
        "redis", "sh", "-c",
        "redis-cli --no-auth-warning -a `"`$REDIS_PASSWORD`" $Command"
    )
    $result = Invoke-DockerCompose -Context $Context -Arguments $arguments -CaptureOutput
    return $result.Output.Trim()
}

function Invoke-MinioCommand {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Command,
        [hashtable] $Variables = @{}
    )

    $arguments = @("run", "--rm", "--no-deps", "--entrypoint", "/bin/sh")
    foreach ($entry in $Variables.GetEnumerator()) {
        $arguments += @("--env", "$($entry.Key)=$($entry.Value)")
    }
    $arguments += @(
        "minio-init", "-c",
        'mc alias set smoke http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null && ' + $Command
    )
    $result = Invoke-DockerCompose -Context $Context -Arguments $arguments -CaptureOutput
    return $result.Output.Trim()
}

function Get-MailpitMessage {
    param(
        [Parameter(Mandatory)] [string] $BaseUri,
        [Parameter(Mandatory)] [string] $Subject,
        [int] $WaitSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($WaitSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $response = Invoke-RestMethod -Method Get -Uri "$BaseUri/api/v1/messages"
        $messages = if ($null -ne $response.messages) { @($response.messages) } else { @($response) }
        $message = $messages | Where-Object { $_.Subject -eq $Subject } | Select-Object -First 1
        if ($null -ne $message) {
            return $message
        }
        Start-Sleep -Seconds 1
    }

    throw "Mailpit did not expose message '$Subject' within $WaitSeconds seconds."
}

function Send-SmokeEmail {
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Subject,
        [Parameter(Mandatory)] [string] $Body
    )

    $message = [System.Net.Mail.MailMessage]::new(
        "fnd002-sender@paquetenvia.local",
        "fnd002-recipient@paquetenvia.local",
        $Subject,
        $Body
    )
    $client = [System.Net.Mail.SmtpClient]::new("127.0.0.1", $Port)
    try {
        $client.Send($message)
    }
    finally {
        $client.Dispose()
        $message.Dispose()
    }
}

function Assert-SmokeData {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string] $Schema,
        [Parameter(Mandatory)] [string] $Value,
        [Parameter(Mandatory)] [string] $RedisKey,
        [Parameter(Mandatory)] [string] $MinioObject,
        [Parameter(Mandatory)] [string] $MailpitBaseUri,
        [Parameter(Mandatory)] [string] $MailSubject
    )

    Assert-Equal -Expected $Value `
        -Actual (Invoke-PostgresQuery -Context $Context -Query "SELECT value FROM $Schema.persistence_probe WHERE id = 1;") `
        -Description "PostgreSQL persistence"
    Assert-Equal -Expected $Value `
        -Actual (Invoke-RedisCommand -Context $Context -Command 'GET "$SMOKE_KEY"' -Variables @{ SMOKE_KEY = $RedisKey }) `
        -Description "Redis persistence"
    Assert-Equal -Expected $Value `
        -Actual (Invoke-MinioCommand -Context $Context -Command 'mc cat "smoke/$MINIO_BUCKET/$SMOKE_OBJECT"' -Variables @{ SMOKE_OBJECT = $MinioObject }) `
        -Description "MinIO persistence"
    $null = Get-MailpitMessage -BaseUri $MailpitBaseUri -Subject $MailSubject
}

$context = $null
$started = $false
$succeeded = $false
$failure = $null
$mailMessageId = $null
$schemaCreated = $false
$redisValueCreated = $false
$minioObjectCreated = $false
$unrelatedBefore = $null
$token = [Guid]::NewGuid().ToString("N")
$schema = "fnd002_smoke_$token"
$value = "persistence-$token"
$redisKey = "fnd002:smoke:$token"
$minioObject = "fnd002-smoke/$token.txt"
$mailSubject = "FND-002 smoke $token"

try {
    $context = Get-LocalEnvironmentContext `
        -ComposeFile $ComposeFile `
        -EnvironmentFile $EnvironmentFile `
        -ProjectName $ProjectName
    Assert-DockerAvailable
    Assert-ComposeStaticPolicy -Context $context

    if ($CI) {
        $unrelatedBefore = Get-UnrelatedResourceSnapshot -ProjectName $context.ProjectName
        Invoke-DockerCompose -Context $context -Arguments @("down", "--volumes", "--remove-orphans") -AllowFailure | Out-Null
        Assert-ProjectResourcesAbsent -ProjectName $context.ProjectName
        Assert-UnrelatedResourcesPreserved -Snapshot $unrelatedBefore
    }

    $existing = Get-ProjectResourceIds -ProjectName $context.ProjectName
    if ($existing.Containers.Count -eq 0) {
        Assert-ConfiguredPortsAvailable -Context $context
    }

    Write-Host "Starting FND-002 environment '$($context.ProjectName)'..."
    Start-ComposeEnvironment -Context $context -TimeoutSeconds $TimeoutSeconds
    $started = $true

    # A second up must be harmless and leave all services healthy.
    Start-ComposeEnvironment -Context $context -TimeoutSeconds $TimeoutSeconds

    $extensionState = Invoke-PostgresQuery -Context $context -Query @"
SELECT extname || ':' || nspname
FROM pg_extension
JOIN pg_namespace ON pg_namespace.oid = pg_extension.extnamespace
WHERE extname IN ('postgis', 'pgcrypto')
ORDER BY extname;
"@
    $extensions = @($extensionState -split "`r?`n")
    if ($extensions -notcontains "pgcrypto:extensions" -or $extensions -notcontains "postgis:public") {
        throw "PostgreSQL extensions are not in the required schemas. Found: $($extensions -join ', ')."
    }
    $postgresVersion = Invoke-PostgresQuery -Context $context -Query "SELECT current_setting('server_version');"
    if ($postgresVersion -notmatch '^17\.') {
        throw "Unexpected PostgreSQL version '$postgresVersion'; expected the pinned 17.x line."
    }
    $postgisVersion = Invoke-PostgresQuery -Context $context -Query "SELECT PostGIS_Version();"
    if ([string]::IsNullOrWhiteSpace($postgisVersion)) {
        throw "PostGIS_Version() returned no value."
    }

    Invoke-PostgresQuery -Context $context -Query "CREATE SCHEMA $schema; CREATE TABLE $schema.persistence_probe (id integer PRIMARY KEY, value text NOT NULL); INSERT INTO $schema.persistence_probe VALUES (1, '$value');" | Out-Null
    $schemaCreated = $true
    Assert-Equal -Expected "PONG" `
        -Actual (Invoke-RedisCommand -Context $context -Command "PING") `
        -Description "Redis connectivity"
    Assert-Equal -Expected "OK" `
        -Actual (Invoke-RedisCommand -Context $context -Command 'SET "$SMOKE_KEY" "$SMOKE_VALUE"' -Variables @{ SMOKE_KEY = $redisKey; SMOKE_VALUE = $value }) `
        -Description "Redis write"
    $redisValueCreated = $true
    $redisWait = Invoke-RedisCommand -Context $context -Command "WAITAOF 1 0 5000"
    if ($redisWait -match "ERR unknown command") {
        throw "Redis does not support WAITAOF; the pinned image is not the expected version."
    }
    Assert-Equal -Expected $value `
        -Actual (Invoke-MinioCommand -Context $context -Command 'printf "%s" "$SMOKE_VALUE" | mc pipe "smoke/$MINIO_BUCKET/$SMOKE_OBJECT" >/dev/null && mc cat "smoke/$MINIO_BUCKET/$SMOKE_OBJECT"' -Variables @{ SMOKE_OBJECT = $minioObject; SMOKE_VALUE = $value }) `
        -Description "MinIO write/read"
    $minioObjectCreated = $true

    $mailpitBaseUri = "http://127.0.0.1:$($context.Environment['MAIL_UI_HOST_PORT'])"
    Send-SmokeEmail -Port ([int]$context.Environment["MAIL_SMTP_HOST_PORT"]) -Subject $mailSubject -Body $value
    $mailMessage = Get-MailpitMessage -BaseUri $mailpitBaseUri -Subject $mailSubject
    $mailMessageId = [string]$mailMessage.ID
    if ([string]::IsNullOrWhiteSpace($mailMessageId)) {
        throw "Mailpit returned the smoke message without an ID."
    }

    Write-Host "Verifying persistence across individual service restarts..."
    foreach ($service in @("postgres", "redis", "minio", "mailpit")) {
        Invoke-DockerCompose -Context $context -Arguments @("restart", $service) | Out-Null
        Wait-ComposeHealthy -Context $context -TimeoutSeconds $TimeoutSeconds
    }
    Assert-SmokeData -Context $context -Schema $schema -Value $value -RedisKey $redisKey -MinioObject $minioObject -MailpitBaseUri $mailpitBaseUri -MailSubject $mailSubject

    Write-Host "Verifying non-destructive down and recovery..."
    Invoke-DockerCompose -Context $context -Arguments @("down", "--remove-orphans") | Out-Null
    $started = $false
    $preserved = Get-ProjectResourceIds -ProjectName $context.ProjectName
    if ($preserved.Volumes.Count -lt 4) {
        throw "Non-destructive down did not preserve all four named volumes."
    }
    Start-ComposeEnvironment -Context $context -TimeoutSeconds $TimeoutSeconds
    $started = $true
    Assert-SmokeData -Context $context -Schema $schema -Value $value -RedisKey $redisKey -MinioObject $minioObject -MailpitBaseUri $mailpitBaseUri -MailSubject $mailSubject

    Write-Host "Verifying unhealthy-service diagnostics..."
    Invoke-DockerCompose -Context $context -Arguments @("stop", "redis") | Out-Null
    $healthFailureDetected = $false
    try {
        Wait-ComposeHealthy -Context $context -TimeoutSeconds 5
    }
    catch {
        if ($_.Exception.Message -match "redis") {
            $healthFailureDetected = $true
        }
    }
    if (-not $healthFailureDetected) {
        throw "The health waiter did not report the intentionally stopped Redis service."
    }
    Invoke-DockerCompose -Context $context -Arguments @("start", "redis") | Out-Null
    Wait-ComposeHealthy -Context $context -TimeoutSeconds $TimeoutSeconds

    Write-Host "Verifying port-collision diagnostics..."
    $collisionListener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $collisionListener.Start()
    try {
        $collisionPort = ([System.Net.IPEndPoint]$collisionListener.LocalEndpoint).Port
        $collisionDetected = $false
        try {
            Assert-HostPortAvailable -Port $collisionPort
        }
        catch {
            if ($_.Exception.Message -match "already in use") {
                $collisionDetected = $true
            }
        }
        if (-not $collisionDetected) {
            throw "The port preflight did not report an occupied loopback port."
        }
    }
    finally {
        $collisionListener.Stop()
    }

    Invoke-PostgresQuery -Context $context -Query "DROP SCHEMA $schema CASCADE;" | Out-Null
    $schemaCreated = $false
    Invoke-RedisCommand -Context $context -Command 'DEL "$SMOKE_KEY"' -Variables @{ SMOKE_KEY = $redisKey } | Out-Null
    $redisValueCreated = $false
    Invoke-MinioCommand -Context $context -Command 'mc rm --force "smoke/$MINIO_BUCKET/$SMOKE_OBJECT"' -Variables @{ SMOKE_OBJECT = $minioObject } | Out-Null
    $minioObjectCreated = $false
    Invoke-RestMethod -Method Delete -Uri "$mailpitBaseUri/api/v1/messages/$mailMessageId" | Out-Null
    $mailMessageId = $null

    $succeeded = $true
    Write-Host "FND-002 smoke test passed: health, APIs, restart persistence, down persistence, diagnostics, and cleanup behavior."
}
catch {
    $failure = $_
    if ($null -ne $context -and $started) {
        Show-ComposeDiagnostics -Context $context
    }
}
finally {
    if ($null -ne $context) {
        if (-not $succeeded -and $started) {
            Invoke-DockerCompose -Context $context -Arguments @("up", "--detach", "postgres", "redis", "minio", "mailpit") -AllowFailure | Out-Null
            if ($schemaCreated) {
                try { Invoke-PostgresQuery -Context $context -Query "DROP SCHEMA IF EXISTS $schema CASCADE;" | Out-Null } catch { }
            }
            if ($redisValueCreated) {
                try { Invoke-RedisCommand -Context $context -Command 'DEL "$SMOKE_KEY"' -Variables @{ SMOKE_KEY = $redisKey } | Out-Null } catch { }
            }
            if ($minioObjectCreated) {
                try { Invoke-MinioCommand -Context $context -Command 'mc rm --force "smoke/$MINIO_BUCKET/$SMOKE_OBJECT"' -Variables @{ SMOKE_OBJECT = $minioObject } | Out-Null } catch { }
            }
            if (-not [string]::IsNullOrWhiteSpace($mailMessageId)) {
                try { Invoke-RestMethod -Method Delete -Uri "$mailpitBaseUri/api/v1/messages/$mailMessageId" | Out-Null } catch { }
            }
        }
        if ($CI) {
            Invoke-DockerCompose -Context $context -Arguments @("down", "--volumes", "--remove-orphans") -AllowFailure | Out-Null
            try {
                Assert-ProjectResourcesAbsent -ProjectName $context.ProjectName
                if ($null -ne $unrelatedBefore) {
                    Assert-UnrelatedResourcesPreserved -Snapshot $unrelatedBefore
                }
            }
            catch {
                if ($null -eq $failure) {
                    $failure = $_
                }
            }
        }
        elseif (-not $KeepResources) {
            Invoke-DockerCompose -Context $context -Arguments @("down", "--remove-orphans") -AllowFailure | Out-Null
        }
    }
}

if ($null -ne $failure) {
    Write-Error $failure.Exception.Message
    exit 1
}
if (-not $succeeded) {
    Write-Error "FND-002 smoke test did not complete."
    exit 1
}
