// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 1 of 5: workload identities, Key Vault and its keys.
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

var addressSpace = '10.60.0.0/22'
var containerAppsSubnetPrefix = '10.60.0.0/23'
var postgresSubnetPrefix = '10.60.2.0/28'

var identityNames = [
  'id-pv-pilot-api'
  'id-pv-pilot-worker'
  'id-pv-pilot-web'
  'id-pv-pilot-migrate'
  'id-pv-pilot-logins'
]

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = [for name in identityNames: {
  name: name
  location: location
  tags: tags
}]

// Network: created in stage 1 so the Key Vault firewall can admit the Container Apps subnet.
resource vnet 'Microsoft.Network/virtualNetworks@2024-07-01' = {
  name: 'vnet-pv-pilot'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressSpace
      ]
    }
    subnets: [
      {
        name: 'snet-containerapps'
        properties: {
          addressPrefix: containerAppsSubnetPrefix
          delegations: [
            {
              name: 'containerapps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
          // Free service endpoints: Blob and Key Vault traffic from the workloads stays on the Azure backbone.
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
            }
            {
              service: 'Microsoft.KeyVault'
            }
          ]
        }
      }
      {
        name: 'snet-postgres'
        properties: {
          addressPrefix: postgresSubnetPrefix
          delegations: [
            {
              name: 'postgres'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
    ]
  }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-07-01' existing = {
  parent: vnet
  name: 'snet-containerapps'
}

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
    // PILOT-KEYVAULT-PRIVATE-APP-READ: deny by default. Only the Container Apps subnet (Microsoft.KeyVault
    // service endpoint) reaches the data plane; the apps and jobs read their secrets and keys themselves
    // with their managed identities. No trusted-service bypass. The deployment workflow adds its runner's
    // IP as a temporary rule around its secret writes and always removes it (kv-firewall.sh); every
    // redeploy of this template resets ipRules to empty.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: [
        {
          id: containerAppsSubnet.id
          ignoreMissingVnetServiceEndpoint: false
        }
      ]
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
