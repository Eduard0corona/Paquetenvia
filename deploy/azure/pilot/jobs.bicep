// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 3 of 4: manual Container Apps Jobs that run the canonical
// DatabaseMigrator inside the VNet (the database has no public endpoint). No app runs migrations at startup.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Immutable db-ops image reference (<registry>/paquetenvia-db-ops@sha256:<digest>).')
param dbOpsImage string

var suffix = uniqueString(subscription().id, resourceGroup().id)
var tags = {
  project: 'Paquetenvia'
  environment: 'PILOT'
  contract: 'ENV-001'
  dataClassification: 'REAL_PEOPLE'
  cleanupGroup: 'ENV-001'
}
// Key Vault Secrets User (built-in), assigned per individual secret.
var kvReaderRole = '4633458b-17de-408a-b874-0445c86b69e6'
var migrateSecretNames = [
  'pg-migrate-connection'
  'pg-api-login-verifier'
  'pg-worker-login-verifier'
]

// Classification that lets the E-002 ownership bridge run on the pilot database (exact pair, see
// AzureOwnershipBridgeSelection) and that the SEC-003 synthetic policy never treats as DevSynthetic.
var classificationEnv = [
  {
    name: 'DOTNET_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'PAQUETERIA_DEPLOYMENT_CLASS'
    value: 'PILOT_REAL_PEOPLE'
  }
  {
    name: 'PAQUETERIA_DB_DEPLOYMENT_PROVIDER'
    value: 'AZURE_POSTGRESQL_FLEXIBLE_SERVER'
  }
]

resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: 'cae-pv-pilot-${suffix}'
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: 'pvpilot${suffix}'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: 'id-pv-pilot-migrate'
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: 'kv-pvp-${take(suffix, 13)}'
}

resource migrateSecrets 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = [for name in migrateSecretNames: {
  parent: vault
  name: name
}]

resource migrateSecretReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in migrateSecretNames: {
  name: guid(migrateSecrets[i].id, identity.id, kvReaderRole)
  scope: migrateSecrets[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvReaderRole)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

// Clean path: AI-06 + AI-18 through the E-002 bridge, then EVERY module lane; Applied path: pending lanes only.
resource migrationJob 'Microsoft.App/jobs@2025-07-01' = {
  name: 'job-pv-pilot-migrate'
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
    workloadProfileName: 'Consumption'
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
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-migrate-connection'
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
          env: concat(classificationEnv, [
            {
              name: 'PAQUETERIA_MIGRATION_CONNECTION'
              secretRef: 'pg-migrate-conn'
            }
          ])
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    migrateSecretReaders
  ]
}

// Least-privilege runtime LOGIN roles (pv_pilot_api -> paqueteria_app, pv_pilot_worker -> paqueteria_worker)
// from SCRAM verifiers; the plaintext runtime passwords exist only in the runtime connection secrets.
resource runtimeLoginsJob 'Microsoft.App/jobs@2025-07-01' = {
  name: 'job-pv-pilot-logins'
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
    workloadProfileName: 'Consumption'
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
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-migrate-connection'
          identity: identity.id
        }
        {
          name: 'pg-api-verifier'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-api-login-verifier'
          identity: identity.id
        }
        {
          name: 'pg-worker-verifier'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-worker-login-verifier'
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'db-runtime-logins'
          image: dbOpsImage
          command: [
            'dotnet'
            '/app/migrator/Paqueteria.DatabaseMigrator.dll'
          ]
          args: [
            'runtime-logins'
            '--connection-env'
            'PAQUETERIA_MIGRATION_CONNECTION'
          ]
          env: concat(classificationEnv, [
            {
              name: 'PAQUETERIA_MIGRATION_CONNECTION'
              secretRef: 'pg-migrate-conn'
            }
            {
              name: 'PAQUETERIA_API_LOGIN_VERIFIER'
              secretRef: 'pg-api-verifier'
            }
            {
              name: 'PAQUETERIA_WORKER_LOGIN_VERIFIER'
              secretRef: 'pg-worker-verifier'
            }
          ])
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    migrateSecretReaders
  ]
}

// Read-only verification (`assert`): run after every deployment against the pilot database, and by the
// restore drill (restore-drill.sh) against a point-in-time restored server by re-pointing `pg-verify-conn`.
resource verifyJob 'Microsoft.App/jobs@2025-07-01' = {
  name: 'job-pv-pilot-verify'
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
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
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
          name: 'pg-verify-conn'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-migrate-connection'
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'db-verify'
          image: dbOpsImage
          command: [
            'dotnet'
            '/app/migrator/Paqueteria.DatabaseMigrator.dll'
          ]
          args: [
            'assert'
            '--connection-env'
            'PAQUETERIA_MIGRATION_CONNECTION'
          ]
          env: concat(classificationEnv, [
            {
              name: 'PAQUETERIA_MIGRATION_CONNECTION'
              secretRef: 'pg-verify-conn'
            }
          ])
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    migrateSecretReaders
  ]
}

output migrationJobName string = migrationJob.name
output runtimeLoginsJobName string = runtimeLoginsJob.name
output verifyJobName string = verifyJob.name
