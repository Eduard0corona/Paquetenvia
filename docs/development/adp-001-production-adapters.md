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
| `AZURE_CLIENT_ID` | API, Worker | Client id of the **user-assigned** managed identity of that workload. When absent, the system-assigned identity is used. No secret, connection string or account key is read for any Azure adapter. |

The adapters authenticate only with `ManagedIdentityCredential`; there is no client secret,
storage account key or SAS in configuration.

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
