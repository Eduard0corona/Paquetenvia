targetScope = 'resourceGroup'

param location string = resourceGroup().location

var suffix = uniqueString(subscription().id, resourceGroup().id)

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-pv-${suffix}'
  location: location
  tags: {
    project: 'Paquetenvia'
    environment: 'DEV_SYNTHETIC'
    contract: 'AZR-001'
    dataClassification: 'SYNTHETIC_ONLY'
    productionPattern: 'NOT_PRODUCTION_PATTERN'
    cleanupGroup: 'AZR-001'
  }
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

output vaultName string = vault.name
output vaultId string = vault.id
