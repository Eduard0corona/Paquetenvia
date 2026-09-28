// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 3 of 5: manual Container Apps Jobs that run the canonical
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
// Least privilege per job identity: migrate and verify read only the migration connection; the logins job
// (its own identity) reads the migration connection and the two SCRAM verifiers.
var migrateSecretNames = [
  'pg-migrate-connection'
]
var loginsSecretNames = [
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


// PILOT-KEYVAULT-PRIVATE-APP-READ: the migrator reads its settings itself (ADP-001 Key Vault secrets
// source); no Container Apps Key Vault reference and no secret in the job definition.
var migrateKeyVaultEnv = [
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
  {
    name: 'KeyVaultSecrets__VaultUri'
    value: vault.properties.vaultUri
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__SecretName'
    value: 'pg-migrate-connection'
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__ConfigurationKey'
    value: 'PAQUETERIA_MIGRATION_CONNECTION'
  }
]

var loginsKeyVaultEnv = [
  {
    name: 'AZURE_CLIENT_ID'
    value: loginsIdentity.properties.clientId
  }
  {
    name: 'KeyVaultSecrets__VaultUri'
    value: vault.properties.vaultUri
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__SecretName'
    value: 'pg-migrate-connection'
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__ConfigurationKey'
    value: 'PAQUETERIA_MIGRATION_CONNECTION'
  }
  {
    name: 'KeyVaultSecrets__Mappings__1__SecretName'
    value: 'pg-api-login-verifier'
  }
  {
    name: 'KeyVaultSecrets__Mappings__1__ConfigurationKey'
    value: 'PAQUETERIA_API_LOGIN_VERIFIER'
  }
  {
    name: 'KeyVaultSecrets__Mappings__2__SecretName'
    value: 'pg-worker-login-verifier'
  }
  {
    name: 'KeyVaultSecrets__Mappings__2__ConfigurationKey'
    value: 'PAQUETERIA_WORKER_LOGIN_VERIFIER'
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

resource loginsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: 'id-pv-pilot-logins'
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

resource loginsSecrets 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = [for name in loginsSecretNames: {
  parent: vault
  name: name
}]

resource loginsSecretReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in loginsSecretNames: {
  name: guid(loginsSecrets[i].id, loginsIdentity.id, kvReaderRole)
  scope: loginsSecrets[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvReaderRole)
    principalId: loginsIdentity.properties.principalId
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
          env: concat(classificationEnv, migrateKeyVaultEnv)
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
      '${loginsIdentity.id}': {}
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
          identity: loginsIdentity.id
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
          env: concat(classificationEnv, loginsKeyVaultEnv)
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    loginsSecretReaders
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
          env: concat(classificationEnv, migrateKeyVaultEnv)
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
