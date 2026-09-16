param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,
    [Parameter(Mandatory = $true)]
    [string]$DbOpsDigest,
    [string]$ResourceGroup = 'rg-pv-azr001-devsynthetic'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($DbOpsDigest -cnotmatch '^sha256:[0-9a-f]{64}$') {
    throw 'An immutable db-ops sha256 digest is required.'
}
$account = & az account show --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $account.id -cne $SubscriptionId -or $account.state -cne 'Enabled') {
    throw 'STOP_FOR_OWNER_DECISION: selected subscription is unavailable.'
}

$core = & az deployment group show --resource-group $ResourceGroup --name 'azr-001-core' `
    --query properties.outputs --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $core) {
    throw 'AZR-001 core deployment must succeed before the migration job.'
}
$vaultName = $core.vaultName.value
$postgresHost = $core.postgresHost.value
$registryServer = $core.registryServer.value
$image = "$registryServer/paquetenvia-db-ops@$DbOpsDigest"
$password = & az keyvault secret show --vault-name $vaultName --name 'pg-admin-password' `
    --query value --output tsv
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($password)) {
    throw 'PostgreSQL administrator credential is unavailable in Key Vault.'
}

$connectionFile = [System.IO.Path]::GetTempFileName()
try {
    $connection = "Host=$postgresHost;Database=paqueteria;Username=pvazradmin;Password=$password;SSL Mode=Require;Maximum Pool Size=4;Minimum Pool Size=0;Timeout=15"
    [System.IO.File]::WriteAllText($connectionFile, $connection,
        [System.Text.UTF8Encoding]::new($false))
    & az keyvault secret set --vault-name $vaultName --name 'pg-migrate-connection' `
        --file $connectionFile --output none
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot store migration connection in Key Vault.'
    }
}
finally {
    $connection = $null
    $password = $null
    Remove-Item -LiteralPath $connectionFile -Force -ErrorAction SilentlyContinue
}

$template = Join-Path $PSScriptRoot 'migration-job.bicep'
$imageParameter = 'dbOpsImage=' + $image
& az deployment group validate --resource-group $ResourceGroup --template-file $template `
    --parameters $imageParameter --output none
if ($LASTEXITCODE -ne 0) {
    throw 'Migration job Bicep validation failed.'
}
& az deployment group create --resource-group $ResourceGroup --name 'azr-001-migration-job' `
    --template-file $template --parameters $imageParameter --output none
if ($LASTEXITCODE -ne 0) {
    throw 'Migration job deployment failed.'
}

Write-Output 'AZR-001 migration job deployed by immutable image digest.'
