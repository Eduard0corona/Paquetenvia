targetScope = 'resourceGroup'

@description('The single DEV_SYNTHETIC Azure region selected by the preflight.')
param location string = resourceGroup().location

@secure()
@description('Random PostgreSQL administrator password. Supply through a protected parameter file; never commit it.')
param postgresAdministratorPassword string

var suffix = uniqueString(subscription().id, resourceGroup().id)
var tags = {
  project: 'Paquetenvia'
  environment: 'DEV_SYNTHETIC'
  contract: 'AZR-001'
  dataClassification: 'SYNTHETIC_ONLY'
  productionPattern: 'NOT_PRODUCTION_PATTERN'
  cleanupGroup: 'AZR-001'
}

var identityNames = [
  'mi-azr-dev-migrate'
  'mi-azr-dev-prereq-seed'
  'mi-azr-dev-api'
  'mi-azr-dev-worker'
  'mi-azr-dev-web'
  'mi-azr-dev-acceptance'
  'mi-azr-dev-rls'
]

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: 'law-pv-azrdev-${suffix}'
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: 'cae-pv-azrdev-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'pvazrdev${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-pv-${suffix}'
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'pg-pv-azrdev-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: 'pvazradmin'
    administratorLoginPassword: postgresAdministratorPassword
    version: '18'
    storage: {
      storageSizeGB: 32
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
  }
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'paqueteria'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource postgresFirewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'allow-azure-services'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource secureTransport 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'require_secure_transport'
  properties: {
    value: 'ON'
    source: 'user-override'
  }
}

resource allowedExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    value: 'POSTGIS,PGCRYPTO'
    source: 'user-override'
  }
}

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for identityName in identityNames: {
  name: identityName
  location: location
  tags: tags
}]

// AcrPull is the non-ABAC registry role. No ACR admin account or pull password exists.
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (identityName, i) in identityNames: {
  name: guid(registry.id, identityName, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identities[i].properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output postgresName string = postgres.name
output postgresHost string = postgres.properties.fullyQualifiedDomainName
output vaultName string = vault.name
output containerEnvironmentName string = containerEnvironment.name
output containerEnvironmentDefaultDomain string = containerEnvironment.properties.defaultDomain
output identities array = [for (identityName, i) in identityNames: {
  name: identityName
  id: identities[i].id
  principalId: identities[i].properties.principalId
}]
