# ENV-001 pilot environment on Azure

ENV-001 (`docs/normative/v0.6/specs/AI-08_BACKLOG.yaml`) is the pilot **with real people**
(`PILOT-REAL-PEOPLE`). It is separate from the AZR-001 DEV-SYNTHETIC environment. AZR-001 and
AZR-001-DEC-001 (synthetic data only, public PostgreSQL) do not apply here, and none of the AZR-001
files (`deploy/azure/*.bicep`, `deploy-core.ps1`, `deploy-azure-dev.yml`) or guards were changed.

| Piece | Location |
|---|---|
| Templates | `deploy/azure/pilot/{security,platform,jobs,apps,observability}.bicep` |
| Pilot-only images | `deploy/azure/pilot/Dockerfile.db-ops` (migrator only), `deploy/azure/pilot/Dockerfile.web` (AUTH-001 BFF build) |
| API and Worker images | `deploy/azure/Dockerfile.api`, `deploy/azure/Dockerfile.worker` (shared, unchanged) |
| Business settings reviewed by the owner | `deploy/azure/pilot/apps.settings.json` |
| OBS-002 alert e-mail (owner) | `deploy/azure/pilot/observability.parameters.json` |
| Accepted terms and privacy versions for the web (owner, UI-001) | `deploy/azure/pilot/web.parameters.json` |
| Deployment workflow | `.github/workflows/deploy-azure-pilot.yml` (`workflow_dispatch`, GitHub Environment `azure-pilot`) |
| Static guards | `tools/azr-001/env001_pilot_guards.py` (23 guards) and `test_env001_pilot_guards.py` |
| Restore drill | `deploy/azure/pilot/restore-drill.sh` |
| One-time bootstrap (owner) | [`bootstrap.md`](bootstrap.md) |

Nothing in this directory has been deployed yet. There was no Azure access while writing it. Section 9
lists what could only be checked statically.

## 1. Owner decisions and where they are implemented

| Decision (decision-log, 2026-09-27) | Implementation |
|---|---|
| PILOT-REAL-PEOPLE | Separate templates, workflow, GitHub Environment, identities and guards. The workloads run as `Production` with `PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE`. |
| PILOT-DOMAIN-PRODUCTION | `paquetenvia.com` apex, AuthCenter client `paquetenvia-web-prod`, `AuthCenter__PublicOrigin=https://paquetenvia.com` |
| PILOT-DB-PRIVATE-NETWORK | VNet `10.60.0.0/22`. PostgreSQL uses private access (delegated `/28` + private DNS zone, `publicNetworkAccess=Disabled`, no firewall rules). The Container Apps environment (Consumption workload profile) is injected into a delegated `/23`. |
| PILOT-BUDGET-100USD | `budget-pv-pilot` (100 USD per month; alerts at actual 80 %, actual 100 % and forecast 100 %). Log Analytics is capped at 0.3 GB/day. The cost table is in §5. |
| AZURE-DEPLOY-VIA-WORKFLOW | Single `workflow_dispatch` workflow using OIDC, with the 13/13 Foundation provenance gate (AZR-001 §27 `deploy-gate` reused unchanged) |
| PILOT-SAME-ORIGIN-ROUTING | Container Apps rule-based routing (§3) |
| ADP-001-POD-BLOB-DEFENDER | Storage account (Entra-only, no anonymous or shared-key access), private container `proofs`, Defender for Storage on-upload malware scanning, RBAC from the ADP-001 table |
| ADP-001-PII-KEYVAULT-ENVELOPE | Key Vault RSA-3072 `pii-kek` (wrap/unwrap only). The API alone has *Key Vault Crypto Service Encryption User* on it. |
| SCL-001 / ENV-001 Data Protection KEK | Key Vault RSA-3072 `dataprotection-kek`. API and Worker have *Crypto Service Encryption User*, and the workloads get `DataProtection__KeyEncryption__*`. |
| GATE-013 | API and Worker run with `minReplicas = maxReplicas = 1`, SignalR `InProcess`, no Redis and no Azure SignalR Service (guard P08) |

The ENV-001 acceptance criterion "no pilot deployment without GATE-007 and GATE-012 resolved or
explicitly scoped by the project owner" is enforced by the workflow. The environment variables
`PILOT_GATE_007_DECISION` and `PILOT_GATE_012_DECISION` (for example `GATE-012-PILOT-SCOPE`:
Mexico Central only, 14-day PITR, no geo-redundant backup, Log Analytics 30 days with a daily cap)
must each equal the ID of **exactly one** row in the `decision-log.md` table of the deployed tree.
That row's ID must start with `GATE-007-` / `GATE-012-` and its Type must be exactly
`Gate resolution` or `Gate scoping`. `env001_pilot_guards.py gate-decision` parses the table,
splitting only on unescaped `|`.

Rows that mention a gate without resolving it are rejected, for example:
- `GATE-007-PRIVACY-DRAFT` (a legal process decision);
- `PILOT-REAL-PEOPLE`;
- `NTF-001-OWNER-001`.

Otherwise the deploy job stops before logging in to Azure.

## 2. Architecture

```text
                         paquetenvia.com  (A @ -> environment static IP, TXT asuid)
                                   |
                  Container Apps environment (VNet-injected, Consumption)
                  route "pvpilotroutes" (managed certificate, bindingType Auto)
          /api/*  /hubs/*  /auth/*  /signin-authcenter        every other path
                        |                                            |
               ca-pv-pilot-api (internal ingress, 1 replica)   ca-pv-pilot-web (internal ingress, 1-2)
                        |   \                                    Next.js, NEXT_PUBLIC_AUTH_MODE=bff
                        |    \-- Key Vault (deny by default; app-read secrets, pii-kek, dataprotection-kek)
                        |    \-- Blob Storage "proofs" (user-delegation SAS; Defender scan)
               ca-pv-pilot-worker (no ingress, 1 replica) ---/
                        |
        snet-postgres (delegated) -- PostgreSQL 18 Flexible Server B1ms, private access only
                        ^
   job-pv-pilot-migrate / job-pv-pilot-logins / job-pv-pilot-verify (manual Container Apps Jobs)
```

| Resource | Name (suffix = `uniqueString(subscription, resource group)`) |
|---|---|
| Identities | `id-pv-pilot-api`, `id-pv-pilot-worker`, `id-pv-pilot-web`, `id-pv-pilot-migrate` (migrate and verify jobs), `id-pv-pilot-logins` (logins job) |
| Key Vault | `kv-pvp-<suffix13>`: RBAC, soft delete 90 days, purge protection, firewall `Deny` with only the Container Apps subnet admitted (PILOT-KEYVAULT-PRIVATE-APP-READ) |
| VNet / subnets | `vnet-pv-pilot`: `snet-containerapps` `10.60.0.0/23` (service endpoints Storage and Key Vault), `snet-postgres` `10.60.2.0/28` |
| Private DNS | `pv-pilot.private.postgres.database.azure.com` |
| Log Analytics | `law-pv-pilot-<suffix>`: 30 days, 0.3 GB/day cap |
| Registry | `pvpilot<suffix>`: Basic, admin disabled, pulls by managed identity only |
| Storage | `stpvpilot<suffix13>` / container `proofs` |
| PostgreSQL | `pg-pv-pilot-<suffix>`: 18, Burstable B1ms, 32 GB with auto-grow, 14-day PITR backups, `require_secure_transport=ON`, `azure.extensions=POSTGIS,PGCRYPTO`, `password_encryption=SCRAM-SHA-256` |
| Container Apps environment | `cae-pv-pilot-<suffix>` |
| Apps / jobs | `ca-pv-pilot-{api,worker,web}`, `job-pv-pilot-{migrate,logins,verify}` |
| Alerts (OBS-002, §10) | `sqr-pv-pilot-{outbox-lag,outbox-dead,job-failure,readiness,api-5xx}`, action group `ag-pv-pilot-ops` (e-mail only), workbook *Paquetenvia pilot operations (OBS-002)* |

### Database roles

- `job-pv-pilot-migrate` runs the canonical migrator: `apply --confirm-initial-baseline
  --azure-ownership-bridge`. On the Clean path that is AI-06 + AI-18 through the E-002 bridge and then
  **every module lane**; on the Applied path only the pending lanes run. No app migrates at startup
  (guard P09).
- The E-002 bridge now accepts the exact classification `Production`/`PILOT_REAL_PEOPLE` in addition to
  `DevSynthetic`/`DEV_SYNTHETIC`; any mixed pair fails closed. A test checks that it covers every
  privileged `NOLOGIN` role that AI-18 declares, including `paqueteria_lifecycle_executor`, the
  cleanup, registration and session executors, and the roles the adoption lanes re-declare
  (`PilotAzureOwnershipBridgeContractTests`).
- `job-pv-pilot-logins` runs the new migrator command `runtime-logins`. It creates or re-keys
  `pv_pilot_api` → `paqueteria_app` and `pv_pilot_worker` → `paqueteria_worker` as
  `LOGIN NOINHERIT NOBYPASSRLS` with exactly one membership. It receives **SCRAM-SHA-256 verifiers
  only**, so no plaintext runtime password reaches PostgreSQL or its logs.
- `job-pv-pilot-verify` runs the read-only `assert` after every deployment. The restore drill also
  uses it.

## 3. Same-origin routing (PILOT-SAME-ORIGIN-ROUTING)

**Choice:** Azure Container Apps *rule-based routing*
(`Microsoft.App/managedEnvironments/httpRouteConfigs`, GA API `2025-07-01`). One route on the
environment holds two ordered rules:

1. `pathSeparatedPrefix` `/api`, `/hubs`, `/auth` and exact `path` `/signin-authcenter` → API. A
   path-separated prefix does not capture look-alikes such as `/authx`, and the path is not rewritten.
2. prefix `/` → Web.

The apex domain is bound to the route (`customDomains`, `bindingType: Auto`) together with a free
managed certificate (`managedCertificates`, HTTP validation, which the apex requires). API and Web
ingresses are `external: false`: the only public entry point is the route. Their own FQDNs are not
public.

| Option | Monthly cost | Verdict |
|---|---|---|
| **Container Apps rule-based routing** | 0 (part of the environment ingress) | **Chosen**: native path routing, Envoy with WebSocket upgrade, managed certificate on the apex, no extra hop |
| Azure Front Door Standard | ~35 USD base + requests/egress | Would take a third of the budget on its own |
| Application Gateway v2 | ~180+ USD | Out of budget |
| Next.js rewrites (`PAQUETENVIA_API_PROXY_ORIGIN`) | 0 | Adds a Node hop to every API call, makes Web a dependency of the API, and puts WebSocket proxying in Node. Kept as the documented fallback: set the variable on Web and route only `/` to Web. |

- **WebSockets (`/hubs`):** API and Web ingresses use `transport: auto`, which supports the HTTP/1.1
  upgrade. SignalR still falls back to SSE or long-polling if an upgrade fails. The WebSocket check
  after the first deploy is manual (§6.1 step 9).
- **Back-channel logout:** the environment ingress does not inspect `Origin`, and the API accepts
  `POST /auth/backchannel-logout` without it (auth-001 §14.2). The workflow smoke test sends such a
  POST through the route and requires the API's `{"error":"invalid_request"}`.
- **Forwarded headers:** the API trusts `X-Forwarded-For`/`X-Forwarded-Proto` only from the
  Container Apps subnet (`Http__ForwardedHeaders__KnownNetworks__0=10.60.0.0/23`, `ForwardLimit=1`).

## 4. Configuration, identities and secrets

Every secret lives only in Key Vault. The vault firewall denies public traffic (PILOT-KEYVAULT-PRIVATE-APP-READ):
- `defaultAction: Deny` and `bypass: None`;
- only the Container Apps subnet is admitted, through its free `Microsoft.KeyVault` service endpoint.

Container Apps Key Vault *references* are **not used**. The platform, not the app, resolves those, and
there is a public report that they fail behind such a firewall (microsoft/azure-container-apps#1287).
Instead, API, Worker and the three database jobs read their secrets **themselves** at startup, from
inside the VNet, with their managed identity (`AZURE_CLIENT_ID`). They use the ADP-001 Key Vault
secrets source ("Key Vault secrets read by the application" in
`docs/development/adp-001-production-adapters.md`):

- `KeyVaultSecrets__VaultUri` turns the source on.
- `KeyVaultSecrets__Mappings__<n>__SecretName` and `__ConfigurationKey` map one secret to one
  configuration key. Only the mapped secrets are read.
- A missing, disabled or unreadable secret stops the host before it starts.

Each identity has *Key Vault Secrets User* on **exactly** the secrets it maps, never on the whole vault.
Guard P06 checks three things:
- the mappings equal that least-privilege set;
- the per-secret RBAC arrays equal the mappings;
- no workload declares Container Apps secrets, a `secretRef`, or a sensitive setting in plain env.

| Workload (identity) | Secret → configuration key |
|---|---|
| API (`id-pv-pilot-api`) | `pg-api-runtime-connection` → `ConnectionStrings:Paqueteria`; `authcenter-paquetenvia-client-secret` → `AuthCenter:ClientSecret`; `paquetenvia-email-lookup-key-1` → `EmailLookup:Keys:1`; `google-maps-api-key` → `Locations:GoogleMaps:ApiKey`; `public-tracking-link-key` → `PublicTracking:LinkKeys:1` |
| Worker (`id-pv-pilot-worker`) | `pg-worker-runtime-connection` → `ConnectionStrings:PaqueteriaWorker` and `ConnectionStrings:Paqueteria` (one secret, read once, mapped to both keys) |
| Migrate and verify jobs (`id-pv-pilot-migrate`) | `pg-migrate-connection` → `PAQUETERIA_MIGRATION_CONNECTION` |
| Logins job (`id-pv-pilot-logins`) | `pg-migrate-connection` → `PAQUETERIA_MIGRATION_CONNECTION`; `pg-api-login-verifier` → `PAQUETERIA_API_LOGIN_VERIFIER`; `pg-worker-login-verifier` → `PAQUETERIA_WORKER_LOGIN_VERIFIER` |

The GitHub runner is outside the VNet. `deploy/azure/pilot/kv-firewall.sh` opens a temporary `/32`
rule for the runner's public IP only around its secret reads and writes, and an `always()` step closes
it. There are two windows:
- generated secrets and the platform parameters;
- the database connection secrets.

Every redeploy of `security.bicep` also resets `ipRules` to empty. A secret rotation takes effect when
the app restarts or starts a new revision; the workflow restarts the API and Worker after a rotation.

| Key Vault secret | Written by | Read by |
|---|---|---|
| `pg-admin-password` | workflow (generated once, never printed) | workflow only |
| `pg-migrate-connection` | workflow (every run, derived from the admin password) | migrate, logins and verify jobs |
| `pg-api-runtime-connection` / `pg-worker-runtime-connection` | workflow (generated once, 64 hex chars) | API / Worker |
| `pg-api-login-verifier` / `pg-worker-login-verifier` | workflow (SCRAM of the above) | logins job (its own identity) |
| `paquetenvia-email-lookup-key-1` | workflow (generated once: 32 random bytes, base64) | API (`EmailLookup__Keys__1`) |
| `authcenter-paquetenvia-client-secret` | **owner** (AuthCenter hands it over once) | API (`AuthCenter__ClientSecret`) |
| `google-maps-api-key` | **owner** (Google Cloud Console, GATE-003-PROVIDER-GOOGLE) | API (`Locations__GoogleMaps__ApiKey`) |
| `public-tracking-link-key` | **owner** (32 random bytes, base64; TRK-002-AUTO-LINK) | API (`PublicTracking__LinkKeys__1`) |
| `pg-restore-drill-connection` | `restore-drill.sh` | verify job, during a drill only |

**Reserved names (not created, for later work):**

| Secret | For |
|---|---|
| `whatsapp-cloud-api-token` | GATE-004-CHANNELS (Meta Cloud API access token) → Worker `Messaging:WhatsApp:MetaCloudApi:AccessToken` |
| `whatsapp-phone-number-id` | GATE-004-CHANNELS (WhatsApp Business phone number id) → Worker `Messaging:WhatsApp:MetaCloudApi:PhoneNumberId` |
| `whatsapp-app-secret` | GATE-004-CHANNELS (webhook signature verification; no webhook exists yet, so nothing reads it) |

Azure Communication Services email uses the Worker's managed identity, so it needs no secret.
It is **not** in the Bicep yet. The resources themselves are free (about 0.00025 USD per email).
Binding `paquetenvia.com` as a sender needs its own DNS records (domain TXT, SPF, DKIM x2) and
Meta/ACS sender setup. The adapters exist (`docs/development/gate-004-messaging-adapters.md`) but
are **not wired into the pilot templates**: every `Messaging` channel stays `Disabled` until the owner
supplies the secrets, the approved templates and the sender domain, and a separate change adds the
Worker mappings, the per-secret RBAC and the guard allowlist together (a mapped secret that does not
exist stops the Worker at start).

App settings wired by `apps.bicep`:

- **API:**
  - `ConnectionStrings__Paqueteria`.
  - AuthCenter (`Authority`/`Issuer` `https://authcenter.info`, `ClientId` `paquetenvia-web-prod`,
    `PublicOrigin`, `SessionStore=PostgreSql`).
  - `EmailLookup__CurrentKeyVersion=1`, `EmailLookup__Keys__1`.
  - TRK-002-AUTO-LINK: `PublicTracking__PublicBaseUrl` (the public origin, so links are
    `https://<public host>/track/<token>`), `PublicTracking__CurrentLinkKeyVersion=1` and
    `PublicTracking__LinkKeys__1` (Key Vault `public-tracking-link-key`). The API refuses to start
    with public tracking enabled and no valid key.
  - Every module provider set to `PostgreSql`.
  - `Realtime__Provider=SignalR`, `Backplane=InProcess`, allowed origins.
  - ADP-001: `AZURE_CLIENT_ID`, `Locations__PiiProtector` and `Incidents__PiiProtector` set to
    `AzureKeyVault`, `PiiProtection__AzureKeyVault__KeyId`, `ProofStorage__Provider=AzureBlob`,
    `ProofStorage__ThreatScanner=DefenderForStorage`, `ProofStorage__AzureBlob__ServiceUri`,
    `DataProtection__Provider=PostgreSql` plus the `DataProtection__KeyEncryption__*` settings.
  - `Locations__GeocodingProvider=Manual`. The `GoogleMaps` adapter exists and its key is mapped, but
    GATE-003 stays open until the owner records the Google spending cap, daily quotas and billing alerts
    and writes the Geocoding-only key (section 6.6); switching the value to
    `GoogleMaps` in `apps.bicep` is then the only change (see `docs/development/gate-003-google-maps-geocoding.md`).
- **Worker:** `ConnectionStrings__Paqueteria` and `ConnectionStrings__PaqueteriaWorker`, the same
  ADP-001 proof-storage and Data Protection settings, `Dispatch__AssignmentLifecycle`,
  `Notifications`, `Orders__ClaimWindowFinalization__Enabled=true`, and `Urls=http://+:8080` for the
  probes.
- **Web:** `NODE_ENV=production`, `PAQUETERIA_CSP_CONNECT_SOURCES=<blob endpoint>` (so drivers can
  PUT proofs directly), and `PAQUETERIA_TERMS_VERSION` / `PAQUETERIA_PRIVACY_VERSION` from
  `web.parameters.json` (see "Accepted terms and privacy versions" below). The image is built with
  `NEXT_PUBLIC_AUTH_MODE=bff` and **without** `NEXT_PUBLIC_API_BASE_URL`.

> The shared `deploy/azure/Dockerfile.web` cannot produce a BFF build. It always defines
> `NEXT_PUBLIC_API_BASE_URL`, as `""` when the argument is omitted, and `next.config.ts` then fails
> with `TypeError: Invalid URL`. This was reproduced with `pnpm build`. The pilot therefore uses
> `deploy/azure/pilot/Dockerfile.web`.

`apps.settings.json` holds the business and policy values the owner must choose. It is appended to the
API and Worker settings. It cannot override any platform-managed prefix, and it cannot carry a
mock/synthetic value or a secret-looking name (guard P17). The workflow refuses to deploy while any
value is still `OWNER_DECISION_REQUIRED`; today no value in the file awaits the owner (it only carries
`Finance__OperationalTimeZone=America/Mazatlan`).

There is no assignment or driver eligibility policy version setting (POLICY-VERSIONS-PER-ORG-2026-10-02,
owner 2026-10-02: "Por empresa, piloto-2026-10-v1"). Each organization carries its own versions in
`organizations.organizations.assignment_policy_version` and `driver_eligibility_policy_version`; every
organization, existing or created later, starts at `piloto-2026-10-v1`. The version applied is that of the
organization of the driver being assigned or evaluated (the order owner when it assigns its own driver, the
operator when the operator does). The API refuses to start if `Dispatch__AssignmentPolicyVersion` or
`Drivers__Eligibility__PolicyVersion` is still set, and the guard rejects both names in
`apps.settings.json` (as `REMOVED_SETTINGS`), so they no longer block the deploy. Raising an organization's
version is a reviewed migrator step; there is no loader or API path for it yet.

There is no pricing policy version setting (PRC-POLICY-VERSION-PER-ORG, owner 2026-09-28): each
organization versions its own pricing policy in `pricing.tariff_rules.policy_version`, loaded with its
tariffs (MDM-001), and every quote freezes the version of the rule it selected. The API refuses to
start if `Pricing__PricingPolicyVersion` is still set.

Driver eligibility maps (`Drivers__Eligibility__RequiredDocumentTypesByVehicleType__<TYPE>__0`,
`Drivers__Eligibility__VehicleCapacity__<TYPE>__MaximumPackageCount`, …) go in the same file.

**Accepted terms and privacy versions (UI-001, owner decision pending).** The operator-assisted order
screen (`/ops/orders/new`) reads two server-only runtime settings on the web container:
`PAQUETERIA_TERMS_VERSION` and `PAQUETERIA_PRIVACY_VERSION`. Each must match the AI-05
`terms_version`/`privacy_version` format `^[A-Za-z0-9._-]{1,64}$` (letters, digits, `.`, `_`, `-`; 1 to 64
characters, for example `2026-10-01`). If either is missing, malformed or `OWNER_DECISION_REQUIRED`, the web
disables order confirmation. There is no default.

The values are owner decisions. They come with the approved privacy notice (GATE-007) and the terms in force,
and the owner has not supplied them yet. They live in `deploy/azure/pilot/web.parameters.json`, an ARM
parameters file like `observability.parameters.json`:

- `webTermsVersion` → `PAQUETERIA_TERMS_VERSION` and `webPrivacyVersion` → `PAQUETERIA_PRIVACY_VERSION`, both
  set on `ca-pv-pilot-web` only. In `apps.bicep` both are string parameters with no default,
  `@minLength(1)` and `@maxLength(64)`.
- Both start as `OWNER_DECISION_REQUIRED`. `env001_pilot_guards.py web-check` fails with
  `STOP_FOR_OWNER_DECISION: webTermsVersion (PAQUETERIA_TERMS_VERSION) ...` (and the same for the privacy
  version) while a value is the placeholder, empty, longer than 64 characters or outside the format, or
  while the file has any other parameter. The provenance job and Stage 4 run it, so **the pilot deploy
  stops before touching Azure** until the owner fills both values.
- Guard P22 checks the parameters (no default, 1 to 64), that only the web receives the two variables, and
  that the workflow checks and deploys the file.

They are not in `apps.settings.json`, because that file only reaches the API and Worker, only takes
`.NET` `Section__Key` names, and treats the `PAQUETERIA_` prefix as platform-managed. Changing a version
later is a reviewed PR to `web.parameters.json` and a redeploy; new orders then record the new version.

**Cleanups (PILOT-CLEANUPS-ENABLED, owner 2026-09-28).** The Worker runs:

- **OPS-004 outbox retention:** `OutboxRetention__Enabled=true`, `DryRun=false`. It purges only old
  `PROCESSED`/`DEAD` rows through the maintenance functions. The contract retention defaults are kept:
  business 7 d / 30 d, location 1 d / 7 d.
- **OPS-003 operational cleanup:**
  - Idempotency keys: `Enabled=true`, `DryRun=false`, with the fixed 72 h floor.
  - Expired proof upload sessions and revoked/expired BFF sessions: `Enabled=true`.

These are platform-managed. `apps.settings.json` cannot override them, and guard P19 rejects any
retention or batch override, so the contract defaults hold. To pause a job, set its `Enabled` to
`false` in `apps.bicep` and redeploy. The ops-003/ops-004 runbooks describe dry-run measurement.

## 5. Cost estimate (PILOT-BUDGET-100USD)

Retail prices for `mexicocentral` from the Azure Retail Prices API on 2026-09-27, in USD, for 730
hours a month. Sized for 50–100 real deliveries.

| Item | Basis | USD/month |
|---|---|---|
| PostgreSQL B1ms compute | 0.0187 per hour | 13.65 |
| PostgreSQL storage 32 GB | 0.1265 per GB | 4.05 |
| PostgreSQL backups (14 days) | free up to 100 % of provisioned storage | 0.00 |
| API 0.5 vCPU / 1 GiB, always on | active rate 0.000026 per vCPU-s, 0.000003 per GiB-s (the outbox dispatchers poll every 250–500 ms, so the replica rarely qualifies for the idle rate) | 42.04 |
| Worker 0.25 vCPU / 0.5 GiB, always on | active rate (continuous polling) | 21.02 |
| Web 0.25 vCPU / 0.5 GiB, min 1 | mostly idle rate (0.000003 per vCPU-s) with request bursts | ~7.00 |
| Container Apps monthly free grant | 180,000 vCPU-s + 360,000 GiB-s | −5.76 |
| Container Apps requests and jobs | < 2 M requests free; jobs run for minutes | ~0.10 |
| Container Registry Basic | 0.1666 per day | 5.07 |
| Defender for Storage (per account) | 0.0134 per hour | 9.78 |
| Malware scanning | 0.15 per GB scanned (~0.2 GB of photos) | 0.03 |
| Log Analytics | 5 GB/month free, then 2.53 per GB; cap 0.3 GB/day ⇒ ≤ 9.1 GB | 0 – 10.40 |
| Key Vault | RSA-3072 operations 0.165 per 10 k; secrets 0.033 per 10 k | ~0.30 |
| Blob storage | a few GB Hot LRS | ~0.20 |
| Private DNS zone | 0.50 per zone | 0.50 |
| VNet, route, managed certificate, budget | free | 0.00 |
| **Subtotal before OBS-002** | | **≈ 98 typical, ≈ 108 if the log cap is hit every day** |
| Log search alerts (OBS-002, §10) | 5 rules at 15 min (0.55 each), Retail Prices API 2026-09-28; e-mails and workbook free; the extra ~0.3–0.45 GB/month of logs fits the 5 GB free tier | 2.75 |
| **Total** | | **≈ 101 typical, ≈ 111 if the log cap is hit every day** |

**This design sits at the budget line and does not fit ~100 USD with margin.** The owner accepted this
sizing (PILOT-BUDGET-ACCEPT-98-108, 2026-09-28) without applying any lever. OBS-002 adds about
2.75 USD per month on top of that accepted range, with every alert evaluated every 15 minutes; the
owner accepted it on 2026-10-02 (OBS-002-ALERTS-15MIN-COST-2026-10-02, literal "Sí, revisar cada 15
min"). The largest item is the
always-on API at 0.5 vCPU. For the record, the levers in order of impact:

1. API at 0.25 vCPU / 0.5 GiB saves about 21 USD, at the risk of memory pressure in a 15-module .NET
   host. Measure it first.
2. Slower outbox/notification poll intervals in `apps.settings.json` would let replicas reach the idle
   rate (up to about 30 USD saved) at the cost of real-time latency. This is a product decision.
3. Web scaled to zero saves about 6 USD, at the cost of a cold start on the first page.
4. Log cap at 0.15 GB/day saves up to about 5 USD.

Two further caveats:

- Budgets alert; they do not stop spending.
- The VNet-injected environment may bill a standard public IP and load balancer in its managed
  resource group (about 4 USD if charged; not confirmed).

## 6. Runbook

### 6.1 First deployment

1. The owner runs [`bootstrap.md`](bootstrap.md) once. It creates the resource group in
   `mexicocentral`, the OIDC app registration and federated credential, the roles and the custom role,
   and the `azure-pilot` GitHub Environment with its variables.
2. The owner records the GATE-007 and GATE-012 decisions (or explicit scopes) in `decision-log.md`,
   then sets `PILOT_GATE_007_DECISION` / `PILOT_GATE_012_DECISION` to those row ids.
   `PILOT_GATE_012_DECISION=GATE-012-PILOT-SCOPE` is already approved (owner, 2026-09-28).
3. The owner replaces every `OWNER_DECISION_REQUIRED` (PR → `development` → `main`):
   - `deploy/azure/pilot/apps.settings.json` (none remains since POLICY-VERSIONS-PER-ORG-2026-10-02);
   - the alert e-mail in `deploy/azure/pilot/observability.parameters.json`;
   - `webTermsVersion` and `webPrivacyVersion` in `deploy/azure/pilot/web.parameters.json`, the accepted
     terms and privacy notice versions that come with the approved notice (§4).

   Each file blocks the workflow while a placeholder remains.
4. Promote to `main` and wait for the Foundation CI push run on `main` (13/13 green). Note its run id
   and head SHA.
5. Actions → *Deploy Azure PILOT* → Run workflow on `main`: `tested_git_sha=<head SHA>`,
   `foundation_run_id=<run id>`, `custom_domain_phase=none`.
6. The first run creates the vault, then stops with *"lacks authcenter-paquetenvia-client-secret"*. The
   workflow requires three owner secrets and reports the first one missing. Write all three straight into
   the vault (§6.6) and run again:
   - `authcenter-paquetenvia-client-secret`;
   - `google-maps-api-key`;
   - `public-tracking-link-key`.
7. The run summary shows the DNS records. At the DNS host of `paquetenvia.com`, create:
   - `A` `@` → the environment static IP;
   - `TXT` `asuid` → the custom-domain verification id.

   Remove any other `A`/`AAAA`/`CNAME` on the apex. Wait for propagation
   (`dig +short paquetenvia.com`, `dig +short TXT asuid.paquetenvia.com`).
8. Run again with `custom_domain_phase=bind`. The route binds the apex and the managed certificate is
   issued by HTTP validation. Check with
   `az containerapp env certificate list -g <rg> -n <env> --managed-certificates-only -o table`
   (status `Succeeded`), then `curl -I https://paquetenvia.com/`.
9. Register the production AuthCenter client URIs (auth-001 §10.1, all on `https://paquetenvia.com`).
   The login test in the next step needs them.
10. Check by hand, in a browser on `https://paquetenvia.com`:
    - Log in through AuthCenter.
    - In DevTools → Network → WS, `/hubs/...` shows `101 Switching Protocols`.
    - Upload a proof photo, then check that the blob has the Defender scan-result index tag.
    - `/ops/orders/new` allows confirming an order (the terms and privacy versions are set).
    - `http://paquetenvia.com` redirects to HTTPS or is refused. HTTP-to-HTTPS on a route custom domain
      is not verified yet.
11. Run the restore drill (§6.4) and keep its `EVIDENCE` line. The ENV-001 criterion "restore drill
    evidence is current before pilot start" requires it.

### 6.2 Redeploy

Promote, wait for 13/13 on `main`, then dispatch with the new SHA and run id. Keep
`custom_domain_phase=bind` once the domain is bound: `none` would unbind the apex. On every run the
workflow:

- redeploys the five templates idempotently (stage 5 is the OBS-002 alerts, §10);
- keeps every generated secret;
- rebuilds the images from `tested_git_sha` and deploys them by digest;
- runs pending migrations, re-applies the runtime logins and runs `assert`;
- smoke-tests the route.

### 6.3 Rollback

- **Application:** dispatch again with an earlier certified SHA and its Foundation run id. Container
  Apps run in single-revision mode, so the earlier digests replace the current ones.
- **Database:** migrations are forward-only in the pilot. An application rollback is only safe while
  the older code still works with the newer schema; every lane is additive, see each lane's rollback
  notes. For data loss or a bad migration, use a point-in-time restore (§6.4). The template tooling
  never rolls a schema back.
- **Routing emergency:** the owner can repoint the route to Web only with
  `az containerapp env http-route-config update ... --yaml <file>`. The next workflow run restores it.

### 6.4 Restore and restore drill

The recovery target is any point in the last 14 days (PITR, locally redundant, no geo backup).

- **Drill:** does not touch the pilot database.

  ```bash
  ./deploy/azure/pilot/restore-drill.sh --resource-group <rg> --restore-time 2026-10-05T13:10:00Z
  ```

  It restores into a new private server, re-points `job-pv-pilot-verify` to it, runs `assert`
  (baseline plus every module lane) and puts the job back. It deletes the drill server unless you pass
  `--keep`, and prints an `EVIDENCE ...` line. Keep that line for the pilot readiness report.
- **Real restore:**
  1. Stop the writers:
     `az containerapp update -n ca-pv-pilot-api -g <rg> --min-replicas 0 --max-replicas 0`, and the
     same for the Worker.
  2. Run the drill with `--keep`, so the restored server stays after verification.
  3. Put the restored server in place. There are two options.
     - **Swap names (preferred):** delete the old server, then restore again under the original
       name `pg-pv-pilot-<suffix>`. This keeps every connection secret valid.
     - **Point secrets at the new host:** update the three connection secrets (`pg-migrate-connection`, `pg-api-runtime-connection`, `pg-worker-runtime-connection`) to the restored
       host, then restart the API and Worker revisions.
  4. Re-run the workflow for the current SHA. This restores the replica counts and re-applies the
     runtime logins.

### 6.5 Destroy

```bash
az group delete --name <rg> --yes
```

This removes everything in the group. The budget and role assignments are scoped to it.

- **Key Vault:** it has purge protection, so it stays soft-deleted for 90 days and cannot be purged.
  Redeploying into a resource group with the same name and subscription derives the same vault name,
  so recover it first: `az keyvault recover --name <kv>`.
- **Outside the group:** delete the app registration (`az ad app delete --id <appId>`), the custom
  role (`az role definition delete --name "Paquetenvia Pilot Blob Tag Reader"`) and the DNS records.

### 6.6 Secrets the owner writes

The vault denies public traffic, so open a temporary rule for your Cloud Shell's IP and close it
afterwards:

```bash
bash deploy/azure/pilot/kv-firewall.sh open <kv>
# Paste the value when prompted; it never appears in shell history or process lists.
read -rs AUTHCENTER_SECRET && printf '%s' "$AUTHCENTER_SECRET" > /tmp/ac && \
az keyvault secret set --vault-name <kv> --name authcenter-paquetenvia-client-secret --file /tmp/ac --encoding utf-8 --output none; \
shred -u /tmp/ac; unset AUTHCENTER_SECRET
read -rs GOOGLE_MAPS_KEY && printf '%s' "$GOOGLE_MAPS_KEY" > /tmp/gm && \
az keyvault secret set --vault-name <kv> --name google-maps-api-key --file /tmp/gm --encoding utf-8 --output none; \
shred -u /tmp/gm; unset GOOGLE_MAPS_KEY
# TRK-002-AUTO-LINK: 32 random bytes, base64, generated in place and never shown.
umask 077 && openssl rand -base64 32 | tr -d '\n' > /tmp/tl && \
az keyvault secret set --vault-name <kv> --name public-tracking-link-key --file /tmp/tl --encoding utf-8 --output none; \
shred -u /tmp/tl
bash deploy/azure/pilot/kv-firewall.sh close <kv>
```

`public-tracking-link-key` derives every public tracking link (HMAC-SHA256; only the SHA-256 of each
link is stored). Write it once and keep it: replacing the value under the same version makes the API
refuse to show existing links (it fails closed). To rotate, store the new key under a new secret name,
map it as `PublicTracking:LinkKeys:2`, set `PublicTracking__CurrentLinkKeyVersion=2` and keep version 1
mapped while its links should still be shown to operators; links already sent keep working either way,
because the public lookup only compares hashes. Rotation is a template change reviewed like any other.

The workflow stops before deploying while any owner secret is missing. The API reads
`google-maps-api-key` at startup even while `Locations__GeocodingProvider=Manual`, so the key must exist
before the next deploy.

**Google Maps key (GATE-003-MAPS-PILOT-RULES-2026-10-02, owner literal "Sí, las 4").** Before writing the
key, the owner restricts it in Google Cloud Console (*APIs & Services → Credentials → API restrictions*) to
the **Geocoding API only**. The pilot has no routes or ETAs, so Directions, Routes and Distance Matrix stay
disabled; the adapter only calls `maps/api/geocode/json`, always with `components=country:MX`, and only a
single non-partial `ROOFTOP` result in Mexico replaces the customer's pin ("Solo ROOFTOP"). The software
cannot check the key restriction, so it is an owner step. Still owner-pending, and GATE-003 stays open
(the pilot keeps `Locations__GeocodingProvider=Manual`) until they are recorded:

- [ ] spending cap (budget) on the Google Cloud project;
- [ ] daily quotas for the Geocoding API;
- [ ] billing alerts;
- [ ] the restricted key written to Key Vault as `google-maps-api-key` (commands above).

See `docs/development/gate-003-google-maps-geocoding.md`.

The workflow identity has *Key Vault Secrets Officer* on the vault. The owner needs a data-plane role
too, for example a temporary *Key Vault Secrets Officer* on the vault.

**Rotation:**

- **Runtime logins:** dispatch the workflow with `rotate_runtime_logins=true`. It writes new secret
  versions, the logins job re-keys `pv_pilot_api`/`pv_pilot_worker`, and the API and Worker revisions
  restart. Expect a few seconds of errors while the restart completes. Never delete these secrets:
  the vault has purge protection, so a deleted name stays blocked for 90 days.
- **PostgreSQL administrator:**
  1. `az postgres flexible-server update -g <rg> -n <pg> --admin-password <new>`.
  2. Write the same value as a new version of `pg-admin-password`.
  3. Run the workflow; it re-derives `pg-migrate-connection`.
- **EmailLookup:** add `paquetenvia-email-lookup-key-2` and wire version 2 (auth-001 §9).

### 6.7 Troubleshooting

- **Job console logs:** Log Analytics →
  `ContainerAppConsoleLogs_CL | where ContainerJobName_s startswith "job-pv-pilot" | order by TimeGenerated desc`.
- **App logs:** `ContainerAppConsoleLogs_CL | where ContainerAppName_s == "ca-pv-pilot-api"`. The logs
  are JSON and carry no PII by contract.
- **An OBS-002 alert fired:** open the workbook *Paquetenvia pilot operations (OBS-002)* or run the
  rule's query from Azure Monitor → Alerts → the alert → *View query results*. The query shows the
  lane, job, app or status counts that tripped it (§10).
- **PostgreSQL connections:** B1ms allows about 50. API and Worker pools are capped at 5 per data
  source (`Maximum Pool Size=5;Minimum Pool Size=0`). `too many connections` means lowering those
  or moving to B2s (+≈41 USD/month, over budget).
- **TLS:** connection strings use `SSL Mode=VerifyFull`. If the image's CA bundle ever lacks the
  PostgreSQL certificate chain, the migrate job fails at connect. Fix the CA bundle; do not downgrade
  to `Require` without an owner decision.

### 6.8 Master data (MDM-001, manual run)

The MDM-001 operator loader (`master-data-load`, owner decision MDM-001-OPERATOR-LOADER) is **not wired
into `jobs.bicep`**. A pilot job needs its own operator login secret and a way to bring the reviewed file
into the VNet, and neither is decided yet; the data itself is gated:

- real service zones and tariffs wait on **GATE-010** and **GATE-011**;
- real driver profiles wait on **GATE-007**. The database enforces it: the loader function refuses any
  `driver_profiles` until the deployment marker `platform.master_data_deployment_gate` records GATE-007 as
  closed. The job also refuses them outside Development, Testing and DEV_SYNTHETIC unless
  `--allow-real-driver-profiles` is passed; both may only be used once GATE-007 is closed;
- the pilot database is `REAL`: with no marker row, or after `master-data-gate --deployment-class REAL`, it
  refuses every `SYNTHETIC` file, so the synthetic examples (`tests/fixtures/mdm-001/`) cannot be loaded
  into it;
- the loader role is a platform-operator capability, not a tenant boundary: whoever holds it can load any
  organization's master data, so only named operator logins the owner controls may hold it.

When the gates are closed, the manual run is:

1. `job-pv-pilot-migrate` has applied the Pricing lane `20260928000100_AddMasterDataLoader` (the `assert`
   job shows `Pricing: APPLIED`). On a pilot database whose baseline predates MDM-001, the lane's Azure
   bridge stops with `E002_EFFECTIVE_ROLE_CAPABILITY_MISSING` until an Azure administrator has created
   `paqueteria_master_data_executor NOLOGIN BYPASSRLS` (with SET for the deployment role) and
   `paqueteria_master_data_loader NOLOGIN NOBYPASSRLS` (granted to the deployment role only
   `WITH ADMIN TRUE, INHERIT FALSE, SET FALSE`: it administers the role but no member of the migrator may
   be able to use the loader); nothing is written before that check.
2. Record the deployment class with the migration connection (a manual `job-pv-pilot-migrate`-style run
   of the db-ops image): `master-data-gate --connection-env PAQUETERIA_MIGRATION_CONNECTION
   --deployment-class REAL --platform-organization-id <PLATFORM organization>`. Only once GATE-007 is
   closed, and only to load driver profiles, add `--gate-007-closed`. Every change is audited
   (`MASTER_DATA_GATE_CHANGED`, PLATFORM organization) and a REAL database is never made SYNTHETIC again.
   The operator login can neither read nor change this marker.
3. Create a named operator login (for example `pv_pilot_mdm_ec`, initials rather than a full name, since
   the login name is recorded in the audit row) as
   `LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`, from a SCRAM-SHA-256
   verifier (never a plaintext password in SQL), and `GRANT paqueteria_master_data_loader TO pv_pilot_mdm`.
   Never grant it `paqueteria_app`, `paqueteria_worker` or `paqueteria_migrator`: the loader refuses such a
   login, and the lane and the `assert` job reject a loader member that is also a deployment principal.
   Store its connection string in Key Vault like the runtime connections.
4. From a container inside the VNet running the `paquetenvia-db-ops` image, with the reviewed file copied
   in and the connection string in `PAQUETERIA_MASTER_DATA_CONNECTION`:

   ```bash
   dotnet /app/migrator/Paqueteria.DatabaseMigrator.dll master-data-load \
     --connection-env PAQUETERIA_MASTER_DATA_CONNECTION \
     --file /tmp/reviewed-master-data.json --organization-id <uuid> --dry-run
   ```

   Review the counts and the `CREATE/UPDATE section[n] fields=...` diff, then run it again without
   `--dry-run`. Keep `DOTNET_ENVIRONMENT=Production` and `PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE`.
   Cities come first, in a load for the PLATFORM organization; each organization's load can only
   reference existing ACTIVE cities.
5. The load leaves one `MASTER_DATA_LOADED` row in `platform.audit_logs` with a pseudonymous `operator_ref`
   (a random UUID per operator login, kept in the platform-only `platform.master_data_operator_refs` and
   readable only as `paqueteria_migrator`; never the login itself or a hash of it, since tenants can read
   their audit rows; rows written before the MDM-001 hardening keep the former SHA-256 format), the file's SHA-256 and PostgreSQL's own SHA-256 of the document; keep the reviewed
   file, its hash and the login with the change record. A re-run of the same file changes no master data.
6. Remove the operator login (`DROP ROLE pv_pilot_mdm_ec`) or rotate its secret when the load is done.

Details, format and error codes: `docs/development/mdm-001-master-data-loader.md`.

## 7. Security notes

- **PostgreSQL:** no public endpoint. Only the VNet reaches it, and every database action runs as a
  Container Apps Job inside the VNet. The runner never connects to it.
- **Blob Storage:** the endpoint stays **public** (owner-accepted: PILOT-BLOB-PUBLIC-ENDPOINT, 2026-09-28). AI-01 §4.16 requires drivers to upload proof bytes
  directly with a signed URL, so phones must reach it from the internet.
  - Anonymous access and shared-key access are disabled. Every request needs Entra ID or a
    user-delegation SAS, which carries no `t`/tags permission.
  - The Container Apps subnet is listed as a virtual-network rule with the free service endpoint.
    With `defaultAction: Allow` this rule is **inert by design**: it grants nothing extra. It is kept
    so that switching `defaultAction` to `Deny` later keeps the workloads' access and cuts off only
    browsers.
  - A private endpoint (about 7.30 USD per month) would not help: phones are not in the VNet.
- **Key Vault (PILOT-KEYVAULT-PRIVATE-APP-READ):** `defaultAction: Deny`, `bypass: None`, and only the
  Container Apps subnet admitted.
  - The apps and jobs read their secrets themselves with their managed identities (§4).
  - The GitHub runner, or the owner's Cloud Shell, gets a temporary `/32` rule only around its secret
    access, and the rule is always removed.
  - Guard P13 fails if the vault is not deny-by-default or if the workflow stops closing its rule.
- **ACR:** public endpoint with Entra RBAC only. The GitHub-hosted runner pushes images, and ACR Basic
  has no network rules.
- **Deployer:** *Contributor* plus *Role Based Access Control Administrator* on the resource group
  only, constrained by an ABAC condition to the seven roles the templates assign (see bootstrap).
  The templates never assign Owner, Contributor, User Access Administrator, Storage Blob Data Owner or
  any tag-writing role (guard P14).
- **Proof verdict tag:** neither workload can write blob index tags. The Worker reads the Defender
  verdict through a custom role with only `blobs/tags/read`.

## 8. Static guards (`tools/azr-001/env001_pilot_guards.py`)

The 23 guards (P00–P22) run on the compiled ARM output and the workflow:

- **Resources:** only authorized resource types and exactly six workloads. Redis, Azure SignalR,
  Front Door and PostgreSQL firewall rules are rejected.
- **Workflow:**
  - `workflow_dispatch` only, `permissions: {}`, the `azure-pilot` environment and concurrency, and
    every third-party action pinned by SHA.
  - No `${{ }}` inside `run:`.
  - The 13/13 `deploy-gate`, image digests and the GATE-007/GATE-012 checks are present.
  - Every job that runs a guard tool first sets up Python and installs `PyYAML==6.0.3` (P20).
- **Secrets and settings:**
  - Secrets come only from Key Vault, read by the application. The per-workload mappings equal the per-secret RBAC, and there are no Container Apps secrets or `secretRef`.
  - Workloads run as `Production`/`PILOT_REAL_PEOPLE`, with no Mock or Synthetic values.
- **Replicas and migrations:** a single API and Worker replica on `InProcess`. Migrations run only
  through the canonical migrator jobs, and the pilot db-ops image contains no DevSeed.
- **Routing:** the exact same-origin rules, with internal ingress for API and Web.
- **Data services:**
  - PostgreSQL is private, version 18, with ≥ 7-day backups, secure transport and the extension
    allowlist, in a VNet-injected Consumption environment.
  - Storage flags and Defender scanning are set.
  - Key Vault denies public traffic (only the Container Apps subnet, no bypass, no standing IP rule) and has RBAC, purge protection and wrap-only RSA keys of at least 3072 bits.
- **RBAC:** role assignments stay within the allowlist.
- **Cost:** the 100 USD budget exists and the log cap is set.
- **ADP-001 and web:**
  - The ADP-001 configuration contract is wired.
  - Business settings cannot override platform settings.
  - The web image is a BFF build.
- **Observability (P21, OBS-002):**
  - exactly the five alert rules, every 5–15 minutes, costing at most 5 USD per month;
  - stateful rules on the pilot workspace;
  - one action group, e-mail only, bound to a parameter without a default;
  - queries read only the Container Apps log tables and the allowlisted event properties;
  - the workflow checks the parameters file.
- **Accepted terms and privacy versions (P22, UI-001):** the web receives `PAQUETERIA_TERMS_VERSION` and
  `PAQUETERIA_PRIVACY_VERSION` from `apps.bicep` parameters without a default (1 to 64 characters), no
  other workload receives them, `web.parameters.json` is well formed, and the workflow checks and deploys it.

The unit tests compile the real templates whenever the pinned Bicep CLI is present (the azr-static CI
job installs it). They mutate the ARM output to prove that each guard fails closed.

The AZR-001 guards only glob `deploy/azure/*` and never read `deploy/azure/pilot/`; they still pass
31/31.

## 9. Verified here vs. pending

**Verified without Azure:**

- `bicep build` and `bicep lint` pass with the pinned v0.47.16, with no warnings.
- 23/23 pilot guards (P00–P22) and 31/31 AZR-001 guards pass, along with the guard unit tests and the CI
  tooling tests. The gitleaks scan was not re-run for OBS-002.
- The OBS-002 alert and workbook queries parse and type-check offline (Kusto language service).
- The migrator changes pass contract tests on real PostgreSQL 18/PostGIS 3.6.
- The BFF web build and `next start` with the CSP connect source work.

**Unverified until the first real deployment:**

- ARM-level acceptance of every property: the Defender settings API `2025-01-01`, route
  `customDomains` with `bindingType: Auto`, and HTTP validation of the apex managed certificate on a
  route.
- WebSocket upgrade and HTTP→HTTPS redirect through the route custom domain.
- That the runner-side `az` and `docker` steps behave as scripted.
- `SSL Mode=VerifyFull` from the .NET images.
- That PostgreSQL 18 is offered with private access in `mexicocentral`. AZR-001 used PG 18 there
  with public access.
- The ADP-001 adapters against real Azure. They are merged (`AzureKeyVaultPiiKeyWrapClient`,
  `AzureBlobProofObjectStorage`, `DefenderForStorageThreatScanner`) and tested offline, but they have
  never called Key Vault, Blob Storage or Defender for Storage.
- OBS-002:
  - ARM acceptance of the alert rules, action group and workbook;
  - that the JSON console line reaches `Log_s` unchanged;
  - the exact `Reason_s` values Container Apps uses for probe failures and restarts.

## 10. Observability (OBS-002)

Details, event schema, thresholds and tests: `docs/development/obs-002-pilot-observability.md`.

- **Signals.** Nothing new is exported. The API and Worker write JSON console logs that already reach
  Log Analytics, and OBS-002 adds three low-cardinality events on top of the existing OPS-004
  retention lane result (4004):
  - `OutboxLaneSummary` (4601): one per outbox lane per minute, including idle minutes;
  - `ScheduledJobCycle` (4602): one per Worker job cycle;
  - `HttpStatusSummary` (4603): API responses by status class, one per minute.

  Their properties are fixed names and counts only; `ObservabilityArchitectureTests` enforces the
  allowlist.
- **Alerts:** stage 5 of the workflow deploys `observability.bicep`.

  | Rule | Fires when | Every |
  |---|---|---|
  | `sqr-pv-pilot-outbox-lag` | a lane's oldest claimed message waited > 5 min, its loop keeps failing, or it wrote no summary in 15 min | 15 min |
  | `sqr-pv-pilot-outbox-dead` | any message was settled DEAD | 15 min |
  | `sqr-pv-pilot-job-failure` | retention or a scheduled job failed, or retention had no success for an hour | 15 min |
  | `sqr-pv-pilot-readiness` | ≥ 3 probe failures, crash loops or restarts of one app in 15 min | 15 min |
  | `sqr-pv-pilot-api-5xx` | ≥ 5 API 5xx that are also ≥ 5 % of the responses in 15 min | 15 min |

  The rules are stateful: one e-mail when an alert fires and one when it resolves. They go to the
  owner's address in `observability.parameters.json`. Thresholds are template parameters; add them
  to the same file to tune them.
- **Known limit:** no approved function exposes the outbox backlog. Runtime may not `SELECT` the
  outbox (AI-01 §4.7), so lag is the wait of the messages actually claimed, plus stall detection. An
  exact backlog gauge would need a new maintenance function and a normative change.
- **Workbook:** *Paquetenvia pilot operations (OBS-002)*. It shows API responses by status class,
  Worker job outcomes, outbox lanes, and probe failures and restarts.
