param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,
    [string]$Location = 'mexicocentral',
    [string]$ResourceGroup = 'rg-pv-azr001-devsynthetic'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AzureCli {
    param([string[]]$Arguments)
    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$templatePath = Join-Path $PSScriptRoot 'core.bicep'
$vaultTemplatePath = Join-Path $PSScriptRoot 'vault.bicep'
$expectedMain = 'b8dbb47664d58595f5f6a750c1f8856ea20e5acf'
$actualMain = (& git -C $repositoryRoot rev-parse origin/main).Trim()
if ($LASTEXITCODE -ne 0 -or $actualMain -cne $expectedMain) {
    throw 'STOP_FOR_CONTRACT_REVIEW: origin/main differs from the AZR-001 frozen baseline.'
}

$account = (& az account show --output json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $account.id -cne $SubscriptionId -or $account.state -cne 'Enabled') {
    throw 'STOP_FOR_OWNER_DECISION: the selected enabled subscription is unavailable.'
}

$capabilities = & az postgres flexible-server list-skus --location $Location --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    throw 'STOP_FOR_CONTRACT_REVIEW: PostgreSQL regional capabilities could not be read.'
}
$burst = @($capabilities[0].supportedServerEditions | Where-Object name -eq 'Burstable')
$b1ms = @($burst | ForEach-Object supportedServerSkus | Where-Object name -eq 'Standard_B1ms')
$storage32 = @($burst | ForEach-Object supportedStorageEditions | ForEach-Object supportedStorageMb |
    Where-Object storageSizeMb -eq 32768)
$pg18 = @($capabilities[0].supportedServerVersions | Where-Object name -eq '18')
if ($b1ms.Count -ne 1 -or $storage32.Count -lt 1 -or $pg18.Count -ne 1) {
    throw 'STOP_FOR_OWNER_DECISION: PG18/B1ms/32 GiB regional capabilities do not satisfy AZR-001.'
}

Invoke-AzureCli -Arguments @('group', 'create', '--name', $ResourceGroup, '--location', $Location,
    '--tags', 'project=Paquetenvia', 'environment=DEV_SYNTHETIC', 'contract=AZR-001',
    'dataClassification=SYNTHETIC_ONLY', 'productionPattern=NOT_PRODUCTION_PATTERN',
    '--output', 'none')

$parameterFile = [System.IO.Path]::GetTempFileName()
$passwordFile = [System.IO.Path]::GetTempFileName()
$password = $null
try {
    Invoke-AzureCli -Arguments @('deployment', 'group', 'create', '--name', 'azr-001-vault',
        '--resource-group', $ResourceGroup, '--template-file', $vaultTemplatePath,
        '--parameters', ('location=' + $Location), '--output', 'none')
    $vaultName = & az deployment group show --resource-group $ResourceGroup --name 'azr-001-vault' `
        --query 'properties.outputs.vaultName.value' --output tsv
    $vaultId = & az deployment group show --resource-group $ResourceGroup --name 'azr-001-vault' `
        --query 'properties.outputs.vaultId.value' --output tsv
    $operatorId = & az ad signed-in-user show --query id --output tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vaultName) -or
        [string]::IsNullOrWhiteSpace($vaultId) -or [string]::IsNullOrWhiteSpace($operatorId)) {
        throw 'Cannot resolve Key Vault or operator identity for secret provisioning.'
    }
    $existingAssignment = & az role assignment list --assignee $operatorId.Trim() `
        --scope $vaultId.Trim() --role 'Key Vault Secrets Officer' --query '[0].id' --output tsv
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot inspect Key Vault provisioning permissions.'
    }
    if ([string]::IsNullOrWhiteSpace($existingAssignment)) {
        Invoke-AzureCli -Arguments @('role', 'assignment', 'create', '--assignee-object-id',
            $operatorId.Trim(), '--assignee-principal-type', 'User', '--role',
            'Key Vault Secrets Officer', '--scope', $vaultId.Trim(), '--output', 'none')
    }
    $vaultReadable = $false
    for ($attempt = 1; $attempt -le 10 -and -not $vaultReadable; $attempt++) {
        try {
            $ErrorActionPreference = 'Continue'
            & az keyvault secret list --vault-name $vaultName.Trim() --output none 2>$null | Out-Null
            $vaultReadable = $LASTEXITCODE -eq 0
        }
        finally {
            $ErrorActionPreference = 'Stop'
        }
        if (-not $vaultReadable) {
            Start-Sleep -Seconds 10
        }
    }
    if (-not $vaultReadable) {
        throw 'Key Vault RBAC did not become effective; database was not deployed.'
    }

    $secretsJson = & az keyvault secret list --vault-name $vaultName.Trim() --output json
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot list Key Vault secrets before database deployment.'
    }
    $secrets = $secretsJson | ConvertFrom-Json
    if (@($secrets | Where-Object { $_.name -ceq 'pg-admin-password' }).Count -gt 0) {
        $password = & az keyvault secret show --vault-name $vaultName.Trim() `
            --name 'pg-admin-password' --query value --output tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($password)) {
            throw 'Cannot read the existing PostgreSQL admin credential.'
        }
    }
    else {
        $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $passwordBytes = New-Object byte[] 32
        $random.GetBytes($passwordBytes)
        $random.Dispose()
        $password = [BitConverter]::ToString($passwordBytes).Replace('-', '')
        [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
        [System.IO.File]::WriteAllText($passwordFile, $password,
            [System.Text.UTF8Encoding]::new($false))
        Invoke-AzureCli -Arguments @('keyvault', 'secret', 'set', '--vault-name', $vaultName.Trim(),
            '--name', 'pg-admin-password', '--file', $passwordFile, '--output', 'none')
    }

    $parameters = @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{
            location = @{ value = $Location }
            postgresAdministratorPassword = @{ value = $password }
        }
    }
    [System.IO.File]::WriteAllText($parameterFile, ($parameters | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))

    Invoke-AzureCli -Arguments @('deployment', 'group', 'validate', '--resource-group', $ResourceGroup,
        '--template-file', $templatePath, '--parameters', ('@' + $parameterFile), '--output', 'none')
    Invoke-AzureCli -Arguments @('deployment', 'group', 'create', '--name', 'azr-001-core',
        '--resource-group', $ResourceGroup, '--template-file', $templatePath,
        '--parameters', ('@' + $parameterFile), '--output', 'none')

}
finally {
    $password = $null
    Remove-Item -LiteralPath $parameterFile, $passwordFile -Force -ErrorAction SilentlyContinue
}

Write-Output 'AZR-001 core deployed. PostgreSQL administrator credential is in Key Vault.'
