Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-LocalEnvironmentContext {
    param(
        [Parameter(Mandatory)] [string] $ComposeFile,
        [Parameter(Mandatory)] [string] $EnvironmentFile,
        [string] $ProjectName
    )

    $resolvedCompose = [System.IO.Path]::GetFullPath($ComposeFile)
    $resolvedEnvironment = [System.IO.Path]::GetFullPath($EnvironmentFile)

    if (-not (Test-Path -LiteralPath $resolvedCompose -PathType Leaf)) {
        throw "Compose file not found: $resolvedCompose"
    }

    if (-not (Test-Path -LiteralPath $resolvedEnvironment -PathType Leaf)) {
        throw "Environment file not found: $resolvedEnvironment. Copy deploy/.env.example to deploy/.env.local first."
    }

    $environment = Read-LocalEnvironmentFile -Path $resolvedEnvironment
    $resolvedProject = if ([string]::IsNullOrWhiteSpace($ProjectName)) {
        $environment["COMPOSE_PROJECT_NAME"]
    }
    else {
        $ProjectName
    }

    if ([string]::IsNullOrWhiteSpace($resolvedProject) -or $resolvedProject -notmatch '^[a-z0-9][a-z0-9_-]+$') {
        throw ("Compose project name must match ^[a-z0-9][a-z0-9_-]+`$: '{0}'." -f $resolvedProject)
    }

    $arguments = @(
        "compose",
        "--project-name", $resolvedProject,
        "--env-file", $resolvedEnvironment,
        "--file", $resolvedCompose
    )

    return [pscustomobject]@{
        ComposeFile = $resolvedCompose
        EnvironmentFile = $resolvedEnvironment
        ProjectName = $resolvedProject
        Environment = $environment
        DockerArguments = $arguments
    }
}

function Read-LocalEnvironmentFile {
    param([Parameter(Mandatory)] [string] $Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path -Encoding utf8) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0) {
            throw "Invalid environment entry in $Path. Expected NAME=value."
        }

        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim()
        $values[$name] = $value
    }

    return $values
}

function Assert-DockerAvailable {
    & docker version --format '{{.Server.Version}}' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Engine is unavailable. Start Docker Desktop or the Docker daemon and retry."
    }

    & docker compose version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose V2 is unavailable. Install a current Docker Compose plugin and retry."
    }
}

function Invoke-DockerCompose {
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [switch] $CaptureOutput,
        [switch] $AllowFailure
    )

    $allArguments = @($Context.DockerArguments) + $Arguments
    if ($CaptureOutput) {
        $output = & docker @allArguments 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0 -and -not $AllowFailure) {
            throw "docker compose $($Arguments -join ' ') failed with exit code $exitCode.`n$($output -join [Environment]::NewLine)"
        }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = ($output -join [Environment]::NewLine).Trim()
        }
    }

    & docker @allArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $exitCode."
    }

    return $exitCode
}

function Get-ComposeConfig {
    param([Parameter(Mandatory)] $Context)

    $result = Invoke-DockerCompose -Context $Context -Arguments @("config", "--format", "json") -CaptureOutput
    return $result.Output | ConvertFrom-Json
}

function Assert-ComposeStaticPolicy {
    param([Parameter(Mandatory)] $Context)

    Invoke-DockerCompose -Context $Context -Arguments @("config", "--quiet") | Out-Null
    $config = Get-ComposeConfig -Context $Context
    $allowedServices = @("postgres", "redis", "minio", "minio-init", "mailpit")
    $permanentServices = @("postgres", "redis", "minio", "mailpit")
    $violations = [System.Collections.Generic.List[string]]::new()
    $serviceProperties = @($config.services.psobject.Properties)

    foreach ($serviceProperty in $serviceProperties) {
        $name = $serviceProperty.Name
        $service = $serviceProperty.Value
        $propertyNames = @($service.psobject.Properties.Name)

        if ($name -notin $allowedServices) {
            $violations.Add("Unexpected service '$name'; application containers are outside FND-002.")
        }

        if ($propertyNames -contains "container_name") {
            $violations.Add("$name defines container_name.")
        }

        if ([string]$service.image -match '(^|:)latest($|@)') {
            $violations.Add("$name uses a latest image tag.")
        }

        if ([string]$service.image -notmatch '@sha256:[0-9a-f]{64}$') {
            $violations.Add("$name image is not pinned by digest.")
        }

        if ($propertyNames -contains "privileged" -and $service.privileged -eq $true) {
            $violations.Add("$name enables privileged mode.")
        }

        if ($propertyNames -contains "network_mode" -and [string]$service.network_mode -eq "host") {
            $violations.Add("$name uses host networking.")
        }

        if ($name -in $permanentServices -and -not ($propertyNames -contains "healthcheck")) {
            $violations.Add("$name has no healthcheck.")
        }

        $ports = if ($propertyNames -contains "ports") { @($service.ports) } else { @() }
        foreach ($port in $ports) {
            if ([string]$port.host_ip -ne "127.0.0.1") {
                $violations.Add("$name publishes port $($port.published) outside loopback.")
            }
        }

        $mounts = if ($propertyNames -contains "volumes") { @($service.volumes) } else { @() }
        foreach ($mount in $mounts) {
            $source = [string]$mount.source
            $target = [string]$mount.target
            if ($source -match 'docs[/\\]normative[/\\]v0\.6|AI-06_SCHEMA\.sql|AI-18_DATABASE_ROLE_MODEL\.sql') {
                $violations.Add("$name mounts a frozen normative artifact: $source.")
            }

            if ($source -eq "/var/run/docker.sock" -or $target -eq "/var/run/docker.sock") {
                $violations.Add("$name mounts the Docker socket.")
            }
        }
    }

    $requiredVolumes = @{
        postgres = "/var/lib/postgresql/data"
        redis = "/data"
        minio = "/data"
        mailpit = "/data"
    }

    foreach ($entry in $requiredVolumes.GetEnumerator()) {
        $service = $config.services.($entry.Key)
        $matchingMount = @(@($service.volumes) | Where-Object {
            $_.target -eq $entry.Value -and $_.type -eq "volume"
        })
        if ($matchingMount.Count -ne 1) {
            $violations.Add("$($entry.Key) must use one named volume at $($entry.Value).")
        }
    }

    if ($violations.Count -gt 0) {
        throw "Compose policy violations:`n- $($violations -join "`n- ")"
    }
}

function ConvertFrom-ComposePsOutput {
    param([string] $Output)

    if ([string]::IsNullOrWhiteSpace($Output)) {
        return @()
    }

    $trimmed = $Output.Trim()
    if ($trimmed.StartsWith("[")) {
        return @($trimmed | ConvertFrom-Json)
    }

    return @($trimmed -split "`r?`n" | ForEach-Object { $_ | ConvertFrom-Json })
}

function Get-ComposeServiceState {
    param([Parameter(Mandatory)] $Context)

    $result = Invoke-DockerCompose -Context $Context -Arguments @("ps", "--all", "--format", "json") -CaptureOutput
    return @(ConvertFrom-ComposePsOutput -Output $result.Output)
}

function Show-ComposeDiagnostics {
    param(
        [Parameter(Mandatory)] $Context,
        [string] $Service
    )

    Write-Host "Compose status for project '$($Context.ProjectName)':"
    Invoke-DockerCompose -Context $Context -Arguments @("ps", "--all") -AllowFailure | Out-Null
    $logArguments = @("logs", "--tail", "100")
    if (-not [string]::IsNullOrWhiteSpace($Service)) {
        $logArguments += $Service
    }
    Invoke-DockerCompose -Context $Context -Arguments $logArguments -AllowFailure | Out-Null
}

function Wait-ComposeHealthy {
    param(
        [Parameter(Mandatory)] $Context,
        [int] $TimeoutSeconds = 180
    )

    $permanentServices = @("postgres", "redis", "minio", "mailpit")
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastProblem = "services have not reported state"

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $states = @(Get-ComposeServiceState -Context $Context)
        $problems = [System.Collections.Generic.List[string]]::new()
        foreach ($serviceName in $permanentServices) {
            $service = $states | Where-Object { $_.Service -eq $serviceName } | Select-Object -First 1
            if ($null -eq $service) {
                $problems.Add("$serviceName is absent")
                continue
            }

            if ($service.State -ne "running") {
                $problems.Add("$serviceName state=$($service.State)")
            }
            elseif ($service.Health -ne "healthy") {
                $problems.Add("$serviceName health=$($service.Health)")
            }
        }

        $init = $states | Where-Object { $_.Service -eq "minio-init" } | Select-Object -First 1
        if ($null -eq $init) {
            $problems.Add("minio-init is absent")
        }
        elseif ($init.State -eq "exited" -and [int]$init.ExitCode -ne 0) {
            $problems.Add("minio-init exited with code $($init.ExitCode)")
        }
        elseif ($init.State -notin @("exited", "running")) {
            $problems.Add("minio-init state=$($init.State)")
        }

        if ($problems.Count -eq 0 -and $null -ne $init -and $init.State -eq "exited") {
            return
        }

        $lastProblem = $problems -join "; "
        Start-Sleep -Seconds 2
    }

    Show-ComposeDiagnostics -Context $Context
    throw "Local environment did not become healthy within $TimeoutSeconds seconds: $lastProblem"
}

function Start-ComposeEnvironment {
    param(
        [Parameter(Mandatory)] $Context,
        [int] $TimeoutSeconds = 180
    )

    # Compose --wait expects selected services to remain running. The initializer
    # is deliberately one-shot, so start and wait for permanent services first.
    Invoke-DockerCompose -Context $Context -Arguments @(
        "up", "--detach", "--wait", "postgres", "redis", "minio", "mailpit"
    ) | Out-Null
    Invoke-DockerCompose -Context $Context -Arguments @(
        "up", "--no-deps", "minio-init"
    ) | Out-Null
    Wait-ComposeHealthy -Context $Context -TimeoutSeconds $TimeoutSeconds
}

function Assert-HostPortAvailable {
    param([Parameter(Mandatory)] [int] $Port)

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
    }
    catch {
        throw "Loopback port $Port is already in use. Change the corresponding value in the environment file; no process was stopped."
    }
    finally {
        $listener.Stop()
    }
}

function Assert-ConfiguredPortsAvailable {
    param([Parameter(Mandatory)] $Context)

    foreach ($name in @(
        "POSTGRES_HOST_PORT",
        "REDIS_HOST_PORT",
        "MINIO_API_HOST_PORT",
        "MINIO_CONSOLE_HOST_PORT",
        "MAIL_SMTP_HOST_PORT",
        "MAIL_UI_HOST_PORT"
    )) {
        if (-not $Context.Environment.ContainsKey($name)) {
            throw "Missing required port variable $name."
        }
        Assert-HostPortAvailable -Port ([int]$Context.Environment[$name])
    }
}

function Get-ProjectResourceIds {
    param([Parameter(Mandatory)] [string] $ProjectName)

    $containers = @(& docker container ls --all --quiet --filter "label=com.docker.compose.project=$ProjectName")
    $networks = @(& docker network ls --quiet --filter "label=com.docker.compose.project=$ProjectName")
    $volumes = @(& docker volume ls --quiet --filter "label=com.docker.compose.project=$ProjectName")
    return [pscustomobject]@{
        Containers = @($containers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Networks = @($networks | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Volumes = @($volumes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
}

function Get-UnrelatedResourceSnapshot {
    param([Parameter(Mandatory)] [string] $ProjectName)

    $project = Get-ProjectResourceIds -ProjectName $ProjectName
    return [pscustomobject]@{
        Containers = @(& docker container ls --all --quiet | Where-Object { $_ -notin $project.Containers })
        Networks = @(& docker network ls --quiet | Where-Object { $_ -notin $project.Networks })
        Volumes = @(& docker volume ls --quiet | Where-Object { $_ -notin $project.Volumes })
    }
}

function Assert-UnrelatedResourcesPreserved {
    param([Parameter(Mandatory)] $Snapshot)

    $currentContainers = @(& docker container ls --all --quiet)
    $currentNetworks = @(& docker network ls --quiet)
    $currentVolumes = @(& docker volume ls --quiet)
    $missing = @(
        @($Snapshot.Containers | Where-Object { $_ -notin $currentContainers } | ForEach-Object { "container:$_" })
        @($Snapshot.Networks | Where-Object { $_ -notin $currentNetworks } | ForEach-Object { "network:$_" })
        @($Snapshot.Volumes | Where-Object { $_ -notin $currentVolumes } | ForEach-Object { "volume:$_" })
    )
    if ($missing.Count -gt 0) {
        throw "Cleanup removed resources outside the Compose project: $($missing -join ', ')."
    }
}

function Assert-ProjectResourcesAbsent {
    param([Parameter(Mandatory)] [string] $ProjectName)

    $resources = Get-ProjectResourceIds -ProjectName $ProjectName
    if ($resources.Containers.Count -gt 0 -or $resources.Networks.Count -gt 0 -or $resources.Volumes.Count -gt 0) {
        throw "Project '$ProjectName' still owns containers, networks, or volumes after cleanup."
    }
}
