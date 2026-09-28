# ADP-001: production PII and anti-malware adapters

ADP-001 (`docs/normative/v0.6/specs/AI-08_BACKLOG.yaml`, GATE-007) adds the production adapters
behind the seams that GEO-001, INC-001, POD-001 and SCL-001 left fail-closed:

| Owner decision | Adapter |
| --- | --- |
| ADP-001-PII-KEYVAULT-ENVELOPE | `AzureKeyVault` PII protector for Locations and Incidents: envelope encryption, one fresh AES-256-GCM data key per value, wrapped with an RSA key in Azure Key Vault reached through managed identity |
| ADP-001-POD-BLOB-DEFENDER | `AzureBlob` proof object storage (user-delegation SAS upload) and `DefenderForStorage` threat scanner that reads the Microsoft Defender for Storage malware-scanning verdict |
| ENV-001 / SCL-001 prerequisite | optional `AzureKeyVault` key-encryption protector for the PostgreSQL Data Protection key ring |

Synthetic and mock providers stay the default everywhere. No production provider is selected
unless the deployment names it, and every production provider validates its options on start and
publishes a `ready` health check that fails closed.

## Azure configuration contract

This section is the interface with the Azure infrastructure (ENV-001, `deploy/azure`). Every name
below is read exactly as written; environment variables use the .NET double-underscore form.
Values in angle brackets are placeholders.

### Workload identity

| Variable | Host | Meaning |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | API, Worker, DatabaseMigrator job | Client id of the **user-assigned** managed identity of that workload. When absent, the system-assigned identity is used. No secret, connection string or account key is read for any Azure adapter. |

The adapters authenticate only with `ManagedIdentityCredential`; there is no client secret,
storage account key or SAS in configuration.

### Key Vault secrets read by the application (PILOT-KEYVAULT-PRIVATE-APP-READ)

The pilot Key Vault uses `defaultAction=Deny`, so Container Apps platform Key Vault references are
not used. API, Worker and the DatabaseMigrator job read their own secrets at startup, from inside
the VNet (Key Vault private endpoint), with `ManagedIdentityCredential` (`AZURE_CLIENT_ID`). The
source is **off unless `KeyVaultSecrets__VaultUri` is set**; local and synthetic runs are unchanged.

| Variable | Value | Required |
| --- | --- | --- |
| `KeyVaultSecrets__VaultUri` | `https://<vault>.vault.azure.net/` (HTTPS, no path) | turns the source on |
| `KeyVaultSecrets__Mappings__<n>__SecretName` | Key Vault secret name (`[0-9A-Za-z-]`, 1–127) | one per secret, `n` = 0, 1, 2… |
| `KeyVaultSecrets__Mappings__<n>__ConfigurationKey` | the configuration key that receives the value (`:` separated) | same `n` |
| `KeyVaultSecrets__TimeoutSeconds` | `30` (default; 5–120) | no |

Rules: only the listed secrets are read (there is no "load every secret" mode); the latest
enabled version is read once at startup; a missing, disabled or unreadable secret, a duplicated
secret name or configuration key, or an empty mapping list **stops the host before it starts**;
values are never logged. Key Vault values override the same key from environment variables and
appsettings. A secret rotation takes effect on the next restart/revision.

Suggested mappings (the secret names are the ENV-001 agent's choice; the configuration keys are
fixed by the application):

| Workload | Configuration key | Meaning |
| --- | --- | --- |
| API | `ConnectionStrings:Paqueteria` | PostgreSQL connection of `paqueteria_app` login |
| API | `EmailLookup:Keys:1` | REG-002 email lookup HMAC key (Base64) |
| API | any other secret setting (for example the AuthCenter client secret, maps API key) | same mechanism |
| Worker | `ConnectionStrings:PaqueteriaWorker` | PostgreSQL connection of the worker login |
| Worker | `ConnectionStrings:Paqueteria` | when the Worker hosts components that read it |
| DatabaseMigrator job | `PAQUETERIA_MIGRATION_CONNECTION` | the value `--connection-env PAQUETERIA_MIGRATION_CONNECTION` reads; with the source on, the migrator resolves that name from its configuration (environment variables overlaid by the mapped Key Vault secret), so the job no longer needs the connection string in an environment variable |

Example (API): `KeyVaultSecrets__VaultUri=https://kv-paquetenvia.vault.azure.net/`,
`KeyVaultSecrets__Mappings__0__SecretName=paqueteria-app-connection`,
`KeyVaultSecrets__Mappings__0__ConfigurationKey=ConnectionStrings:Paqueteria`.

RBAC: **Key Vault Secrets User** for each workload identity, scoped to each secret it maps
(`/secrets/<name>`), never the whole vault. The Worker gets no access to API-only secrets, and the
migrator job only to its migration connection.

### PII envelope protector (API only)

| Variable | Value | Required |
| --- | --- | --- |
| `Locations__PiiProtector` | `AzureKeyVault` | yes, to enable production location PII |
| `Incidents__PiiProtector` | `AzureKeyVault` | yes, to enable production incident PII |
| `PiiProtection__AzureKeyVault__KeyId` | `https://<vault>.vault.azure.net/keys/<pii-key-name>` — **versionless** key URI | yes when either selector above is `AzureKeyVault` |
| `PiiProtection__AzureKeyVault__CurrentVersionRefreshSeconds` | `300` (default; 30–3600) | no |
| `PiiProtection__AzureKeyVault__HealthCacheSeconds` | `300` (default; 30–3600) | no |

`Incidents__PiiKeyVersion` is ignored by the `AzureKeyVault` protector: the server takes the
key version from Key Vault, never from configuration or the client.

Key requirements: RSA key (3072 bits recommended; `RSA-HSM` optional), permitted operations
`wrapKey` and `unwrapKey`, algorithm `RSA-OAEP-256`. Rotation creates a **new version of the same
key name**; earlier versions must stay **enabled** (never disabled, expired or purged) while any row
protected under them exists, because rows are unwrapped with the exact version recorded in
`pii_key_version`. Enable soft-delete and purge protection on the vault.

### Proof storage and malware scanning (API and Worker, same values)

| Variable | Value | Required |
| --- | --- | --- |
| `ProofStorage__Provider` | `AzureBlob` | yes |
| `ProofStorage__ThreatScanner` | `DefenderForStorage` | yes (requires `ProofStorage__Provider=AzureBlob`) |
| `ProofStorage__AzureBlob__ServiceUri` | `https://<account>.blob.core.windows.net` (HTTPS, no path, no query) | yes |
| `ProofStorage__AzureBlob__ContainerName` | `proofs` (default) | no |
| `ProofStorage__AzureBlob__UserDelegationKeyLifetimeMinutes` | `60` (default; 15–1440) | no |
| `ProofStorage__DefenderForStorage__ScanResultTagName` | `Malware Scanning scan result` (default) | no |
| `ProofStorage__DefenderForStorage__ScanTimeTagName` | `Malware Scanning scan time UTC` (default) | no |
| `ProofStorage__DefenderForStorage__NoThreatsFoundValue` | `No threats found` (default) | no |
| `ProofStorage__DefenderForStorage__MaliciousValue` | `Malicious` (default) | no |

The existing POD-001 keys keep their meaning for every provider (`UploadUrlLifetimeMinutes`,
`DownloadUrlLifetimeMinutes`, `SessionLifetimeMinutes`, `MaximumBytes`, `ProcessingIntervalSeconds`,
…). The S3/MinIO keys (`ServiceUrl`, `PublicPresignUrl`, `Region`, `Bucket`, `AWS_*`) are not read
when `Provider=AzureBlob`, and the local `S3Compatible` + `Synthetic` path is unchanged.

The tag names and values default to the Microsoft Defender for Storage documentation
("Understand malware scanning results": result tag with `No threats found` / `Malicious`, plus
the scan time tag). They are configurable because tag keys are case-sensitive and Microsoft has
changed the capitalization in its documentation; the adapter matches the configured names
exactly and treats an absent tag as "not scanned yet".

Storage account requirements:

- StorageV2, **no hierarchical namespace** (blob index tags are required), blob versioning off;
- `allowBlobPublicAccess=false`, container access level `private`, HTTPS only, minimum TLS 1.2;
- `allowSharedKeyAccess=false` is supported (the adapters use only Microsoft Entra ID and
  user-delegation SAS);
- Microsoft Defender for Storage with **on-upload malware scanning** enabled and the blob index
  tag scan result enabled (the default); optional Event Grid/Log Analytics are not read;
- CORS on the Blob service for the PWA origin (`https://paquetenvia.com`): method `PUT`, allowed
  headers `content-type,x-ms-blob-type,x-ms-meta-*,x-ms-version,x-ms-client-request-id`, exposed
  headers `etag,x-ms-request-id`, max age 300. Clients upload directly with the returned SAS URL;
  the API never receives the bytes.

### Data Protection key-encryption key (API and Worker, same values)

| Variable | Value | Required |
| --- | --- | --- |
| `DataProtection__Provider` | `PostgreSql` (existing SCL-001 key) | yes, a KEK is only accepted with the shared ring |
| `DataProtection__KeyEncryption__Provider` | `AzureKeyVault` (default `None`) | to enable the KEK |
| `DataProtection__KeyEncryption__AzureKeyVault__KeyId` | `https://<vault>.vault.azure.net/keys/<dataprotection-kek-name>` — **versionless** key URI | yes when the provider is `AzureKeyVault` |

Use a key distinct from the PII key. The same rotation rule applies: never disable earlier
versions while ring entries wrapped by them exist.

### Azure RBAC per workload

| Workload | Scope | Role | Why |
| --- | --- | --- | --- |
| API | PII key (`/keys/<pii-key-name>`) | **Key Vault Crypto Service Encryption User** | read key (current version), `wrapKey`, `unwrapKey` |
| API | Data Protection KEK key | **Key Vault Crypto Service Encryption User** | Data Protection ring wrap/unwrap |
| API | container `proofs` | **Storage Blob Data Contributor** | upload/download SAS are limited by the signer's own permissions; readiness probe |
| API | storage account | **Storage Blob Delegator** | `generateUserDelegationKey` for the user-delegation SAS |
| Worker | container `proofs` | **Storage Blob Data Contributor** | list/read quarantine, copy to `proofs/`, delete quarantine |
| Worker | container `proofs` | custom role with the single data action `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read` | read the Defender verdict tag |
| Worker | Data Protection KEK key | **Key Vault Crypto Service Encryption User** | Data Protection ring wrap/unwrap |

Neither workload may hold `Storage Blob Data Owner` or any role with
`blobs/tags/write`: the Defender verdict is a blob index tag, and index tags are not
tamper-resistant against principals that can write them. The upload SAS never carries the `t`
(tags) permission, so a client cannot forge a verdict either. The Worker needs no access to the
PII key.

## PII envelope protector (ADP-001-PII-KEYVAULT-ENVELOPE)

`Paqueteria.Infrastructure.Security.Pii.PiiEnvelopeProtector` seals every value independently:

1. The server asks the key-encryption client for the **current** key version. The Key Vault client
   reads it from Key Vault (`GET /keys/{name}`), checks the key is enabled, not expired, RSA and
   allowed to wrap and unwrap, and caches it for `CurrentVersionRefreshSeconds`. Configuration and
   clients never supply a version.
2. A fresh 256-bit data key per value encrypts it with AES-256-GCM. The associated data binds the
   ciphertext to the owning organization (tenant), the row id (location or incident id), the column
   (`locations.address_text`, `locations.contact_name`, `locations.phone`,
   `incidents.description`) and the key version, so a value cannot be moved to another tenant,
   another row or another column, or relabelled with another version.
3. The data key is wrapped with `RSA-OAEP-256` under that exact version
   (`CryptographyClient.WrapKey`) and zeroed from memory.

Envelope format v1 (stored as-is in the existing `bytea` columns): `PQE\x01`, a big-endian
`uint16` wrapped-key length, the wrapped key, a 12-byte nonce, the 16-byte tag and the ciphertext.
`pii_key_version` holds `akv:{key name}/{Key Vault version}` (text, as AI-06 already defines).
**No schema change and no migration**: the columns (`address_ciphertext`, `contact_name_ciphertext`,
`phone_ciphertext`, `description_ciphertext`, `pii_key_version`) are the AI-06 ones.

Unwrapping always addresses the recorded version (`/keys/{name}/{version}/unwrapkey`), so
rotation keeps earlier rows readable while their version stays enabled. A stored version naming
another key is refused before any call to Key Vault.

`ILocationPiiProtector` and `IIncidentPiiProtector` became asynchronous, receive the row binding
(owner organization and row id) and return the key version they chose. The location id is derived
from the idempotency key before protection; for incidents, the service reads and authorizes the
order in a short read-only transaction to learn its owner organization (the incident's
`owner_org_id`), generates the incident id, protects, and re-checks the owner inside the writing
transaction. Both services now protect **before** opening the database transaction:
an unavailable vault ends in `503` with no row, idempotency reservation, audit entry or outbox
event, and no Key Vault call is made while a transaction is held. The quote-location path
(PRC-001) no longer pins the synthetic label `PRC-001-SYNTHETIC-V1`; it uses the protector's
version like location creation. The mocks keep their exact bytes and labels.

The ciphertext is not deterministic. That is safe for idempotency: location replays are keyed by
the derived location id and incident replays hash the plaintext request, never the ciphertext.
No read path of PII exists yet; `UnprotectAsync` is used by tests and is the future read seam.

## Proof storage and Defender verdict (ADP-001-POD-BLOB-DEFENDER)

`AzureBlobProofObjectStorage` keeps the POD-001 invariants: the API never receives the bytes.

- **Upload grant**: a user-delegation SAS for the single blob
  `quarantine/{owner}/{order}/{session}`, permissions `cw` only (never `t`, `r`, `d` or list),
  resource `b`, HTTPS only, expiry = the POD-001 upload lifetime (a longer expiry is refused).
  The delegation key is cached and always outlives the SAS it signs. It is obtained by
  `PrepareUploadGrantAsync` **before** the session service opens the tenant transaction and takes
  the idempotency lock; the grant itself, created inside the transaction, never calls the storage
  service and fails closed (`503`, nothing written) without a prepared key. Only the API signs;
  the Worker is built with signing disabled.
- **Required headers**: `Content-Type`, `x-ms-blob-type: BlockBlob` and the POD-001 metadata as
  `x-ms-meta-sessionid`, `orderid`, `ownerorgid`, `requestedby`, `prooftype`, `sizebytes`
  (and `sha256` when supplied). Azure metadata names must be C# identifiers, so hyphens are
  dropped; the adapter maps them back to the POD-001 names. They satisfy the PWA header rule
  `[a-z0-9-]{1,80}`, so `apps/web` needs no change. Idempotent replays recompute this shape.
- **Worker**: lists `quarantine/`, validates as before and promotes with a server-side copy
  conditioned on the validated ETag (`x-ms-source-if-match`) and on the destination not existing;
  the final metadata is rebuilt by the server. A changed source is `SOURCE_OBJECT_CHANGED`, exactly
  like the S3 `412`.
- **Internal download**: read-only (`r`) SAS for the final blob, `DownloadUrlLifetimeMinutes`.
- **Readiness**: container exists and is private, listing works and (API) a delegation key can be
  obtained.

`DefenderForStorageThreatScanner` reads Defender's blob index tags:

| Tags on the quarantine blob | Result |
| --- | --- |
| no result tag, or a scan time earlier than the blob's last modification | pending: the Worker skips it before claiming; the object stays in quarantine and the session is untouched |
| `No threats found`, scanned after the last modification | safe: validation continues and the object may be promoted |
| `Malicious` | session `REJECTED` with `THREAT_DETECTED`; the object stays in quarantine (never promoted or deleted) |
| anything else (`Not scanned`, `SAM2592xx` errors) | session `REJECTED` with `THREAT_SCAN_FAILED`; the object stays in quarantine |

The scan-time check means a verdict written for an earlier upload can never vouch for replaced
content, and the ETag-conditioned promotion means the promoted bytes are the ones that were
validated. A verdict that vanishes between the pre-check and the claim leaves the claim for the
POD-001 stale-claim recovery. The scanner's readiness probes the tag-read permission with a Get
Blob Tags on a blob that never exists (`404` = permitted, `403` = not permitted).

The tag names and values default to Microsoft's documentation (checked 2026-09-27, "Understand
malware scanning results": a result tag with `No threats found`, `Malicious`, `Not scanned` and
`SAM2592xx` error states, plus a scan time tag). The exact capitalization of the keys
(`Malware Scanning scan result` / `Malware Scanning scan time UTC`; the page itself writes
"Malware scanning scan result") and the scan-time format must be confirmed on the first real
scan; both keys are configurable and an unparseable scan time is treated as pending (fail closed).

## Data Protection key-encryption key

`DataProtection:KeyEncryption:Provider=AzureKeyVault` calls `ProtectKeysWithAzureKeyVault` with the
Key Vault `KeyResolver` (managed identity). It is accepted only with `DataProtection:Provider=PostgreSql`
and is `None` by default. New ring entries are written wrapped; entries written before activation
stay readable (they were not encrypted). The `data_protection_key_encryption` ready check resolves
the key and round-trips a random probe. Unlike the adapters above, this switch is read when the
host registers Data Protection (as SCL-001 already does for `DataProtection:Provider`), so it must
be present in the host configuration (environment variables), not in a later-added source.

## Provider selection and fail-closed start

| Setting | Default | Production value | Validated on start |
| --- | --- | --- | --- |
| `Locations:PiiProtector` | `Disabled` | `AzureKeyVault` | `PiiProtection` when selected |
| `Incidents:PiiProtector` | `Disabled` | `AzureKeyVault` | `PiiProtection` when selected |
| `ProofStorage:Provider` | `Disabled` | `AzureBlob` | `ProofStorage:AzureBlob` |
| `ProofStorage:ThreatScanner` | `Disabled` | `DefenderForStorage` | tag options; requires `AzureBlob` |
| `DataProtection:KeyEncryption:Provider` | `None` | `AzureKeyVault` | versionless key URI; requires `PostgreSql` |

The PII and proof adapters are registered lazily and chosen from the bound options at runtime, so
nothing Azure-related is constructed unless it is selected. Ready checks: `pii_key_vault`,
`proof_storage`, `proof_scanner`, `data_protection_key_encryption`.

## Tests and evidence

| AI-08 ADP-001 requirement | Tests |
| --- | --- |
| protector unavailable returns 503 without effects | `Adp001IncidentKeyVaultHttpTests.An_unavailable_key_vault_answers_503_without_effects_or_plaintext_logs` (API + PostgreSQL); `LocationsPostGisContractTests.An_unavailable_key_vault_protector_fails_closed_with_zero_effects`; `IncidentsPostgreSqlContractTests.An_unavailable_key_vault_fails_closed_with_zero_effects`; `Adp001KeyVaultWrapClientHttpTests.A_denied_or_disabled_key_fails_closed` |
| key rotation round-trip | `Adp001KeyVaultWrapClientHttpTests.The_server_reads_the_current_version_from_key_vault_and_rotation_keeps_old_rows_readable` (real Key Vault SDK over an in-process fake REST surface); `LocationsPostGisContractTests.Key_vault_envelopes_persist_under_the_server_version_and_stay_readable_after_rotation`; `IncidentsPostgreSqlContractTests.Key_vault_descriptions_use_the_server_version_and_survive_a_key_rotation`; `Adp001PiiEnvelopeTests.Key_rotation_keeps_data_protected_under_the_previous_version_readable` |
| infected upload stays quarantined | `Adp001AzureBlobDefenderPipelineTests.Infected_upload_is_rejected_and_stays_quarantined` (session service, real Worker processor, PostgreSQL); `Unscanned_upload_stays_quarantined_and_the_session_is_untouched`; `Adp001AzureBlobProofStorageTests.Defender_verdict_rules_fail_closed` |
| no plaintext PII in logs, outbox, audit or snapshots | the contract tests above search `platform.audit_logs` and `platform.outbox_events` (text and Base64) and the stored ciphertexts; the HTTP tests capture every API log entry; the new paths write no snapshot |

Azure SDK clients are exercised without Azure: the Key Vault client runs the real SDK over a fake
HTTP transport (challenge authentication, get key, wrap/unwrap); the Blob adapter runs over an
in-memory gateway (`IProofBlobGateway`), whose Azure implementation is a thin pass-through that is
not exercised against a real storage account here.

```bash
CI=true MSBUILDDISABLENODEREUSE=1 dotnet build Paqueteria.sln
dotnet test tests/Paqueteria.UnitTests/Paqueteria.UnitTests.csproj --filter "FullyQualifiedName~Adp001"
dotnet test tests/Paqueteria.ArchitectureTests/Paqueteria.ArchitectureTests.csproj
dotnet test tests/Paqueteria.ContractTests/Paqueteria.ContractTests.csproj --filter "FullyQualifiedName~LocationsPostGisContractTests|FullyQualifiedName~IncidentsPostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests/Paqueteria.IntegrationTests.csproj --filter "FullyQualifiedName~Adp001|Category=SecureProofUpload"
```

## Residual risks

- No test ran against real Azure. First activation must confirm the Defender tag key
  capitalization and scan-time format, that the server-side copy completes within the Worker
  pass, that Get Blob Tags on a missing blob answers `404` (not `403`) with the tag-read role, and
  the CORS rule for the PWA.
- A SAS cannot sign request headers (an S3 presigned PUT can). The Worker's database-backed checks
  (key, requester, size, content type, metadata/key consistency) remain the barrier; the optional
  upload-time `sha256` becomes client-omittable with Blob, while the finalize-time SHA-256
  comparison against the promoted object stays mandatory.
- Rejected and never-scanned objects stay in quarantine indefinitely (POD-001 retention is still a
  GATE-007 decision) and the Worker re-reads their tags on each pass.
- Rotation depends on earlier Key Vault key versions staying enabled; disabling or purging a
  version makes its rows unreadable. Changing the key **name** needs a re-encryption procedure
  that does not exist yet.
- GATE-007 stays open: these adapters make real PII protection possible but do not authorize real
  PII (PILOT-REAL-PEOPLE, GATE-007-PRIVACY-DRAFT).

## Rollback

Set the selectors back to their defaults (`Locations:PiiProtector` / `Incidents:PiiProtector` =
`Disabled`, `ProofStorage:Provider` = `Disabled` or `S3Compatible`,
`DataProtection:KeyEncryption:Provider` = `None`) and redeploy; then revert the ADP-001 commits.
Rows already protected under Key Vault need the key to be read, and ring entries written with the
KEK need the KEK: never delete the keys or their versions as part of a rollback. No migration is
involved.
