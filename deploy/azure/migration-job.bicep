targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Immutable ACR image reference including sha256 digest.')
param dbOpsImage string

var suffix = uniqueString(subscription().id, resourceGroup().id)
var tags = {
  project: 'Paquetenvia'
  environment: 'DEV_SYNTHETIC'
  contract: 'AZR-001'
  dataClassification: 'SYNTHETIC_ONLY'
  productionPattern: 'NOT_PRODUCTION_PATTERN'
  cleanupGroup: 'AZR-001'
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: 'cae-pv-azrdev-${suffix}'
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: 'pvazrdev${suffix}'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'mi-azr-dev-migrate'
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: 'kv-pv-${suffix}'
}

resource migrationSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: 'pg-migrate-connection'
}

resource migrationSecretReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(migrationSecret.id, identity.id, 'Key Vault Secrets User')
  scope: migrationSecret
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource migrationJob 'Microsoft.App/jobs@2025-01-01' = {
  name: 'job-pv-azrdev-migrate'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: [
        {
          name: 'pg-migrate-conn'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/${migrationSecret.name}'
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'db-migrator'
          image: dbOpsImage
          command: [
            'dotnet'
            '/app/migrator/Paqueteria.DatabaseMigrator.dll'
          ]
          args: [
            'apply'
            '--connection-env'
            'PAQUETERIA_MIGRATION_CONNECTION'
            '--confirm-initial-baseline'
            '--azure-ownership-bridge'
          ]
          env: [
            {
              name: 'DOTNET_ENVIRONMENT'
              value: 'DevSynthetic'
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'DevSynthetic'
            }
            {
              name: 'PAQUETERIA_DEPLOYMENT_CLASS'
              value: 'DEV_SYNTHETIC'
            }
            {
              name: 'PAQUETERIA_DB_DEPLOYMENT_PROVIDER'
              value: 'AZURE_POSTGRESQL_FLEXIBLE_SERVER'
            }
            {
              name: 'PAQUETERIA_MIGRATION_CONNECTION'
              secretRef: 'pg-migrate-conn'
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    migrationSecretReader
  ]
}

output migrationJobName string = migrationJob.name
