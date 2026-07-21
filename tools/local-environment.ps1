[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet("Up", "Down", "Restart", "Status", "Logs", "Smoke", "Reset")]
    [string] $Command,

    [string] $ComposeFile,
    [string] $EnvironmentFile,
    [string] $ProjectName,
    [int] $TimeoutSeconds = 240,
    [switch] $Force
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

try {
    $context = Get-LocalEnvironmentContext `
        -ComposeFile $ComposeFile `
        -EnvironmentFile $EnvironmentFile `
        -ProjectName $ProjectName
    Assert-DockerAvailable

    switch ($Command) {
        "Up" {
            Assert-ComposeStaticPolicy -Context $context
            $existing = Get-ProjectResourceIds -ProjectName $context.ProjectName
            if ($existing.Containers.Count -eq 0) {
                Assert-ConfiguredPortsAvailable -Context $context
            }
            Invoke-DockerCompose -Context $context -Arguments @("up", "--detach", "--wait") | Out-Null
            Wait-ComposeHealthy -Context $context -TimeoutSeconds $TimeoutSeconds
            Write-Host "Local environment '$($context.ProjectName)' is healthy."
        }
        "Down" {
            Invoke-DockerCompose -Context $context -Arguments @("down", "--remove-orphans") | Out-Null
            Write-Host "Containers and network stopped; named volumes were preserved."
        }
        "Restart" {
            Invoke-DockerCompose -Context $context -Arguments @("restart") | Out-Null
            Invoke-DockerCompose -Context $context -Arguments @("up", "--detach", "--wait") | Out-Null
            Wait-ComposeHealthy -Context $context -TimeoutSeconds $TimeoutSeconds
            Write-Host "Local environment restarted with named volumes preserved."
        }
        "Status" {
            Invoke-DockerCompose -Context $context -Arguments @("ps", "--all") | Out-Null
        }
        "Logs" {
            Invoke-DockerCompose -Context $context -Arguments @("logs", "--tail", "200") | Out-Null
        }
        "Smoke" {
            $smokeScript = Join-Path $PSScriptRoot "test-local-environment.ps1"
            & $smokeScript `
                -ComposeFile $context.ComposeFile `
                -EnvironmentFile $context.EnvironmentFile `
                -ProjectName $context.ProjectName `
                -TimeoutSeconds $TimeoutSeconds `
                -KeepResources
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
        "Reset" {
            if (-not $Force) {
                $answer = Read-Host "Reset permanently deletes volumes for '$($context.ProjectName)'. Type RESET to continue"
                if ($answer -cne "RESET") {
                    throw "Reset cancelled; no resources were deleted."
                }
            }
            $unrelatedBefore = Get-UnrelatedResourceSnapshot -ProjectName $context.ProjectName
            Invoke-DockerCompose -Context $context -Arguments @("down", "--volumes", "--remove-orphans") | Out-Null
            Assert-ProjectResourcesAbsent -ProjectName $context.ProjectName
            Assert-UnrelatedResourcesPreserved -Snapshot $unrelatedBefore
            Write-Host "Reset completed for '$($context.ProjectName)'; its containers, network, and volumes were removed."
        }
    }
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
