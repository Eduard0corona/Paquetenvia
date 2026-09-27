// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 4 of 4: Web, API and Worker Container Apps and the
// PILOT-SAME-ORIGIN-ROUTING rule-based route on the apex domain.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Immutable image references (<registry>/<repository>@sha256:<digest>).')
param apiImage string
param workerImage string
param webImage string

@description('Apex host of the pilot (PILOT-DOMAIN-PRODUCTION).')
param publicHost string = 'paquetenvia.com'

@description('none: route only on the environment FQDN. bind: also bind publicHost with a managed certificate (after the DNS records exist).')
@allowed([
  'none'
  'bind'
])
param customDomainPhase string = 'none'

@description('AuthCenter production client (auth-001-authcenter-bff.md section 10.1).')
param authCenterAuthority string = 'https://authcenter.info'
param authCenterIssuer string = 'https://authcenter.info'
param authCenterClientId string = 'paquetenvia-web-prod'

@description('Business and operating-policy settings reviewed by the owner (name/value pairs, .NET double-underscore names).')
param businessSettings array

@description('Versionless Key Vault key URIs from security.bicep.')
param piiKeyUri string
param dataProtectionKeyUri string

@description('Blob service endpoint from platform.bicep (https://<account>.blob.core.windows.net).')
param blobServiceUri string

@description('Container Apps infrastructure subnet, trusted for X-Forwarded-For/Proto (the environment ingress runs there).')
param trustedProxyNetwork string = '10.60.0.0/23'

var suffix = uniqueString(subscription().id, resourceGroup().id)
var tags = {
  project: 'Paquetenvia'
  environment: 'PILOT'
  contract: 'ENV-001'
  dataClassification: 'REAL_PEOPLE'
  cleanupGroup: 'ENV-001'
}
var publicOrigin = 'https://${publicHost}'
// Key Vault Secrets User (built-in), assigned per individual secret.
var kvReaderRole = '4633458b-17de-408a-b874-0445c86b69e6'
var apiSecretNames = [
  'pg-api-runtime-connection'
  'authcenter-paquetenvia-client-secret'
  'paquetenvia-email-lookup-key-1'
]
var workerSecretNames = [
  'pg-worker-runtime-connection'
]

resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: 'cae-pv-pilot-${suffix}'
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: 'pvpilot${suffix}'
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: 'kv-pvp-${take(suffix, 13)}'
}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: 'id-pv-pilot-api'
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: 'id-pv-pilot-worker'
}

resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: 'id-pv-pilot-web'
}

resource apiSecrets 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = [for name in apiSecretNames: {
  parent: vault
  name: name
}]

resource workerSecrets 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = [for name in workerSecretNames: {
  parent: vault
  name: name
}]

// Key Vault Secrets User on each individual secret, never on the vault.
resource apiSecretReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in apiSecretNames: {
  name: guid(apiSecrets[i].id, apiIdentity.id, kvReaderRole)
  scope: apiSecrets[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvReaderRole)
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource workerSecretReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in workerSecretNames: {
  name: guid(workerSecrets[i].id, workerIdentity.id, kvReaderRole)
  scope: workerSecrets[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvReaderRole)
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

var productionEnv = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'DOTNET_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'PAQUETERIA_DEPLOYMENT_CLASS'
    value: 'PILOT_REAL_PEOPLE'
  }
]

// ADP-001 configuration contract (docs/development/adp-001-production-adapters.md), shared by API and Worker.
var proofStorageEnv = [
  {
    name: 'ProofStorage__Provider'
    value: 'AzureBlob'
  }
  {
    name: 'ProofStorage__ThreatScanner'
    value: 'DefenderForStorage'
  }
  {
    name: 'ProofStorage__AzureBlob__ServiceUri'
    value: blobServiceUri
  }
  {
    name: 'ProofStorage__AzureBlob__ContainerName'
    value: 'proofs'
  }
]

var dataProtectionEnv = [
  {
    name: 'DataProtection__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'DataProtection__KeyEncryption__Provider'
    value: 'AzureKeyVault'
  }
  {
    name: 'DataProtection__KeyEncryption__AzureKeyVault__KeyId'
    value: dataProtectionKeyUri
  }
]

var apiEnv = concat(productionEnv, proofStorageEnv, dataProtectionEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: apiIdentity.properties.clientId
  }
  {
    name: 'ConnectionStrings__Paqueteria'
    secretRef: 'pg-api-conn'
  }
  {
    name: 'Authentication__Provider'
    value: 'AuthCenter'
  }
  {
    name: 'AuthCenter__Authority'
    value: authCenterAuthority
  }
  {
    name: 'AuthCenter__Issuer'
    value: authCenterIssuer
  }
  {
    name: 'AuthCenter__ClientId'
    value: authCenterClientId
  }
  {
    name: 'AuthCenter__ClientSecret'
    secretRef: 'authcenter-client-secret'
  }
  {
    name: 'AuthCenter__PublicOrigin'
    value: publicOrigin
  }
  {
    name: 'AuthCenter__SessionStore'
    value: 'PostgreSql'
  }
  {
    name: 'EmailLookup__CurrentKeyVersion'
    value: '1'
  }
  {
    name: 'EmailLookup__Keys__1'
    secretRef: 'email-lookup-key-1'
  }
  {
    name: 'IdentityBootstrap__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Tenancy__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Locations__Provider'
    value: 'PostgreSql'
  }
  // GATE-003 (Google Maps Platform) is not implemented yet: operators enter coordinates manually.
  {
    name: 'Locations__GeocodingProvider'
    value: 'Manual'
  }
  {
    name: 'Locations__PiiProtector'
    value: 'AzureKeyVault'
  }
  {
    name: 'Incidents__PiiProtector'
    value: 'AzureKeyVault'
  }
  {
    name: 'PiiProtection__AzureKeyVault__KeyId'
    value: piiKeyUri
  }
  {
    name: 'Pricing__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Orders__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'PublicTracking__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'PublicTracking__AllowedOrigins__0'
    value: publicOrigin
  }
  {
    name: 'Drivers__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Dispatch__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Routing__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Finance__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'OperationsDashboard__Provider'
    value: 'PostgreSql'
  }
  // GATE-013: SignalR in process, single API replica, no Redis and no Azure SignalR Service.
  {
    name: 'Realtime__Provider'
    value: 'SignalR'
  }
  {
    name: 'Realtime__Backplane'
    value: 'InProcess'
  }
  {
    name: 'Realtime__AllowedOrigins__0'
    value: publicOrigin
  }
  {
    name: 'Realtime__OutboxDispatcher__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Realtime__OutboxDispatcher__WorkerId'
    value: 'pilot-api'
  }
  {
    name: 'Http__ForwardedHeaders__KnownNetworks__0'
    value: trustedProxyNetwork
  }
  {
    name: 'Http__ForwardedHeaders__ForwardLimit'
    value: '1'
  }
], businessSettings)

var workerEnv = concat(productionEnv, proofStorageEnv, dataProtectionEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: workerIdentity.properties.clientId
  }
  {
    name: 'Urls'
    value: 'http://+:8080'
  }
  {
    name: 'ConnectionStrings__Paqueteria'
    secretRef: 'pg-worker-conn'
  }
  {
    name: 'ConnectionStrings__PaqueteriaWorker'
    secretRef: 'pg-worker-conn'
  }
  {
    name: 'Drivers__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Dispatch__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Dispatch__AssignmentLifecycle__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Dispatch__AssignmentLifecycle__WorkerId'
    value: 'pilot-worker'
  }
  {
    name: 'Notifications__Provider'
    value: 'PostgreSql'
  }
  {
    name: 'Notifications__WorkerId'
    value: 'pilot-worker'
  }
  {
    name: 'Orders__ClaimWindowFinalization__Enabled'
    value: 'true'
  }
], businessSettings)

var dotnetProbes = [
  {
    type: 'Liveness'
    httpGet: {
      path: '/health/live'
      port: 8080
    }
    periodSeconds: 30
    failureThreshold: 3
  }
  {
    type: 'Readiness'
    httpGet: {
      path: '/health/ready'
      port: 8080
    }
    periodSeconds: 15
    failureThreshold: 3
  }
]

resource api 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-pv-pilot-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // Reachable only through the environment route (PILOT-SAME-ORIGIN-ROUTING), never on its own FQDN.
      ingress: {
        external: false
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: apiIdentity.id
        }
      ]
      secrets: [
        {
          name: 'pg-api-conn'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-api-runtime-connection'
          identity: apiIdentity.id
        }
        {
          name: 'authcenter-client-secret'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/authcenter-paquetenvia-client-secret'
          identity: apiIdentity.id
        }
        {
          name: 'email-lookup-key-1'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/paquetenvia-email-lookup-key-1'
          identity: apiIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          env: apiEnv
          probes: dotnetProbes
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      // GATE-013: exactly one API replica (SignalR InProcess).
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    apiSecretReaders
  ]
}

resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-pv-pilot-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: registry.properties.loginServer
          identity: workerIdentity.id
        }
      ]
      secrets: [
        {
          name: 'pg-worker-conn'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/pg-worker-runtime-connection'
          identity: workerIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          env: workerEnv
          probes: dotnetProbes
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    workerSecretReaders
  ]
}

resource web 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-pv-pilot-web'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${webIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 3000
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: webIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          env: [
            {
              name: 'NODE_ENV'
              value: 'production'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

// PILOT-SAME-ORIGIN-ROUTING: /api, /hubs, /auth and /signin-authcenter go to the API, everything else to Web.
// Rules are evaluated in order; path-separated prefixes do not capture look-alikes such as /authx.
resource routes 'Microsoft.App/managedEnvironments/httpRouteConfigs@2025-07-01' = {
  parent: containerEnvironment
  name: 'pvpilotroutes'
  properties: {
    customDomains: customDomainPhase == 'bind'
      ? [
          {
            name: publicHost
            bindingType: 'Auto'
          }
        ]
      : []
    rules: [
      {
        description: 'API same-origin prefixes'
        routes: [
          {
            match: {
              pathSeparatedPrefix: '/api'
            }
          }
          {
            match: {
              pathSeparatedPrefix: '/hubs'
            }
          }
          {
            match: {
              pathSeparatedPrefix: '/auth'
            }
          }
          {
            match: {
              path: '/signin-authcenter'
            }
          }
        ]
        targets: [
          {
            containerApp: api.name
          }
        ]
      }
      {
        description: 'Web for every other path'
        routes: [
          {
            match: {
              prefix: '/'
            }
          }
        ]
        targets: [
          {
            containerApp: web.name
          }
        ]
      }
    ]
  }
}

// Free managed certificate for the apex; HTTP validation needs the A and asuid TXT records first.
// With bindingType Auto the route picks the certificate up as soon as it is issued.
resource managedCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2025-07-01' = if (customDomainPhase == 'bind') {
  parent: containerEnvironment
  name: 'mc-${replace(publicHost, '.', '-')}'
  location: location
  tags: tags
  properties: {
    subjectName: publicHost
    domainControlValidation: 'HTTP'
  }
  dependsOn: [
    routes
  ]
}

output routeFqdn string = routes.properties.fqdn
output publicOrigin string = publicOrigin
output apiName string = api.name
output workerName string = worker.name
output webName string = web.name
