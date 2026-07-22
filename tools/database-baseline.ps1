[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet("Verify", "Plan", "Apply", "Assert")]
    [string] $Operation,

    [Parameter()]
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string] $ConnectionEnvironment,

    [Parameter()]
    [switch] $ConfirmInitialBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "Paqueteria.DatabaseMigrator/Paqueteria.DatabaseMigrator.csproj"
$arguments = @("run", "--project", $project, "--", $Operation.ToLowerInvariant())

if ($Operation -ne "Verify") {
    if ([string]::IsNullOrWhiteSpace($ConnectionEnvironment)) {
        throw "$Operation requires -ConnectionEnvironment with the name of an environment variable."
    }

    $arguments += @("--connection-env", $ConnectionEnvironment)
}

if ($Operation -eq "Apply") {
    if (-not $ConfirmInitialBaseline) {
        throw "Apply requires -ConfirmInitialBaseline."
    }

    $arguments += "--confirm-initial-baseline"
}
elseif ($ConfirmInitialBaseline) {
    throw "-ConfirmInitialBaseline is valid only for Apply."
}

Push-Location $repositoryRoot
try {
    & dotnet @arguments
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
