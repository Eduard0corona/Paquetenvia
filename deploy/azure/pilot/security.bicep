// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 1 of 4: workload identities, Key Vault and its keys.
// Deployed before anything that needs a secret, so the workflow can persist generated credentials
// (PostgreSQL administrator password, EmailLookup key) before the resources that consume them exist.
targetScope = 'resourceGroup'

@description('Pilot region. The workflow refuses any resource group that is not in mexicocentral.')
param location string = resourceGroup().location

var suffix = uniqueString(subscription().id, resourceGroup().id)
var tags = {
  project: 'Paquetenvia'
  environment: 'PILOT'
  contract: 'ENV-001'
  dataClassification: 'REAL_PEOPLE'
  cleanupGroup: 'ENV-001'
}

// Built-in role definition ids (never Owner, Contributor or User Access Administrator).
var roles = {
  // Key Vault Secrets Officer (deployer only)
  kvOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  keyVaultCryptoServiceEncryptionUser: 'e147488a-f6f5-4113-8e2d-b22465e65bf6'
}

var identityNames = [
  'id-pv-pilot-api'
  'id-pv-pilot-worker'
  'id-pv-pilot-web'
  'id-pv-pilot-migrate'
]

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = [for name in identityNames: {
  name: name
  location: location
  tags: tags
}]

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: 'kv-pvp-${take(suffix, 13)}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    // The GitHub-hosted runner writes generated secrets and Container Apps resolve secret references
    // over the public endpoint; every data-plane call still requires an Entra RBAC role.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// ADP-001-PII-KEYVAULT-ENVELOPE: RSA key that wraps the per-value AES-256-GCM data keys of
// Locations and Incidents PII. Rotation adds versions; earlier versions must stay enabled.
resource piiKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: 'pii-kek'
  tags: tags
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
  }
}

// SCL-001 / ENV-001: key-encryption key for the PostgreSQL Data Protection key ring.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: 'dataprotection-kek'
  tags: tags
  properties: {
    kty: 'RSA'
    keySize: 3072
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
  }
}

// The deployment principal (GitHub OIDC service principal) writes generated secrets. It never
// receives a crypto role, so it cannot unwrap PII or Data Protection keys.
resource deployerSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, deployer().objectId, roles.kvOfficer)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.kvOfficer)
    principalId: deployer().objectId
  }
}

// ADP-001 RBAC table: API wraps/unwraps PII and Data Protection keys; Worker only the Data Protection key.
resource apiPiiKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(piiKey.id, identityNames[0], roles.keyVaultCryptoServiceEncryptionUser)
  scope: piiKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoServiceEncryptionUser)
    principalId: identities[0].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiDataProtectionKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionKey.id, identityNames[0], roles.keyVaultCryptoServiceEncryptionUser)
  scope: dataProtectionKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoServiceEncryptionUser)
    principalId: identities[0].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workerDataProtectionKey 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionKey.id, identityNames[1], roles.keyVaultCryptoServiceEncryptionUser)
  scope: dataProtectionKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoServiceEncryptionUser)
    principalId: identities[1].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
output piiKeyUri string = '${vault.properties.vaultUri}keys/${piiKey.name}'
output dataProtectionKeyUri string = '${vault.properties.vaultUri}keys/${dataProtectionKey.name}'
