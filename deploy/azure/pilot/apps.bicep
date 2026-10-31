// ENV-001 pilot (PILOT-REAL-PEOPLE), stage 4 of 6: Web, API and Worker Container Apps and the
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

// UI-001: terms and privacy notice versions the customer accepts on an operator-assisted order. Owner
// values from web.parameters.json (no default); env001_pilot_guards.py web-check refuses to deploy while
// either is OWNER_DECISION_REQUIRED or does not match the AI-05 format ^[A-Za-z0-9._-]{1,64}$.
@description('Accepted terms version for PAQUETERIA_TERMS_VERSION on the web container (AI-05 terms_version).')
@minLength(1)
@maxLength(64)
param webTermsVersion string

@description('Accepted privacy notice version for PAQUETERIA_PRIVACY_VERSION on the web container (AI-05 privacy_version).')
@minLength(1)
@maxLength(64)
param webPrivacyVersion string

@description('Versionless Key Vault key URIs from security.bicep.')
param piiKeyUri string
param dataProtectionKeyUri string
// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2): the recipient-notice key, which the API wraps with and the Worker unwraps
// with (security.bicep grants each half). Locations__NoticeKeyProtector stays Disabled (the default) until the
// gate-closing change, so no notice copy is written yet (NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31, W4).
param recipientNoticeKeyUri string

// NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W2, W11 item 4): the Communication Services resource the owner created
// by hand (NTF-EMAIL-ACS-PILOT-2026-10-11). Its name comes from the azure-pilot variable PILOT_ACS_NAME, never from the
// repository; the workflow checks that it exists before deploying.
@description('Name of the existing Communication Services resource in the pilot resource group (PILOT_ACS_NAME).')
@minLength(1)
@maxLength(63)
param acsName string

// W6 "Rol propio mínimo (Recommended)": the owner's custom role "Paquetenvia Pilot Email Sender" (the three actions
// Microsoft Learn gives for sending e-mail, never listKeys or regenerateKey), looked up by exact name by the workflow.
@description('Resource id of the custom role that only sends e-mail through the Communication Services resource (guia-owner-avisos.md, paso 2).')
param acsEmailSenderRoleDefinitionId string

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
  // RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the in-process realtime dispatchers' own login (paqueteria_realtime).
  'pg-realtime-runtime-connection'
  'authcenter-paquetenvia-client-secret'
  'paquetenvia-email-lookup-key-1'
  'google-maps-api-key'
  'public-tracking-link-key'
  // OBS-003-TENANT-PSEUDONYM-2026-10-12: the owner-written key of the organization pseudonym in logs and traces.
  'paquetenvia-telemetry-pseudonym-key-1'
  // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W7, W11 item 1; RECEIPTS O11 choice 11): the contact lookup key and the
  // webhook values. Every switch that would use them stays off until the gate-closing change.
  'paquetenvia-contact-lookup-key-1'
  'whatsapp-app-secret'
  'whatsapp-app-secret-previous'
  'whatsapp-webhook-verify-token'
  'whatsapp-webhook-verify-token-previous'
  'acs-email-events-secret'
  'acs-email-events-secret-previous'
  'whatsapp-phone-number-id'
  'acs-communication-resource-id'
]
var workerSecretNames = [
  'pg-worker-runtime-connection'
  'paquetenvia-telemetry-pseudonym-key-1'
  // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: the tracking link key, the contact lookup key and the WhatsApp token and
  // phone number id. The Worker never reads a webhook secret, and the API never reads the WhatsApp token.
  'public-tracking-link-key'
  'paquetenvia-contact-lookup-key-1'
  'whatsapp-cloud-api-token'
  'whatsapp-phone-number-id'
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

// OBS-003: the workspace-based Application Insights of platform.bicep (stage 2).
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: 'appi-pv-pilot-${suffix}'
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

resource communicationServices 'Microsoft.Communication/communicationServices@2023-04-01' existing = {
  name: acsName
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

// AI-24 recipient_notices.pilot_wiring ("the ACS role of the Worker"): the Worker sends e-mail with its managed identity
// (no ACS key or connection string anywhere), through the owner's least-privilege custom role, scoped to that resource
// only. Messaging__Email__Provider stays Disabled until the gate-closing change, so nothing is sent yet.
resource workerEmailSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(communicationServices.id, workerIdentity.id, 'acs-email-sender')
  scope: communicationServices
  properties: {
    roleDefinitionId: acsEmailSenderRoleDefinitionId
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

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

// PILOT-KEYVAULT-PRIVATE-APP-READ (ADP-001 "Key Vault secrets read by the application"): the API and
// the Worker read their secrets themselves at startup, from the VNet, with their managed identities.
// Each mapping must match the per-secret Key Vault Secrets User assignments above (guard P06).
var apiKeyVaultEnv = [
  {
    name: 'KeyVaultSecrets__VaultUri'
    value: vault.properties.vaultUri
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__SecretName'
    value: 'pg-api-runtime-connection'
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__ConfigurationKey'
    value: 'ConnectionStrings:Paqueteria'
  }
  {
    name: 'KeyVaultSecrets__Mappings__1__SecretName'
    value: 'authcenter-paquetenvia-client-secret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__1__ConfigurationKey'
    value: 'AuthCenter:ClientSecret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__2__SecretName'
    value: 'paquetenvia-email-lookup-key-1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__2__ConfigurationKey'
    value: 'EmailLookup:Keys:1'
  }
  // GATE-003-PROVIDER-GOOGLE: the Google Maps Platform key (owner-written, restricted in Google Cloud).
  {
    name: 'KeyVaultSecrets__Mappings__3__SecretName'
    value: 'google-maps-api-key'
  }
  {
    name: 'KeyVaultSecrets__Mappings__3__ConfigurationKey'
    value: 'Locations:GoogleMaps:ApiKey'
  }
  // TRK-002-AUTO-LINK: the owner-written key public tracking links are derived with (HMAC-SHA256).
  {
    name: 'KeyVaultSecrets__Mappings__4__SecretName'
    value: 'public-tracking-link-key'
  }
  {
    name: 'KeyVaultSecrets__Mappings__4__ConfigurationKey'
    value: 'PublicTracking:LinkKeys:1'
  }
  // RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 ("Rol propio y mínimo"): the in-process realtime outbox dispatchers
  // (Realtime__OutboxDispatcher__Provider=PostgreSql below) read their own least-privilege login, a member of
  // paqueteria_realtime only. The API never maps the Worker credential.
  {
    name: 'KeyVaultSecrets__Mappings__5__SecretName'
    value: 'pg-realtime-runtime-connection'
  }
  {
    name: 'KeyVaultSecrets__Mappings__5__ConfigurationKey'
    value: 'ConnectionStrings:PaqueteriaRealtime'
  }
  // OBS-003-TENANT-PSEUDONYM-2026-10-12: the owner-written key of the organization pseudonym (README "Secrets the
  // owner writes"); the API fails at start without it, because its logs carry the pseudonym even with the export off.
  {
    name: 'KeyVaultSecrets__Mappings__6__SecretName'
    value: 'paquetenvia-telemetry-pseudonym-key-1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__6__ConfigurationKey'
    value: 'TelemetryPseudonym:Keys:1'
  }
  // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W7, W11 items 1 and 2): the contact lookup key (opt-out hashes and the
  // e-mail unsubscribe token, AI-24 opt_out.hash) and the webhook values (AI-24 webhooks; RECEIPTS O11 choices 9 and 11).
  // Each previous slot holds a random value nobody has until a rotation copies the old value into it. The workflow
  // generates every one of them once except the Meta app secret and phone number id, which the owner writes.
  {
    name: 'KeyVaultSecrets__Mappings__7__SecretName'
    value: 'paquetenvia-contact-lookup-key-1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__7__ConfigurationKey'
    value: 'ContactLookup:Keys:1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__8__SecretName'
    value: 'whatsapp-app-secret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__8__ConfigurationKey'
    value: 'Messaging:WhatsApp:Webhook:AppSecret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__9__SecretName'
    value: 'whatsapp-app-secret-previous'
  }
  {
    name: 'KeyVaultSecrets__Mappings__9__ConfigurationKey'
    value: 'Messaging:WhatsApp:Webhook:PreviousAppSecret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__10__SecretName'
    value: 'whatsapp-webhook-verify-token'
  }
  {
    name: 'KeyVaultSecrets__Mappings__10__ConfigurationKey'
    value: 'Messaging:WhatsApp:Webhook:VerifyToken'
  }
  {
    name: 'KeyVaultSecrets__Mappings__11__SecretName'
    value: 'whatsapp-webhook-verify-token-previous'
  }
  {
    name: 'KeyVaultSecrets__Mappings__11__ConfigurationKey'
    value: 'Messaging:WhatsApp:Webhook:PreviousVerifyToken'
  }
  {
    name: 'KeyVaultSecrets__Mappings__12__SecretName'
    value: 'acs-email-events-secret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__12__ConfigurationKey'
    value: 'Messaging:Email:Events:Secret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__13__SecretName'
    value: 'acs-email-events-secret-previous'
  }
  {
    name: 'KeyVaultSecrets__Mappings__13__ConfigurationKey'
    value: 'Messaging:Email:Events:PreviousSecret'
  }
  {
    name: 'KeyVaultSecrets__Mappings__14__SecretName'
    value: 'whatsapp-phone-number-id'
  }
  {
    name: 'KeyVaultSecrets__Mappings__14__ConfigurationKey'
    value: 'Messaging:WhatsApp:Webhook:PhoneNumberId'
  }
  {
    name: 'KeyVaultSecrets__Mappings__15__SecretName'
    value: 'acs-communication-resource-id'
  }
  {
    name: 'KeyVaultSecrets__Mappings__15__ConfigurationKey'
    value: 'Messaging:Email:Events:SourceResourceId'
  }
]

var workerKeyVaultEnv = [
  {
    name: 'KeyVaultSecrets__VaultUri'
    value: vault.properties.vaultUri
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__SecretName'
    value: 'pg-worker-runtime-connection'
  }
  {
    name: 'KeyVaultSecrets__Mappings__0__ConfigurationKey'
    value: 'ConnectionStrings:PaqueteriaWorker'
  }
  {
    name: 'KeyVaultSecrets__Mappings__1__SecretName'
    value: 'pg-worker-runtime-connection'
  }
  {
    name: 'KeyVaultSecrets__Mappings__1__ConfigurationKey'
    value: 'ConnectionStrings:Paqueteria'
  }
  // OBS-003-TENANT-PSEUDONYM-2026-10-12: the Worker validates the pseudonym key at start, like the API.
  {
    name: 'KeyVaultSecrets__Mappings__2__SecretName'
    value: 'paquetenvia-telemetry-pseudonym-key-1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__2__ConfigurationKey'
    value: 'TelemetryPseudonym:Keys:1'
  }
  // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31: the tracking link key the Worker derives recipient links with (AI-24
  // tracking_link.derive), the contact lookup key of the opt-out check, and the WhatsApp token and phone number id the
  // Meta adapter sends with (the owner writes both). Messaging__WhatsApp__Provider stays Disabled until the gate-closing
  // change, so the token is read and never used yet.
  {
    name: 'KeyVaultSecrets__Mappings__3__SecretName'
    value: 'public-tracking-link-key'
  }
  {
    name: 'KeyVaultSecrets__Mappings__3__ConfigurationKey'
    value: 'PublicTracking:LinkKeys:1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__4__SecretName'
    value: 'paquetenvia-contact-lookup-key-1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__4__ConfigurationKey'
    value: 'ContactLookup:Keys:1'
  }
  {
    name: 'KeyVaultSecrets__Mappings__5__SecretName'
    value: 'whatsapp-cloud-api-token'
  }
  {
    name: 'KeyVaultSecrets__Mappings__5__ConfigurationKey'
    value: 'Messaging:WhatsApp:MetaCloudApi:AccessToken'
  }
  {
    name: 'KeyVaultSecrets__Mappings__6__SecretName'
    value: 'whatsapp-phone-number-id'
  }
  {
    name: 'KeyVaultSecrets__Mappings__6__ConfigurationKey'
    value: 'Messaging:WhatsApp:MetaCloudApi:PhoneNumberId'
  }
]

// OBS-003 (OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12), shared by API and Worker: the export of traces and metrics to
// Application Insights stays off. Turning it on is a reviewed change of this literal to 'true', after the owner created
// paquetenvia-telemetry-pseudonym-key-1 (docs/development/obs-003-appinsights-export.md); nothing else switches it, and
// apps.settings.json cannot (env001_pilot_guards.py P17 and P21). The connection string is not a secret: its key can only
// send telemetry (residual risk in that note). The pseudonym key version matches the mapped secret name.
var telemetryEnv = [
  {
    name: 'TelemetryExport__Enabled'
    value: 'false'
  }
  {
    name: 'TelemetryExport__ConnectionString'
    value: appInsights.properties.ConnectionString
  }
  {
    name: 'TelemetryPseudonym__CurrentKeyVersion'
    value: '1'
  }
]

var apiEnv = concat(productionEnv, proofStorageEnv, dataProtectionEnv, apiKeyVaultEnv, telemetryEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: apiIdentity.properties.clientId
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
  // GATE-003-PROVIDER-GOOGLE: the GoogleMaps adapter exists and its key is mapped above, but GATE-003
  // stays open until the owner records the Google spending cap; until then the pilot keeps the manual
  // pin. Switching this value to 'GoogleMaps' is the only change needed once that row exists.
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
  // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (W4 "Al cerrar las compuertas (Recommended)"): the notice key is wired
  // (role and KeyId), and Locations__NoticeKeyProtector is not set, so it stays Disabled: the API writes no notice copy
  // and quoting does not depend on this key until the gate-closing change turns it on.
  {
    name: 'PiiProtection__RecipientNoticeKey__KeyId'
    value: recipientNoticeKeyUri
  }
  // AI-24 opt_out.key_versions: the contact lookup key version (paquetenvia-contact-lookup-key-1, mapped above).
  {
    name: 'ContactLookup__CurrentKeyVersion'
    value: '1'
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
  // TRK-002-AUTO-LINK: links are {PublicBaseUrl}/track/{token}, derived with key version 1.
  {
    name: 'PublicTracking__PublicBaseUrl'
    value: publicOrigin
  }
  {
    name: 'PublicTracking__CurrentLinkKeyVersion'
    value: '1'
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

var workerEnv = concat(productionEnv, proofStorageEnv, dataProtectionEnv, workerKeyVaultEnv, telemetryEnv, [
  {
    name: 'AZURE_CLIENT_ID'
    value: workerIdentity.properties.clientId
  }
  {
    name: 'Urls'
    value: 'http://+:8080'
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
  // NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31 (AI-24 tracking_link.derive and opt_out.key_versions): the Worker builds
  // {PublicBaseUrl}/track/<token> in memory with link key version 1 and hashes contacts under contact lookup key
  // version 1. It unwraps notice copies with the notice key only (never the PII key). Notifications__RecipientNotices__
  // and Messaging__ are not set here: every recipient-notice switch stays off and every provider Disabled until the
  // gate-closing change records the GATE-004 and GATE-007 resolution rows (env001_pilot_guards.py P28).
  {
    name: 'PublicTracking__PublicBaseUrl'
    value: publicOrigin
  }
  {
    name: 'PublicTracking__CurrentLinkKeyVersion'
    value: '1'
  }
  {
    name: 'ContactLookup__CurrentKeyVersion'
    value: '1'
  }
  {
    name: 'PiiProtection__RecipientNoticeKey__KeyId'
    value: recipientNoticeKeyUri
  }
  {
    name: 'Orders__ClaimWindowFinalization__Enabled'
    value: 'true'
  }
  // ORD-AUTO-CLOSE-2026-10-10 ("Sí, que se cierre sola"): close DELIVERED orders whose CLOSED guards hold, with the
  // contract defaults (60 s interval, batches of 100, 10 batches per cycle; not overridden).
  {
    name: 'Orders__AutoClose__Enabled'
    value: 'true'
  }
  // PILOT-CLEANUPS-ENABLED. OPS-004: purge only old PROCESSED/DEAD outbox rows through the maintenance
  // functions, with the contract retention defaults (business 7 d / 30 d, location 1 d / 7 d; not overridden).
  {
    name: 'OutboxRetention__Enabled'
    value: 'true'
  }
  {
    name: 'OutboxRetention__DryRun'
    value: 'false'
  }
  // OPS-003: idempotency keys are purged only after the fixed 72 h floor; expired proof upload
  // sessions and revoked/expired BFF sessions are purged by their security functions.
  {
    name: 'OperationalCleanup__IdempotencyKeys__Enabled'
    value: 'true'
  }
  {
    name: 'OperationalCleanup__IdempotencyKeys__DryRun'
    value: 'false'
  }
  {
    name: 'OperationalCleanup__ProofUploadSessions__Enabled'
    value: 'true'
  }
  {
    name: 'OperationalCleanup__BffSessions__Enabled'
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
            // CSP connect-src: drivers PUT proof bytes straight to Blob Storage with the signed URL.
            {
              name: 'PAQUETERIA_CSP_CONNECT_SOURCES'
              value: blobServiceUri
            }
            // UI-001: server-only settings read by /ops/orders/new; a missing or invalid value disables confirmation.
            {
              name: 'PAQUETERIA_TERMS_VERSION'
              value: webTermsVersion
            }
            {
              name: 'PAQUETERIA_PRIVACY_VERSION'
              value: webPrivacyVersion
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
