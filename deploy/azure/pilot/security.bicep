// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 1 of 6: workload identities, Key Vault and its keys.
// Deployed before anything that needs a secret, so the workflow can persist generated credentials
// (PostgreSQL administrator password, EmailLookup key) before the resources that consume them exist.
targetScope = 'resourceGroup'

@description('Pilot region. The workflow refuses any resource group that is not in mexicocentral.')
param location string = resourceGroup().location

// NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W5 "Un rol propio y uno de Azure (Recommended)"): no built-in role only
// unwraps, so the Worker's role on recipient-notice-kek is the owner's custom role
// "Paquetenvia Pilot Recipient Notice Unwrap" (data actions keys/read and keys/unwrap/action only), created once in the
// resource group (docs/operations/env-001-pilot/guia-owner-avisos.md, paso 2) and looked up by exact name by the workflow.
@description('Resource id of the custom role granting only keys/read and keys/unwrap/action (guia-owner-avisos.md, paso 2).')
param recipientNoticeUnwrapRoleDefinitionId string

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
  // Key Vault Reader: keys/read returns the public part of an RSA key, which is all the API needs to wrap in process.
  keyVaultReader: '21090545-7ca7-4776-b22c-e363652d74d2'
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

// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2) and NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W11 item 3): the RSA key
// of the recipient-notice copies, distinct from the PII and Data Protection keys (AI-24 recipient_notices.contact). The
// template creates it like pii-kek; a redeploy does not add a version, and rotation adds versions that stay enabled.
resource recipientNoticeKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: 'recipient-notice-kek'
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

// AI-24 recipient_notices.contact.roles: the API only wraps with the notice key (in process, with the public part it reads)
// and never unwraps; the Worker only unwraps and reads its versions. Each role is scoped to the key alone, and the Worker
// still holds nothing on pii-kek.
resource apiRecipientNoticeKeyReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(recipientNoticeKey.id, identityNames[0], roles.keyVaultReader)
  scope: recipientNoticeKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultReader)
    principalId: identities[0].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource workerRecipientNoticeKeyUnwrap 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(recipientNoticeKey.id, identityNames[1], 'recipient-notice-unwrap')
  scope: recipientNoticeKey
  properties: {
    roleDefinitionId: recipientNoticeUnwrapRoleDefinitionId
    principalId: identities[1].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
output piiKeyUri string = '${vault.properties.vaultUri}keys/${piiKey.name}'
output dataProtectionKeyUri string = '${vault.properties.vaultUri}keys/${dataProtectionKey.name}'
output recipientNoticeKeyUri string = '${vault.properties.vaultUri}keys/${recipientNoticeKey.name}'
