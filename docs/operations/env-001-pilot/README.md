# ENV-001 pilot environment on Azure

ENV-001 (`docs/normative/v0.6/specs/AI-08_BACKLOG.yaml`) is the pilot **with real people**
(`PILOT-REAL-PEOPLE`). It is separate from the AZR-001 DEV-SYNTHETIC environment. AZR-001 and
AZR-001-DEC-001 (synthetic data only, public PostgreSQL) do not apply here, and none of the AZR-001
files (`deploy/azure/*.bicep`, `deploy-core.ps1`, `deploy-azure-dev.yml`) or guards were changed.

| Piece | Location |
|---|---|
| Templates | `deploy/azure/pilot/{security,platform,jobs,apps,observability}.bicep` |
| Pilot-only images | `deploy/azure/pilot/Dockerfile.db-ops` (migrator only, plus the repository files it reads before every command: `Paqueteria.sln`, the baseline manifest, AI-06, AI-18, `src/Modules` and the two migration directories under `src/BuildingBlocks/Paqueteria.Infrastructure`), `deploy/azure/pilot/Dockerfile.web` (AUTH-001 BFF build) |
| API and Worker images | `deploy/azure/Dockerfile.api`, `deploy/azure/Dockerfile.worker` (shared; run as the non-root `app` user, `USER $APP_UID`) |
| Business settings reviewed by the owner | `deploy/azure/pilot/apps.settings.json` |
| OBS-002 alert e-mail (owner) | `deploy/azure/pilot/observability.parameters.json` |
| Accepted terms and privacy versions for the web (owner, UI-001) | `deploy/azure/pilot/web.parameters.json` |
| Deployment workflow | `.github/workflows/deploy-azure-pilot.yml` (`workflow_dispatch`, GitHub Environment `azure-pilot`) |
| Static guards | `tools/azr-001/env001_pilot_guards.py` (26 guards) and `test_env001_pilot_guards.py` |
| Restore drill and real restore | `deploy/azure/pilot/restore-drill.sh` (§6.4) |
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
| ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20 | Blob and container soft delete 30 days on the proof storage, versioning off: a deleted or overwritten photo can be recovered for 30 days (§6.4, guard P12) |
| OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20 | The PostgreSQL server is one setting, the `azure-pilot` variable `PILOT_POSTGRES_SERVER_NAME` (empty = the first server). After a real restore the owner sets it to the restored server and re-runs the workflow; migrations, API, Worker, alerts and drills follow it, and the original server stays untouched with its backups until the owner deletes it (§6.4, guard P25) |
| GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20 | The driver eligibility rules per vehicle type (per-order capacity and required documents) in `apps.settings.json` (§4 "Driver eligibility rules"); GATE-010 stays open |
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
| Storage | `stpvpilot<suffix13>` / container `proofs`: Standard LRS, versioning off, blob and container soft delete 30 days (§6.4) |
| PostgreSQL | the server named by `PILOT_POSTGRES_SERVER_NAME`, by default `pg-pv-pilot-<suffix>` (§6.4): 18, Burstable B1ms, 32 GB with auto-grow, 14-day PITR backups, `require_secure_transport=ON`, `azure.extensions=POSTGIS,PGCRYPTO`, `password_encryption=SCRAM-SHA-256` |
| Container Apps environment | `cae-pv-pilot-<suffix>` |
| Apps / jobs | `ca-pv-pilot-{api,worker,web}`, `job-pv-pilot-{migrate,logins,verify}` |
| Alerts (OBS-002, §10) | `sqr-pv-pilot-{outbox-lag,outbox-dead,job-failure,readiness,api-5xx}`, action group `ag-pv-pilot-ops` (e-mail only), workbook *Paquetenvia pilot operations (OBS-002)* |
| PostgreSQL connection alerts (SCL-007, §10.2) | `mar-pv-pilot-pg-connections-{warning,critical}` (metric alerts on the server named by `PILOT_POSTGRES_SERVER_NAME`, by default `pg-pv-pilot-<suffix>`, same action group) |
| PostgreSQL resource alerts (SCL-007, §10.3) | `mar-pv-pilot-pg-{cpu,memory,storage,cpu-credits}-warning` (metric alerts on the same server, `PILOT_POSTGRES_SERVER_NAME`, same action group) |

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
- DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: on a pilot database whose baseline predates the operator
  outbox executor, the Dispatch lane `20261003000100_AddOperatorOwnerOutboxExecutor` stops in its E-002 bridge
  with `E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_operator_outbox_executor` until an Azure
  administrator has created `paqueteria_operator_outbox_executor NOLOGIN BYPASSRLS` with SET for the deployment
  role (like the lifecycle executor); nothing is written before that check. The role is never granted to the API,
  the Worker or an operator login.
- ORD-AUTO-CLOSE-2026-10-10: on a pilot database whose baseline predates the auto-close executor, the Orders lane
  `20261010000100_AddOrderAutoCloseDiscovery` stops in its E-002 bridge with
  `E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_auto_close_executor` until an Azure administrator has
  created `paqueteria_auto_close_executor NOLOGIN BYPASSRLS` with SET for the deployment role; nothing is written
  before that check. The role is never granted to the API, the Worker or an operator login. The Worker runs the job
  with `Orders__AutoClose__Enabled=true` (`apps.bicep`); its `orders_auto_close` readiness check fails until the lane
  has installed the discovery function.
- `job-pv-pilot-logins` runs the new migrator command `runtime-logins`. It creates or re-keys
  `pv_pilot_api` → `paqueteria_app`, `pv_pilot_worker` → `paqueteria_worker` and `pv_pilot_realtime` →
  `paqueteria_realtime` as `LOGIN NOINHERIT NOBYPASSRLS` with exactly one membership. It receives
  **SCRAM-SHA-256 verifiers only**, so no plaintext runtime password reaches PostgreSQL or its logs.
- RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 (owner: "Rol propio y mínimo (Recommended)"): the API runs the
  realtime outbox dispatchers in process (`Realtime__OutboxDispatcher__Provider=PostgreSql`) with its **own**
  login, `pv_pilot_realtime`, never the Worker's. Its role `paqueteria_realtime` (NOLOGIN NOBYPASSRLS, created by
  AI-18 with no privilege) receives from the Notifications lane step `20261012000100_AddRealtimeDispatcherRole`
  exactly: EXECUTE on the six lease-token claim, settle and stale-requeue functions of the REALTIME business lane
  and the location lane; USAGE on seven schemas; SELECT on the 53 evidence columns, each also readable by
  `paqueteria_app`. No write, no outbox table, no other lane, no maintenance or purge
  (`docs/development/rtm-api-realtime-dispatcher-role.md`). On a fresh pilot database the E-002 bridge pre-creates
  the role like every AI-18 role; the lane needs no extra administrator step. The API's readiness check
  `realtime_outbox_dispatcher` is unhealthy if its login could assume `paqueteria_worker` or `paqueteria_app`.
- `job-pv-pilot-verify` runs the read-only `assert` after every deployment. The restore drill also
  uses it.
- TEN-003-PLATFORM-BOOTSTRAP-2026-10-19: the owner runs the migrator command `platform-bootstrap` once, in an
  execution of `job-pv-pilot-migrate` with its own arguments (§6.1 step 10), on the migration connection. It reads
  every tenant as the deployment login (BYPASSRLS, E-002) only to refuse a second run, then writes as
  `paqueteria_migrator` (NOBYPASSRLS) under FORCE RLS with the new UUIDs pre-authorized only in its transaction.
  The deployment login holds no SET on `paqueteria_app`, so the API's tenant transaction cannot run there. No role,
  grant or function is added (`docs/development/ten-003-platform-bootstrap.md`).

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
| Next.js rewrites (`PAQUETENVIA_API_PROXY_ORIGIN`) | 0 | Adds a Node hop to every API call, makes Web a dependency of the API, and puts WebSocket proxying in Node. Kept as the documented fallback: set the variable on Web, route only `/` to Web, and set `Http__ForwardedHeaders__ForwardLimit=2` on the API (see *Forwarded headers* below). |

- **WebSockets (`/hubs`):** API and Web ingresses use `transport: auto`, which supports the HTTP/1.1
  upgrade. SignalR still falls back to SSE or long-polling if an upgrade fails. The WebSocket check
  after the first deploy is manual (§6.1 step 11).
- **Back-channel logout:** the environment ingress does not inspect `Origin`, and the API accepts
  `POST /auth/backchannel-logout` without it (auth-001 §14.2). The workflow smoke test sends such a
  POST through the route and requires the API's `{"error":"invalid_request"}`.
- **Forwarded headers:** the API trusts `X-Forwarded-For`/`X-Forwarded-Proto` only from the
  Container Apps subnet (`Http__ForwardedHeaders__KnownNetworks__0=10.60.0.0/23`, `ForwardLimit=1`).
  With the chosen routing (one Envoy hop) this gives the API the real client address, which the SEC-004
  sign-in limit (20 per minute per client network) depends on. The Next.js rewrites fallback adds a
  hop: Web reaches the API through the API's internal ingress, which adds Web's address. With
  `ForwardLimit=1` every sign-in would then share one bucket, so the fallback needs
  `Http__ForwardedHeaders__ForwardLimit=2`. The middleware stops at the first address outside
  `KnownNetworks`, so a client still cannot pass a forged address. §6.1 step 11 checks the result.

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
| API (`id-pv-pilot-api`) | `pg-api-runtime-connection` → `ConnectionStrings:Paqueteria`; `authcenter-paquetenvia-client-secret` → `AuthCenter:ClientSecret`; `paquetenvia-email-lookup-key-1` → `EmailLookup:Keys:1`; `google-maps-api-key` → `Locations:GoogleMaps:ApiKey`; `public-tracking-link-key` → `PublicTracking:LinkKeys:1`; `pg-realtime-runtime-connection` → `ConnectionStrings:PaqueteriaRealtime` (the in-process realtime dispatchers; never the Worker's connection); `paquetenvia-telemetry-pseudonym-key-1` → `TelemetryPseudonym:Keys:1` (OBS-003) |
| Worker (`id-pv-pilot-worker`) | `pg-worker-runtime-connection` → `ConnectionStrings:PaqueteriaWorker` and `ConnectionStrings:Paqueteria` (one secret, read once, mapped to both keys); `paquetenvia-telemetry-pseudonym-key-1` → `TelemetryPseudonym:Keys:1` (OBS-003) |
| Migrate and verify jobs (`id-pv-pilot-migrate`) | `pg-migrate-connection` → `PAQUETERIA_MIGRATION_CONNECTION` |
| Logins job (`id-pv-pilot-logins`) | `pg-migrate-connection` → `PAQUETERIA_MIGRATION_CONNECTION`; `pg-api-login-verifier` → `PAQUETERIA_API_LOGIN_VERIFIER`; `pg-worker-login-verifier` → `PAQUETERIA_WORKER_LOGIN_VERIFIER`; `pg-realtime-login-verifier` → `PAQUETERIA_REALTIME_LOGIN_VERIFIER` |

The GitHub runner is outside the VNet. `deploy/azure/pilot/kv-firewall.sh` opens a temporary `/32`
rule for the runner's public IP only around its secret reads and writes, and an `always()` step closes
it. There are two windows:
- generated secrets and the platform parameters;
- the database connection secrets.

Every redeploy of `security.bicep` also resets `ipRules` to empty. A secret rotation takes effect when
the app restarts or starts a new revision; the workflow restarts the API and Worker after a rotation, and after a
new PostgreSQL server (OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20), and starts either one if its revision is stopped.

| Key Vault secret | Written by | Read by |
|---|---|---|
| `pg-admin-password` | workflow (generated once, never printed) | workflow only |
| `pg-migrate-connection` | workflow (every run, derived from the admin password and the server in service, `postgresHost`) | migrate, logins and verify jobs |
| `pg-api-runtime-connection` / `pg-worker-runtime-connection` / `pg-realtime-runtime-connection` | workflow (generated once, 64 hex chars; again with `rotate_runtime_logins` or when its host is not the server in service) | API / Worker / API realtime dispatchers |
| `pg-api-login-verifier` / `pg-worker-login-verifier` / `pg-realtime-login-verifier` | workflow (SCRAM of the above) | logins job (its own identity) |
| `paquetenvia-email-lookup-key-1` | workflow (generated once: 32 random bytes, base64) | API (`EmailLookup__Keys__1`) |
| `authcenter-paquetenvia-client-secret` | **owner** (AuthCenter hands it over once) | API (`AuthCenter__ClientSecret`) |
| `google-maps-api-key` | **owner** (Google Cloud Console, GATE-003-PROVIDER-GOOGLE) | API (`Locations__GoogleMaps__ApiKey`) |
| `public-tracking-link-key` | **owner** (32 random bytes, base64; TRK-002-AUTO-LINK) | API (`PublicTracking__LinkKeys__1`) |
| `paquetenvia-telemetry-pseudonym-key-1` | **owner** (32 random bytes, base64; OBS-003-TENANT-PSEUDONYM-2026-10-12) | API and Worker (`TelemetryPseudonym__Keys__1`) |
| `pg-restore-drill-connection` | `restore-drill.sh` | verify job, during a drill only |

**Reserved names (not created, for later work):**

| Secret | For |
|---|---|
| `whatsapp-cloud-api-token` | GATE-004-CHANNELS (Meta Cloud API access token) → Worker `Messaging:WhatsApp:MetaCloudApi:AccessToken` |
| `whatsapp-phone-number-id` | GATE-004-CHANNELS (WhatsApp Business phone number id) → Worker `Messaging:WhatsApp:MetaCloudApi:PhoneNumberId` |
| `whatsapp-app-secret` | GATE-004-CHANNELS (webhook signature verification; no webhook exists yet, so nothing reads it) |

Azure Communication Services email uses the Worker's managed identity, so it needs no secret.
It is **not** in the Bicep yet. The resources themselves are free (about 0.00025 USD per email).
ACS retires on 2028-09-30 and Microsoft starts limiting onboarding on 2026-10-23, so the owner creates
the Communication Services and Email Communication Services resources by hand before that date
(NTF-EMAIL-ACS-PILOT-2026-10-11, `guia-owner-seccion-a.md` §8); a later change references them from
the templates and wires the Worker. Production needs another email provider (AI-10 `release_gates`).
Binding `paquetenvia.com` as a sender needs its own DNS records (domain TXT, SPF, DKIM x2) and
Meta/ACS sender setup. The adapters exist (`docs/development/gate-004-messaging-adapters.md`) but
are **not wired into the pilot templates**: every `Messaging` channel stays `Disabled` until the owner
supplies the secrets, the approved templates and the sender domain, and a separate change adds the
Worker mappings, the per-secret RBAC and the guard allowlist together (a mapped secret that does not
exist stops the Worker at start).

App settings wired by `apps.bicep`:

- **API:**
  - `ConnectionStrings__Paqueteria`.
  - `ConnectionStrings__PaqueteriaRealtime` (Key Vault `pg-realtime-runtime-connection`) for the in-process realtime
    outbox dispatchers (`Realtime__OutboxDispatcher__Provider=PostgreSql`, `WorkerId=pilot-api`). The API refuses to
    start without it, or when its login equals the API's or the Worker's; it never receives
    `ConnectionStrings__PaqueteriaWorker`.
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
value is still `OWNER_DECISION_REQUIRED` or while the driver eligibility rules are missing (below). Today the
file carries `Finance__OperationalTimeZone=America/Mazatlan` and the driver eligibility rules the owner approved
(GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20, below), so it no longer blocks the first deploy.

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

**Driver eligibility rules (GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20).** The API runs with
`Drivers__Provider=PostgreSql` and `Routing__Provider=PostgreSql`, so it refuses to start unless the same file gives
every vehicle type (`MOTORCYCLE`, `CAR`, `VAN`, `BICYCLE`, `WALKER`) its rules:

- `Drivers__Eligibility__RequiredDocumentTypesByVehicleType__<TYPE>__<n>`, numbered from `0`: the documents a driver
  with that vehicle type needs, at least one, each one of `IDENTITY`, `DRIVER_LICENSE`, `VEHICLE_CARD`, `INSURANCE`,
  `BACKGROUND_CHECK` or `OTHER`;
- `Drivers__Eligibility__VehicleCapacity__<TYPE>__MaximumPackageCount`, `__MaximumTotalWeightGrams`,
  `__MaximumSinglePackageWeightGrams` (not above the total), `__MaximumLengthMillimeters`, `__MaximumWidthMillimeters`
  and `__MaximumHeightMillimeters`: whole numbers above zero, in plain digits. The order of the three size limits does
  not matter, nor the order in which dispatch types a package's length, width and height: the API sorts the limits
  and each package's sides from largest to smallest before comparing them
  (DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20);
- optionally `Drivers__Eligibility__VehicleCapacity__<TYPE>__RequireDimensions` (`true` refuses a package without
  dimensions; `false` when absent) and `Drivers__Eligibility__NonExpiringDocumentTypes__<n>` (document types that need
  no expiry date; every other required document needs one that has not passed).

There is no default: they are business rules the owner chooses. The file carries the ones the owner approved on
2026-10-20 (G4 "Sí, propónmelos (Recommended)", H2 "Aprobar la tabla (Recommended)", H3 "Aprobar la lista
(Recommended)"), as they were proposed. Capacity is per order:

| Type | Packages | Total weight (g) | Heaviest package (g) | Length × width × height (mm) | `RequireDimensions` | Required documents |
|---|---|---|---|---|---|---|
| `MOTORCYCLE` | 5 | 20000 | 10000 | 400 × 400 × 350 | `false` | `IDENTITY`, `DRIVER_LICENSE`, `VEHICLE_CARD`, `INSURANCE` |
| `CAR` | 10 | 100000 | 20000 | 900 × 800 × 600 | `false` | `IDENTITY`, `DRIVER_LICENSE`, `VEHICLE_CARD`, `INSURANCE` |
| `VAN` | 20 | 400000 | 20000 | 2000 × 1200 × 1200 | `false` | `IDENTITY`, `DRIVER_LICENSE`, `VEHICLE_CARD`, `INSURANCE` |
| `BICYCLE` | 3 | 10000 | 10000 | 450 × 400 × 300 | `false` | `IDENTITY` |
| `WALKER` | 2 | 5000 | 5000 | 450 × 400 × 300 | `false` | `IDENTITY` |

- There is no `NonExpiringDocumentTypes` entry: every required document needs an expiry date that has not passed, and
  a permanent Sinaloa driver license gets a review date the owner defines. There is no `BACKGROUND_CHECK` for now; it
  is decided with GATE-007 and GATE-008 (H3).
- The package counts of `MOTORCYCLE`, `CAR`, `BICYCLE` and `WALKER`, and the weights and size of `WALKER`, are
  assumptions without a source, to be adjusted with the real operation (H2). `INSURANCE` is the owner's policy while
  GATE-009 is open; it is removed if the company contracts a fleet policy (H3).
- **Pickups count as `VAN` and cargo bikes as `BICYCLE`** (H2); there is no other vehicle type.
- **For operations: the limits apply per order.** Each order is compared on its own, with all its packages (total
  weight, heaviest package, the largest length, width and height captured); nobody adds up the orders a driver already
  carries or a route holds. Dispatch decides how many orders each driver carries. A quote does not check any vehicle,
  so an order larger than the `VAN` row can be created but no driver can take it.
- **The API starts, but no real driver can be assigned yet.** Nothing registers or validates driver documents: AI-05
  `/driver/me/documents` is not implemented and only the synthetic DevSeed writes `drivers.driver_documents`. Until the
  AI-08 task DRV-DOC-001 (driver document registration and dispatch validation, owner decision H1) and GATE-007 land,
  every driver is refused with `REQUIRED_DOCUMENT_MISSING`.
- GATE-010 stays open (zones, hours, prohibited items, SLA): this decision covers only these vehicle rules. Changing a
  value is a new owner decision and a PR to this file; an organization's `driver_eligibility_policy_version` (above) is
  not raised automatically, so evaluations under different rules would share one version label until it is.

`env001_pilot_guards.py settings-check` (provenance job and Stage 4) prints `ENV001_SETTINGS=PASS` on the file. If
any rule goes missing it stops with
`STOP_FOR_OWNER_DECISION: driver eligibility rules for vehicle type <TYPE> are missing: ...`, naming each missing
setting, so **the pilot deploy stops before touching Azure** until all five vehicle types are complete again. It
applies the API's rules to every entry present and accepts only the exact names above. Guard P17 (`check`) refuses an
invalid entry but not a missing one. Without this stop, a deploy would fail only at the smoke test (`/auth/session is
not served by the API`) or leave the earlier revision serving. `PilotApiStartupConfigurationTests` starts the pilot API
with the file as it is, checks that Drivers, Routing, Dispatch and Orders bind exactly its entries, and pins the two
start failures without them (`MissingEligibilityStartFailures`); `test_env001_pilot_guards.py` pins the approved
values.

**Dispatch number for the driver app (DRV-DISPATCH-CONTACT-2026-10-12, owner 2026-10-12: "Sí, configurable
(Recommended)").** The driver app shows "Llamar a despacho" (a `tel:` link) next to its texts that ask the driver to
call dispatch about a recorded cash collection, only for an organization with a number configured. The setting is
empty by default and the file has no entry today, so the pilot shows no button until the owner gives the number.
Each organization takes two entries in the same file, numbered `0`, `1`, … without gaps:
`Drivers__DispatchContacts__<n>__OrganizationId` (the organization's UUID, lowercase) and
`Drivers__DispatchContacts__<n>__PhoneE164` (`+52` and ten digits, the first one 2-9). The API refuses to start with
an invalid entry or an organization written twice; `settings-check` (guard P17) applies the same rules before
deploying and never echoes a value (a malformed entry is named by its canonical name or its position in the file).
Configuration names ignore case in .NET, so P17 refuses these names in any other casing and treats names that differ
only in case as the same setting. The number is a business contact, never logged. See
`docs/development/ui-001-phase3-driver-dispatch-contact.md`.

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
| API 0.5 vCPU / 1 GiB, always on | active rate 0.000026 per vCPU-s, 0.000003 per GiB-s (the realtime outbox dispatchers still claim about twice a second while idle, SCL-007, so the replica rarely qualifies for the idle rate) | 42.04 |
| Worker 0.25 vCPU / 0.5 GiB, always on | active rate (continuous polling: the Notifications and Dispatch outbox lanes still poll about twice a second while idle, SCL-007) | 21.02 |
| Web 0.25 vCPU / 0.5 GiB, min 1 | mostly idle rate (0.000003 per vCPU-s) with request bursts | ~7.00 |
| Container Apps monthly free grant | 180,000 vCPU-s + 360,000 GiB-s | −5.76 |
| Container Apps requests and jobs | < 2 M requests free; jobs run for minutes | ~0.10 |
| Container Registry Basic | 0.1666 per day | 5.07 |
| Defender for Storage (per account) | 0.0134 per hour | 9.78 |
| Malware scanning | 0.15 per GB scanned (~0.2 GB of photos) | 0.03 |
| Log Analytics | 5 GB/month free, then 2.53 per GB; cap 0.3 GB/day ⇒ ≤ 9.1 GB | 0 – 10.40 |
| Key Vault | RSA-3072 operations 0.165 per 10 k; secrets 0.033 per 10 k (the API realtime login adds two secrets read once per start: no measurable change) | ~0.30 |
| Blob storage | a few GB Hot LRS | ~0.20 |
| Soft-deleted photos (30 days, ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20) | billed as stored data, Hot LRS 0.02288 per GB (Retail Prices API, 2026-10-20); the Worker deletes each quarantine copy after promotion, so about one month of uploads (~0.2 GB, the malware scanning row) stays soft-deleted, ≈ 0.005; with 7 days it was ≈ 0.001 | < 0.01 |
| Private DNS zone | 0.50 per zone | 0.50 |
| VNet, route, managed certificate, budget | free | 0.00 |
| **Subtotal before OBS-002** | | **≈ 98 typical, ≈ 108 if the log cap is hit every day** |
| Log search alerts (OBS-002, §10) | 5 rules at 15 min (0.55 each), Retail Prices API 2026-09-28; e-mails and workbook free; the extra ~0.3–0.45 GB/month of logs fits the 5 GB free tier | 2.75 |
| PostgreSQL metric alerts (SCL-007, §10.2 and §10.3) | 6 time series on the server (2 connection alerts and 4 resource alerts), inside the 10 free per resource, otherwise about 0.10 USD per series per month, whatever the frequency ([pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/)) | 0 – 0.60 |
| Application Insights (OBS-003, §10) | workspace-based on the same workspace and inside the same 0.3 GB/day cap; component free; with the export on, about 1–2 GB/month of traces (10 %) and metrics; 0 while `TelemetryExport__Enabled='false'` | 0 – 5 (inside the Log Analytics row's worst case) |
| **Total** | | **≈ 101 typical, ≈ 111 if the log cap is hit every day** |

**This design sits at the budget line and does not fit ~100 USD with margin.** The owner accepted this
sizing (PILOT-BUDGET-ACCEPT-98-108, 2026-09-28) without applying any lever. OBS-002 adds about
2.75 USD per month on top of that accepted range, with every alert evaluated every 15 minutes; the
owner accepted it on 2026-10-02 (OBS-002-ALERTS-15MIN-COST-2026-10-02, literal "Sí, revisar cada 15
min"). The six SCL-007 PostgreSQL metric alerts are 6 of the 10 time series free per resource, so they usually add
nothing and at most 0.60 USD per month: the two connection alerts (SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14,
literal "Sí, 80 % y 95 % (Recommended)") and the four resource alerts (SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20,
literal "CPU, memoria y disco (Recommended)" and "Sí, agregarla (Recommended)"; the D2 option counted "5 de las 10"
for three rules, and the fourth, for CPU credits, is still free). The totals stay the same. The largest item is the
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
3. The owner replaces every `OWNER_DECISION_REQUIRED` and supplies the missing owner values (PR → `development` →
   `main`):
   - `deploy/azure/pilot/apps.settings.json`: nothing left to supply. No placeholder remains since
     POLICY-VERSIONS-PER-ORG-2026-10-02, and the driver eligibility rules (documents and capacity per vehicle type)
     are in the file since GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20 (§4 "Driver eligibility rules");
   - the alert e-mail in `deploy/azure/pilot/observability.parameters.json`;
   - `webTermsVersion` and `webPrivacyVersion` in `deploy/azure/pilot/web.parameters.json`, the accepted
     terms and privacy notice versions that come with the approved notice (§4).

   Each file blocks the workflow while a placeholder remains or, in `apps.settings.json`, a driver eligibility rule
   is missing.
4. Promote to `main` and wait for the Foundation CI push run on `main` (13/13 green). Note its run id
   and head SHA.
5. Actions → *Deploy Azure PILOT* → Run workflow on `main`: `tested_git_sha=<head SHA>`,
   `foundation_run_id=<run id>`, `custom_domain_phase=none`.
6. The first run creates the vault, then stops with *"lacks authcenter-paquetenvia-client-secret"*. The
   workflow requires four owner secrets and reports the first one missing. Write all four straight into
   the vault (§6.6) and run again:
   - `authcenter-paquetenvia-client-secret`;
   - `google-maps-api-key`;
   - `public-tracking-link-key`;
   - `paquetenvia-telemetry-pseudonym-key-1` (OBS-003; also needed by a vault populated before OBS-003: the
     next run stops with *"lacks paquetenvia-telemetry-pseudonym-key-1"* until it exists).
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
   The login test in step 11 needs them.
10. **Once, before your first sign-in: create the PLATFORM organization and make your AuthCenter account its
    PLATFORM_ADMIN** (TEN-003-PLATFORM-BOOTSTRAP-2026-10-19, owner decision E1). Self-service onboarding creates
    only BUSINESS and ALLY organizations, and only a PLATFORM_ADMIN of a PLATFORM organization can grant DISPATCHER
    and FINANCE, approve allies and record the master-data gate (§6.8), so without this step nobody can create an
    order. Spanish walkthrough: [`guia-owner-seccion-a.md`](guia-owner-seccion-a.md) §7.5.
    - Your AuthCenter `sub`: in `https://authcenter.info/admin-v2/users` open your own user (*Administrar* or
      *Consultar*). The address ends in `/admin-v2/users/<sub>`: that lowercase UUID is your AuthCenter user id,
      which AuthCenter puts in the `sub` claim. Copy it exactly; the command refuses anything else (an e-mail, the
      whole address, upper case).
    - From Cloud Shell, run the migrator's `platform-bootstrap` once in an execution of the migrate job (same image,
      identity and migration connection; only this execution's arguments change, so the next deployment still runs
      `apply`):

      ```bash
      RG=rg-pv-pilot
      JOB=job-pv-pilot-migrate
      read -r -p 'AuthCenter sub: ' SUB
      LEGAL_NAME='<legal name of the platform organization>'
      DISPLAY_NAME='Paquetenvia'
      az containerapp job show -g "$RG" -n "$JOB" --query properties.template -o json > pv-bootstrap.json
      python3 - "$SUB" "$LEGAL_NAME" "$DISPLAY_NAME" <<'EOF'
      import json, sys
      template = json.load(open("pv-bootstrap.json"))
      container = template["containers"][0]
      container["args"] = ["platform-bootstrap", "--connection-env", "PAQUETERIA_MIGRATION_CONNECTION",
                           "--identity-subject", sys.argv[1], "--legal-name", sys.argv[2], "--display-name", sys.argv[3]]
      json.dump({"containers": [container]}, open("pv-bootstrap.json", "w"))
      EOF
      execution="$(az containerapp job start -g "$RG" -n "$JOB" --yaml pv-bootstrap.json --query name -o tsv)"
      rm -f pv-bootstrap.json
      az containerapp job execution show -g "$RG" -n "$JOB" --job-execution-name "$execution" --query properties.status -o tsv
      ```

      Repeat the last line until it no longer says `Running`, then read the result in Log Analytics:
      `ContainerAppConsoleLogs_CL | where ContainerJobName_s == "job-pv-pilot-migrate" | where Log_s has "TEN003_PLATFORM_BOOTSTRAP" | order by TimeGenerated desc`.
    - `TEN003_PLATFORM_BOOTSTRAP_OK organization_type=PLATFORM status=ACTIVE role=PLATFORM_ADMIN (audited)`: done.
      In one transaction the command created the ACTIVE PLATFORM organization, the user of your `sub` and its
      ACTIVE default PLATFORM_ADMIN membership, and one `INITIAL_ORGANIZATION_PROVISIONED` audit row in that
      organization (request id `ten-003-platform-bootstrap`). Its output names no subject, name or id.
    - Exit 11, `TEN003_PLATFORM_BOOTSTRAP_ALREADY_DONE`: a PLATFORM organization or a PLATFORM_ADMIN already exists.
      Nothing was written and nothing is left to do; the command runs only once.
    - Exit 11, `TEN003_PLATFORM_BOOTSTRAP_SUBJECT_ALREADY_SIGNED_IN`: that account already signed in to Paquetenvia.
      Nothing was written. The command never takes over an existing user, and you must not write SQL by hand.
      Restore the database to a point before that sign-in (§6.4) and run the command again, as the box *Si ya
      iniciaste sesión antes de correr el comando* in the guide §7.5 explains step by step (decision E1b).
    - Exit 2: the `sub` is not a lowercase UUID, or a name is empty, has leading or trailing spaces or control
      characters, or is longer than 200 (legal) or 80 (display) characters. The value is never echoed.
    - Exit 5 naming a migration or an assertion: the database is not fully migrated; run the deployment again
      first. Exit 12 (`TEN003_PLATFORM_BOOTSTRAP_DATABASE_ERROR SQLSTATE=...`): nothing was written; see §6.7.
    - The execution keeps its arguments (your `sub` and the two names) in the job's execution history, readable by
      whoever can read the resource group. None of them is a secret.
    - After you sign in (step 11), `https://paquetenvia.com/api/v1/me/organization-contexts` lists the PLATFORM
      organization with its `organization_id`, which `master-data-gate --platform-organization-id` (§6.8) needs.
      PLATFORM_ADMIN operations ask for the AuthCenter MFA step-up (auth-001 §14.3).
11. Check by hand, in a browser on `https://paquetenvia.com`:
    - Log in through AuthCenter with the account of step 10.
    - In DevTools → Network → WS, `/hubs/...` shows `101 Switching Protocols`.
    - Upload a proof photo, then check that the blob has the Defender scan-result index tag.
    - `/ops/orders/new` allows confirming an order (the terms and privacy versions are set).
    - `http://paquetenvia.com` redirects to HTTPS or is refused. HTTP-to-HTTPS on a route custom domain
      is not verified yet.
    - The API sees the client's address, not one inside `10.60.0.0/23` (SEC-004 sign-in limit, §3
      *Forwarded headers*). Use two networks nobody else is signing in from, for example a laptop
      tethered to a phone and another connection. From the first, run
      `for i in $(seq 21); do curl -s -o /dev/null -w '%{http_code} ' 'https://paquetenvia.com/auth/login?return_url=%2Fops%2Fdashboard'; done`:
      twenty redirects, then `429`. Within the same minute, from the second network, one such request
      must still redirect. A `429` there means every client shares one bucket: check `ForwardLimit` and
      `KnownNetworks`. Repeat after any routing change, and after switching to the Next.js fallback.
12. Run the restore drill (§6.4) and keep its `EVIDENCE` line. The ENV-001 criterion "restore drill
    evidence is current before pilot start" requires it.

**Owner actions for RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12:** none by hand. The workflow generates
`pg-realtime-runtime-connection` and `pg-realtime-login-verifier` (also on a vault populated by an earlier run:
they are created the first time they are missing), the logins job creates `pv_pilot_realtime`, the migrate job
grants the role and `apps.bicep` gives the API identity *Key Vault Secrets User* on that one secret. After step 11,
the owner can confirm in Log Analytics that `ca-pv-pilot-api` logs `realtime_outbox_dispatcher_started` with
`provider=postgresql` and no `realtime_outbox_message_failed`.

**Driver eligibility rules (step 3, found by `PilotApiStartupConfigurationTests`):** with
`Drivers__Provider=PostgreSql` and `Routing__Provider=PostgreSql` the API refuses to start unless
`apps.settings.json` carries the driver eligibility rules for every vehicle type (MOTORCYCLE, CAR, VAN, BICYCLE,
WALKER): `Drivers__Eligibility__RequiredDocumentTypesByVehicleType__<TYPE>__<n>` and
`Drivers__Eligibility__VehicleCapacity__<TYPE>__{MaximumPackageCount,MaximumTotalWeightGrams,MaximumSinglePackageWeightGrams,MaximumLengthMillimeters,MaximumWidthMillimeters,MaximumHeightMillimeters}`
(optionally `Drivers__Eligibility__NonExpiringDocumentTypes__<n>` and
`Drivers__Eligibility__VehicleCapacity__<TYPE>__RequireDimensions`; §4 "Driver eligibility rules"). The file carries
the rules the owner approved (GATE-010-VEHICLE-ELIGIBILITY-PILOT-2026-10-20), so the API starts and `settings-check`
passes; it stops the deploy before touching Azure if any goes missing. After the deploy no real driver can be assigned
until DRV-DOC-001 and GATE-007 land: every driver gets `REQUIRED_DOCUMENT_MISSING` (§4).

### 6.2 Redeploy

Promote, wait for 13/13 on `main`, then dispatch with the new SHA and run id. Keep
`custom_domain_phase=bind` once the domain is bound: `none` would unbind the apex. On every run the
workflow:

- redeploys the five templates idempotently (stage 5 is the OBS-002 alerts, §10) on the PostgreSQL server named by
  `PILOT_POSTGRES_SERVER_NAME` (empty: the first server);
- keeps every generated secret (a runtime credential only while its host is that server);
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

What Microsoft documents ([backup and restore](https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore)),
and why the pilot moves to the restored server instead of replacing the original
(OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20, owner: "Que el despliegue lo siga (Recommended)"):

- "A restore operation in your Azure Database for PostgreSQL flexible server instance always creates a new database
  server with the name that you provide. It doesn't overwrite the existing database server."
- It is created with the source's pricing tier, compute, storage size, backup retention period and backup redundancy.
  "The values of all parameters on the original server aren't automatically applied to the new server", and firewall
  rules, private endpoints and virtual network rules "aren't copied from the original server".
- A private-access server can be restored into the same virtual network or another one, never to public access.
  `restore-drill.sh` passes no `--vnet`, `--subnet` or `--private-dns-zone`, so the Azure CLI gives the new server the
  source's delegated subnet and private DNS zone (`az postgres flexible-server restore`, Azure CLI source).
- "If you delete a server, all backups that belong to the server are also deleted and can't be recovered."

So the PostgreSQL server is one setting, the `azure-pilot` environment variable `PILOT_POSTGRES_SERVER_NAME`.
Empty `PILOT_POSTGRES_SERVER_NAME` (the default) means the first server, `pg-pv-pilot-<suffix>` (its "base" name). Any
other value must be the base name followed by `-` parts of lowercase letters and digits (at most 63 characters); the
workflow refuses anything else before it touches Azure. Every consumer follows it: `platform.bicep` (parameter
`postgresServerName`) and its `postgresName` and `postgresHost` outputs, `pg-migrate-connection`, the three runtime
credentials and their logins, the migrate, logins and verify jobs, the SCL-007 alerts and `restore-drill.sh`.

- **Drill:** does not touch the pilot database.

  ```bash
  ./deploy/azure/pilot/restore-drill.sh --resource-group <rg> --restore-time 2026-10-05T13:10:00Z
  ```

  It restores the server in service (the `postgresName` output of the last deployment, which follows
  `PILOT_POSTGRES_SERVER_NAME`) into a new private server `<base>-drill-<UTC minute>`, where `<base>` is the first
  server's name, re-points `job-pv-pilot-verify` to it, runs `assert` (baseline plus every module lane) and puts the
  job back. It deletes the drill server unless you pass `--keep`, and prints an `EVIDENCE ...` line. Keep that line
  for the pilot readiness report.
- **Real restore** (in Cloud Shell, bash, from a copy of the repository):
  1. **Stop the writers.** Put the API and the Worker in multiple revision mode, where Microsoft documents that
     revisions can be activated or deactivated ("Changing the revision mode doesn't create a new revision"), and
     deactivate the running revision, which "stops all running replicas of a revision" (*Manage revisions* and
     *Revisions* on Microsoft Learn). Scaling cannot stop them: the maximum replica count is at least 1.

     ```bash
     RG=rg-pv-pilot
     for app in ca-pv-pilot-api ca-pv-pilot-worker; do
       revision="$(az containerapp show -n "$app" -g "$RG" --query properties.latestRevisionName -o tsv)"
       az containerapp revision set-mode -n "$app" -g "$RG" --mode multiple
       az containerapp revision deactivate -n "$app" -g "$RG" --revision "$revision"
     done
     ```

     The web keeps answering, but sign-in and every API call fail until step 5, whose run starts both revisions
     again once the logins job has run and then puts the apps back in single revision mode (`apps.bicep`).
  2. **Restore into a new server.**

     ```bash
     ./deploy/azure/pilot/restore-drill.sh --resource-group "$RG" --restore-time <YYYY-MM-DDTHH:MM:SSZ> --real
     ```

     `--real` restores the server in service into `<base>-r<UTC minute>` (for example `<base>-r202610201310`; the
     minute is when the command starts), in the same subnet and private DNS zone, and runs the read-only `assert` on
     it. It refuses to start if a server with that name already exists. It keeps the new server only if everything
     succeeds (a failed run deletes it; the original is never touched) and then prints `restored_server_kept=<name>` and
     `next_step=set the azure-pilot variable PILOT_POSTGRES_SERVER_NAME to <name> ...`.
  3. **Check the restore.** The `EVIDENCE` line must say `verify_status=Succeeded`. Keep it.
  4. **Set the variable.** GitHub → *Settings → Environments → azure-pilot → Environment variables*:
     `PILOT_POSTGRES_SERVER_NAME` = the name of step 2 (or
     `gh variable set PILOT_POSTGRES_SERVER_NAME --env azure-pilot --repo Eduard0corona/Paquetenvia --body <name>`).
     Do not use a dispatch input for it: the variable persists, so every later run keeps the restored server.
  5. **Re-run *Deploy Azure PILOT*** with the current certified SHA and its Foundation run id, and the same
     `custom_domain_phase` as before (`bind` once the domain is bound). The run:
     - checks that the named server exists and already has the values a server cannot change later
       (`postgres-adoption-check`: Ready, standalone, same region, administrator login and PostgreSQL version, private
       access in the template's subnet and private DNS zone, geo-redundant backup off, a disk no larger than the
       template's), so a mistyped name stops the run instead of creating a second, empty server;
     - lets `platform.bicep` update the restored server in place and set its server parameters again
       (`require_secure_transport`, `azure.extensions`, `password_encryption`), which a restore does not copy;
     - rewrites `pg-migrate-connection` for the new host, and writes new runtime credentials and verifiers for the API,
       the Worker and the realtime dispatchers because the kept ones name the old host (as `rotate_runtime_logins`
       does); the migrate, logins and verify jobs then run on the restored server and the logins job applies the new
       verifiers there;
     - after the logins job, starts the stopped API and Worker revisions, which read the new credentials as they
       start; `apps.bicep` puts them back in single revision mode, and stage 5 moves the six SCL-007 alerts to the
       restored server.
  6. **Check.** The run summary shows `PostgreSQL server: <name>`. Sign in at `https://paquetenvia.com` and look at
     the restored data. `az monitor metrics alert show -g "$RG" -n mar-pv-pilot-pg-connections-warning --query scopes`
     names the restored server.
  7. **Later, delete the old server, when you are sure.** Until then it is untouched, keeps running and keeps its own
     14 days of backups, the only way back to a moment before the restore point. It also costs about 17.70 USD a month
     (§5: B1ms compute 13.65 plus 32 GB of storage 4.05), on top of a pilot that already sits at the 100 USD budget.
     Deleting it deletes all its backups for good:

     ```bash
     az postgres flexible-server delete -g "$RG" -n <old server> --yes
     ```

     Never delete the server named in `PILOT_POSTGRES_SERVER_NAME`. To go back to the old server before deleting it,
     empty the variable (or set it to the old name) and re-run the workflow: everything follows it the same way, and
     whatever was written on the restored server stays there.
- **Extra drill servers.** A drill deletes its server unless `--keep`; if the delete fails it prints
  `WARNING drill_server_not_deleted=<name>`. Each drill server is a full copy of the pilot data and costs the same as
  the pilot server. List the servers and delete every `-drill-` one you no longer need, never the one in service:

  ```bash
  az postgres flexible-server list -g "$RG" --query "[].name" -o tsv
  az postgres flexible-server delete -g "$RG" -n <base>-drill-<UTC minute> --yes
  ```

#### Proof photos (Blob Storage)

The point-in-time restore above brings back PostgreSQL only. Delivery photos live in the storage account and
have their own protection (ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20, owner: "Borrado suave más largo
(Recommended)"):

- **What can be recovered, for 30 days:**
  - a deleted photo;
  - the previous state of an overwritten photo, kept as a soft-deleted snapshot, since versioning is off
    ([soft delete for blobs](https://learn.microsoft.com/en-us/azure/storage/blobs/soft-delete-blob-overview));
  - the whole `proofs` container with every photo in it, if the container is deleted
    ([container soft delete](https://learn.microsoft.com/en-us/azure/storage/blobs/soft-delete-container-overview)).
- **What cannot:**
  - anything after its 30 days;
  - deleting the storage account or the resource group (§6.5): every photo is lost;
  - changes to a blob's metadata or properties.
- **When the 30 days start:** Azure fixes the expiry when an object is deleted or overwritten. Photos deleted
  before the deployment that raised the period from 7 to 30 days keep their 7 days.
- **Which names matter:** the app never deletes or overwrites a promoted photo. The Worker copies each validated
  upload once to `proofs/<owner organization id>/<order id>/<upload session id>` in the container `proofs`, only if
  that name is still free, and then deletes the upload copy under `quarantine/`. Soft-deleted `quarantine/` blobs are
  therefore normal; restore only names under `proofs/`. The database row keeps pointing at the same name, so a
  restored photo needs no other change.
- **Who can restore:** shared keys are disabled, so use your Microsoft Entra sign-in. You need *Storage Blob Data
  Contributor* on the account or the container `proofs`, the least-privileged built-in role for Undelete Blob and
  Restore Container. The templates never assign it to a person: grant it temporarily and remove it afterwards, as
  with the Key Vault role in §6.6.

**Steps in the Azure portal:**

1. Open the storage account `stpvpilot<suffix13>` → *Containers* → `proofs` and turn on *Show deleted blobs*.
   Deleted blobs show the status *Deleted* and how many days remain.
2. **Deleted photo:** open the blob and select *Undelete*.
3. **Overwritten photo:** open the blob and select *Undelete*. This restores its soft-deleted snapshots even
   when the blob itself is not deleted. Then open *Snapshots*, choose the snapshot from before the overwrite and
   select *Promote snapshot*.
4. **Deleted container:** in *Storage browser* → *Blob containers*, turn on *Show deleted containers*, select
   `proofs` and *Restore*. Do this before re-running the pilot workflow: the workflow recreates `proofs`, and a
   container with the same name blocks the restore until it is deleted again.

**The same from Cloud Shell** (`--auth-mode login`):

```bash
# Deleted photos of one order:
az storage blob list --account-name <account> -c proofs --prefix 'proofs/<owner>/<order>/' --include d \
  --auth-mode login --query "[?deleted].name" -o tsv
az storage blob undelete --account-name <account> -c proofs -n '<blob name>' --auth-mode login

# Deleted container:
az storage container list --account-name <account> --include-deleted --auth-mode login \
  --query "[?deleted].{name:name, version:version}" -o table
az storage container restore --account-name <account> -n proofs --deleted-version <version> --auth-mode login
```

To promote a snapshot of an overwritten photo, use the portal (step 3).

**Cost:** soft-deleted data is billed as stored data. In the pilot that is mostly the upload copies the Worker
deletes, about one month of uploads (~0.2 GB): under 0.01 USD per month (§5). Even if every photo of the pilot
(a few GB) were deleted at once, keeping them for 30 days would cost well under 0.10 USD.

### 6.5 Destroy

```bash
az group delete --name <rg> --yes
```

This removes everything in the group. The budget and role assignments are scoped to it.

- **Proof photos:** the storage account goes with the group, and soft delete does not protect against that, so
  every delivery photo is lost (§6.4).
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
# OBS-003-TENANT-PSEUDONYM-2026-10-12: 32 random bytes, base64, generated in place and never shown.
umask 077 && openssl rand -base64 32 | tr -d '\n' > /tmp/tp && \
az keyvault secret set --vault-name <kv> --name paquetenvia-telemetry-pseudonym-key-1 --file /tmp/tp --encoding utf-8 --output none; \
shred -u /tmp/tp
bash deploy/azure/pilot/kv-firewall.sh close <kv>
```

`public-tracking-link-key` derives every public tracking link (HMAC-SHA256; only the SHA-256 of each
link is stored). Write it once and keep it: replacing the value under the same version makes the API
refuse to show existing links (it fails closed). To rotate, store the new key under a new secret name,
map it as `PublicTracking:LinkKeys:2`, set `PublicTracking__CurrentLinkKeyVersion=2` and keep version 1
mapped while its links should still be shown to operators; links already sent keep working either way,
because the public lookup only compares hashes. Rotation is a template change reviewed like any other.

`paquetenvia-telemetry-pseudonym-key-1` keys the organization pseudonym (OBS-003, HMAC-SHA256) that the API's and the
Worker's log lines, and the API's request spans once the export is on, carry instead of the organization id. The API and the Worker
refuse to start without it. Write it once. To rotate, store `paquetenvia-telemetry-pseudonym-key-2`, then one reviewed
PR maps it to `TelemetryPseudonym:Keys:2` in `apps.bicep` (API and Worker), sets
`TelemetryPseudonym__CurrentKeyVersion='2'` and drops version 1; exactly one key is configured. The same PR sets
`TELEMETRY_PSEUDONYM_KEY_VERSION = "2"` in `tools/azr-001/env001_pilot_guards.py` (P06, P21 and P23 follow it), changes
the workflow's `has_secret` check in window 2 to the new name (P21 requires it), and updates the tests that name
version 1. Pseudonyms carry their key version (`v1-…`, `v2-…`) and do not match across versions. Keep the old secret until the logs carrying its
version are past the 30-day retention. Details: `docs/development/obs-003-appinsights-export.md`.

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
  versions, the logins job re-keys `pv_pilot_api`/`pv_pilot_worker`/`pv_pilot_realtime`, and the API and
  Worker revisions restart. Expect a few seconds of errors while the restart completes. Never delete these secrets:
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
- **PostgreSQL connections:** B1ms has `max_connections` 50, of which Azure reserves 15, so 35 are user
  connections. SCL-007 gives each process one pool per database role: the API's `paqueteria_app` pool (10) and its
  realtime dispatcher pool (`paqueteria_realtime`, 4), and the Worker's `paqueteria_worker` pool (10), 24 in all
  (`docs/development/scl-007-pool-per-role.md`). The sizes come from `DatabasePools` (defaults in each image's
  `appsettings.json`) and replace the `Maximum Pool Size=5` of the runtime secrets. Each process refuses to start when
  the three values of its own configuration sum to more than 24, but it cannot see the other process's values, so the
  24 holds only when both apps carry the same three. Change the sizes only by setting all three variables,
  `DatabasePools__ApplicationMaximumPoolSize`, `DatabasePools__RealtimeMaximumPoolSize` and
  `DatabasePools__WorkerMaximumPoolSize`, with identical values on both `ca-pv-pilot-api` and `ca-pv-pilot-worker`;
  never on one app alone. Today the Bicep sets none. The other 11 are for the migrate, verify and logins jobs
  (`Maximum Pool Size=4`), the restore drill and administration. `too many connections` means lowering all three
  `DatabasePools__*` on both apps, identically, or moving to B2s (+≈41 USD/month, over budget).
- **A PostgreSQL connection alert fired** (`mar-pv-pilot-pg-connections-warning` at 28,
  `mar-pv-pilot-pg-connections-critical` at 33): follow §10.2.
- **A PostgreSQL resource alert fired** (`mar-pv-pilot-pg-cpu-warning`, `-memory-warning`, `-storage-warning` or
  `-cpu-credits-warning`): follow §10.3.
- **Realtime dispatcher not ready:** `/health/ready` reports `realtime_outbox_dispatcher` unhealthy when
  `pg-realtime-runtime-connection` is missing or its login is not `pv_pilot_realtime` (member of
  `paqueteria_realtime` only), when the Notifications lane step `20261012000100_AddRealtimeDispatcherRole` has not
  run, or when the login is a member of any other role (directly or through a chain) or has SUPERUSER, BYPASSRLS,
  CREATEROLE, CREATEDB, REPLICATION or INHERIT. Re-run the workflow (it regenerates a missing secret and the logins job
  resets the attributes and the direct memberships).
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

The 26 guards (P00–P25) run on the compiled ARM output, the workflow, the pilot Dockerfiles and the restore drill
script:

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
    P06 includes `TelemetryPseudonym:Keys:1` for the API and the Worker and treats `TelemetryPseudonym__Keys__*` as
    sensitive, so the key can never be a plain setting.
  - Workloads run as `Production`/`PILOT_REAL_PEOPLE`, with no Mock or Synthetic values.
- **Replicas and migrations:** a single API and Worker replica on `InProcess`. Migrations run only
  through the canonical migrator jobs, and the pilot db-ops image contains no DevSeed.
- **Routing:** the exact same-origin rules, with internal ingress for API and Web.
- **Data services:**
  - PostgreSQL is private, version 18, with ≥ 7-day backups, secure transport and the extension
    allowlist, in a VNet-injected Consumption environment.
  - Storage flags and Defender scanning are set. Blob versioning is off, and blob and container soft delete are on
    for exactly 30 days with no permanent delete (P12, ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20).
  - Key Vault denies public traffic (only the Container Apps subnet, no bypass, no standing IP rule) and has RBAC, purge protection and wrap-only RSA keys of at least 3072 bits.
- **RBAC:** role assignments stay within the allowlist.
- **Cost:** the 100 USD budget exists and the log cap is set.
- **ADP-001 and web:**
  - The ADP-001 configuration contract is wired.
  - Business settings cannot override platform settings (P17; `TelemetryExport__` and `TelemetryPseudonym__` are
    platform-managed). Driver eligibility entries follow the API's start rules (P17), and `settings-check` also
    requires them for every vehicle type (§4).
  - The web image is a BFF build.
- **Observability (P21, OBS-002):**
  - exactly the five alert rules, every 5–15 minutes, costing at most 5 USD per month;
  - stateful rules on the pilot workspace;
  - one action group, e-mail only, bound to a parameter without a default;
  - queries read only the Container Apps log tables and the allowlisted event properties;
  - the workflow checks the parameters file.
- **PostgreSQL connection and resource alerts (P01 and P21, SCL-007):**
  - `Microsoft.Insights/metricAlerts` only in `observability.bicep` (P01), and there exactly six, each one literal
    global resource: `mar-pv-pilot-pg-connections-warning`, `mar-pv-pilot-pg-connections-critical`,
    `mar-pv-pilot-pg-cpu-warning`, `mar-pv-pilot-pg-memory-warning`, `mar-pv-pilot-pg-storage-warning` and
    `mar-pv-pilot-pg-cpu-credits-warning`; any other metric alert is refused, a second level for a resource alert and
    one on Application Insights included;
  - each watches only the server in service, `[resourceId(..., parameters('postgresServerName'))]` (P25 pins the
    parameter to `PILOT_POSTGRES_SERVER_NAME`): the Maximum of `active_connections`
    over 5 minutes (`PT5M`), every minute (`PT1M`), `GreaterThanOrEqual` 28 with severity 2 and 33 with severity 1,
    one static criterion with no dimension, enabled and stateful;
  - the resource alerts watch the same server, every minute (`PT1M`), severity 2, one static criterion with no
    dimension, enabled and stateful: `cpu_percent` Average over 30 minutes (`PT30M`) `GreaterThanOrEqual` 70,
    `memory_percent` Maximum over 5 minutes `GreaterThanOrEqual` 80, `storage_percent` Maximum over 5 minutes
    `GreaterThanOrEqual` 70 and `cpu_credits_remaining` Minimum over 5 minutes `LessThanOrEqual` 72;
  - the only action is the pilot action group, with no webhook, and the observability template contains no e-mail
    address;
  - the server SKU stays `Standard_B1ms`, whose 35 user connections and 288 CPU credits the thresholds belong to, and
    its template disk stays 32 GiB (`storageSizeGB`), the base of the storage percentage;
  - each rule counts as one time series at 0.10 USD in the 5 USD alert bound.
- **Application Insights (P21, OBS-003):**
  - exactly one `Microsoft.Insights/components`, in `platform.bicep`, in the pilot region, workspace-based on the
    pilot workspace (`IngestionMode: LogAnalytics`), IP masking kept, no ingestion sampling or own retention;
  - the API and the Worker get `TelemetryExport__Enabled` as a literal `true`/`false`, the connection string only from
    that component (by its managed name, with the same suffix as `platform.bicep`) and
    `TelemetryPseudonym__CurrentKeyVersion='1'`, and no other telemetry or `OTEL_` setting; no other
    workload gets telemetry settings;
  - alert and workbook queries read no Application Insights table and no pseudonym or role kind;
  - the workflow stops in window 2 while `paquetenvia-telemetry-pseudonym-key-1` is missing.
- **Accepted terms and privacy versions (P22, UI-001):** the web receives `PAQUETERIA_TERMS_VERSION` and
  `PAQUETERIA_PRIVACY_VERSION` from `apps.bicep` parameters without a default (1 to 64 characters), no
  other workload receives them, `web.parameters.json` is well formed, and the workflow checks and deploys it.
- **API realtime dispatcher credential (P23, RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12):** with the PostgreSQL
  dispatcher on, the API maps `ConnectionStrings:PaqueteriaRealtime` from `pg-realtime-runtime-connection`; it never
  maps `ConnectionStrings:PaqueteriaWorker` or a Worker secret (the shared OBS-003 pseudonym key is not a Worker
  credential and is exempt); no other workload maps the realtime secret; and the workflow generates exactly the
  `api`, `worker` and `realtime` runtime logins.
- **Non-root images (P24, AZR-001-AC-064):** AZR-001 v0.8 says "Non-root cuando soporte runtime. Root requiere
  waiver", and no waiver exists. The workflow builds exactly the four images `deploy/azure/Dockerfile.api`,
  `deploy/azure/Dockerfile.worker`, `deploy/azure/pilot/Dockerfile.db-ops` and `deploy/azure/pilot/Dockerfile.web`,
  and the last `USER` of each final stage is non-root: `$APP_UID` (uid 1654, the `app` user) on an official
  `mcr.microsoft.com/dotnet/` base whose `APP_UID` no `ARG` or `ENV` redefines, or a literal user other than
  `root`/`0` (`node` for the web). A missing `USER`, a later `USER root` or an unresolvable variable fails.
  This workflow does not build the AZR-001 DEV-SYNTHETIC images `deploy/azure/Dockerfile.web` (`USER node`) and
  `deploy/azure/Dockerfile.db-ops` (`USER $APP_UID`), so P24 does not read them. They also run as non-root.
  `DevSyntheticNonRootImageTests` in `tools/azr-001/test_azr001_static_guards.py` applies the same check to every
  `deploy/azure/Dockerfile*`.
- **PostgreSQL server setting (P25, OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20):**
  - `platform.bicep` and `observability.bicep` take `postgresServerName` (3 to 63 characters) whose default is exactly
    the first server's name; `platform.bicep` declares the only server with that name and outputs `postgresName` and
    `postgresHost` from it; no template spells the name anywhere else;
  - the deploy job reads `PILOT_POSTGRES_SERVER_NAME` once, validates it (`postgres-server-name`) before
    `azure/login`, and before stage 2 refuses a named server that cannot be read or adopted (`az resource show` with
    the template's API version, then `postgres-adoption-check`); it passes the value to `platform.bicep` and the
    `postgresName` output to `observability.bicep`, and checks that `postgresHost` belongs to `postgresName`;
  - the connections step keeps a runtime credential only while its host (`connection-host`) is the `postgresHost`
    output and reports `runtime_logins_changed`; one step then starts a stopped API or Worker revision and restarts a
    running one when that output is `true`;
  - `restore-drill.sh` restores the `postgresName` output and checks it with the same name pattern.

  The unit tests also run the connections step and the start-or-restart step with a stand-in `az`, and scan
  `deploy/`, `.github/`, `tools/` and the operations and development docs: the name prefix appears only in the two
  defaults, the guards' prefix constant and the drill script's check, and every docs line that names the server also
  names `PILOT_POSTGRES_SERVER_NAME`.

The unit tests compile the real templates whenever the pinned Bicep CLI is present (the azr-static CI
job installs it). They mutate the ARM output to prove that each guard fails closed.

The AZR-001 guards only glob `deploy/azure/*` and never read `deploy/azure/pilot/`; they still pass
31/31.

## 9. Verified here vs. pending

**Verified without Azure:**

- `bicep build` and `bicep lint` pass with the pinned v0.47.16, with no warnings.
- 26/26 pilot guards (P00–P25) and 31/31 AZR-001 guards pass, along with the guard unit tests and the CI
  tooling tests. The gitleaks scan was not re-run for OBS-002.
- The SCL-007 PostgreSQL connection and resource alerts build and lint with no diagnostic, and P21 pins them.
- The OBS-002 alert and workbook queries parse and type-check offline (Kusto language service).
- The migrator changes pass contract tests on real PostgreSQL 18/PostGIS 3.6.
- The BFF web build and `next start` with the CSP connect source work.
- The API and Worker images run as the non-root `app` user (uid 1654). With the final stage of each Dockerfile
  copied verbatim over a local `dotnet publish`, both listen on `0.0.0.0:8080` and answer `/health/live` with 200, with
  no permission error (Development configuration, so the PostgreSQL key ring and Key Vault were not exercised).
- Both db-ops images run the migrator. Before 2026-10-20 they did not copy the three lane sources under
  `src/BuildingBlocks/Paqueteria.Infrastructure` (the SCL-001 key-ring lane and the two PlatformEvolution
  migrations). Every migrator command therefore exited 3 at start ("DataProtection adoption migration is missing"),
  as uid 1654 and as root. Now both images, built locally with their final stage verbatim (only the sandbox proxy CA
  was added to the build stage), pass `verify` with all 13 lanes `VERIFIED`. The pilot image also runs `apply` and then `assert` as uid 1654 against the pinned PostGIS 18/3.6
  image. That run used a local `postgres` superuser, so it did not use `--azure-ownership-bridge`.
  `DbOpsImageArchitectureTests` fails if a file the migrator reads is not copied into every
  `deploy/**/Dockerfile.db-ops*`.

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
- SCL-007 PostgreSQL connection alerts:
  - ARM acceptance of the two metric alerts on the private server;
  - whether `active_connections` counts Azure's own `azure_superuser` connections (§10.2).
- SCL-007 PostgreSQL resource alerts (§10.3, first-deploy checks):
  - ARM acceptance of the four metric alerts, `cpu_credits_remaining` with its `LogicalServerName` dimension included;
  - the B1ms CPU credit numbers (288 at most, 12 per hour) on the real server: they come from the Bv1-series VM page,
    and the PostgreSQL pages do not restate them;
  - the disk is still 32 GiB, the base of the storage percentages; after an autogrow the same 70 % is of the larger
    disk.
- TEN-003-PLATFORM-BOOTSTRAP-2026-10-19: `az containerapp job start --yaml` with the migrate job's own template and
  new arguments (§6.1 step 10). The command itself passes contract tests on real PostgreSQL as a NOSUPERUSER
  BYPASSRLS deployment login without `paqueteria_app`; the `az` override was checked only against the Azure CLI
  source.
- Proof photo soft delete (ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20): ARM acceptance of the 30-day blob and
  container retention and the restore steps of §6.4 were not run against real Azure.
- OPS-002-RESTORE-FOLLOWS-SERVER-2026-10-20 (§6.4 real restore), checked against Microsoft Learn, the ARM reference
  and the Azure CLI source, never against Azure:
  - that ARM updates a point-in-time restored server in place when `platform.bicep` names it (every property the
    template sets was compared with what an existing restored server accepts; `postgres-adoption-check` refuses the
    cases that differ);
  - the ARM GET shape `az resource show --api-version 2024-08-01` returns (location as a display name, `state`,
    `storage.storageSizeGB`);
  - moving the six SCL-007 metric alerts to another server by redeploying their scope (the ARM reference forbids a
    scope change only for metric rules based on logs);
  - `az containerapp revision set-mode`, `deactivate` and `activate` on the API and Worker, and `apps.bicep` putting
    them back in single revision mode.
- OBS-003:
  - ARM acceptance of the workspace-based component;
  - that the exporter reaches the Mexico Central ingestion endpoint from inside the VNet, and the real volume
    against the 0.3 GB/day cap once the export is on.

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

### 10.1 Application Insights (OBS-003)

Details, allowlist, pseudonym and tests: `docs/development/obs-003-appinsights-export.md`.

- **Component:** `appi-pv-pilot-<suffix>` in `platform.bicep`, workspace-based on the pilot workspace, so its data
  counts against the same 0.3 GB/day cap and 30-day retention. Ingestion uses the instrumentation key in the
  connection string (Q-PR6-2, "Llave en la conexión (Recommended)"); it lives only in the API and Worker settings.
  Whoever obtains it can send fake telemetry, not read it, and fake telemetry counts against the daily cap.
- **Off until a reviewed PR:** `apps.bicep` passes `TelemetryExport__Enabled='false'`. Turning it on is a PR that
  changes that one literal to `'true'`; guard P21 refuses a parameter or any other value.
- **When:** after one week of pilot (OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14, owner: "Tras una semana de piloto
  (Recommended)"). Deploy without exporting, measure for one week how much of the 0.3 GB daily cap the logs use
  (`_LogOperation` and the workspace usage), and then open the PR with that number.
- **What goes out once on:** traces and metrics only (no logs; the logs stay in Log Analytics as in OBS-002).
  - 10 % of traces by trace id: API requests (not `/health/*`) and the ADR-034 scheduled job cycles (Q-PR6-1,
    "Solo tareas programadas (Recommended)"), with their outgoing HTTP calls. Never the outbox dispatch loops,
    the proof-validation poll or database spans.
  - Span attributes are allowlisted (method, route template, status, peer host and port, error type, and on
    requests with an organization the pseudonym and role kind).
  - Metrics: the application meters and the ASP.NET Core meters, with only the OBS-002 metric tag allowlist.
- **Organization pseudonym:** `v1-<32 hex>`, HMAC of the organization id under `paquetenvia-telemetry-pseudonym-key-1`.
  It is on the API's log lines (`State.OrganizationPseudonym`, `State.ActorRoleKind`) whenever a request selected an
  organization, also with the export off, and on the Worker's lines of each outbox message and of each proof photo with
  valid signed metadata, with the role kind `SYSTEM` (OBS-003-WORKER-PSEUDONYM-2026-10-14). It is never an alert,
  workbook or metric dimension.
- **Alerts:** none added.

### 10.2 PostgreSQL connections (SCL-007)

Decision SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14 (owner: "Sí, 80 % y 95 % (Recommended)" and "Máximo en 5 min,
cada minuto (Recommended)"). Pools and budget: `docs/development/scl-007-pool-per-role.md`.

- **Rules:** two metric alerts in `observability.bicep`, deployed by stage 5 with the OBS-002 alerts.

  | Rule | Fires when | Severity |
  |---|---|---|
  | `mar-pv-pilot-pg-connections-warning` | `active_connections` ≥ 28 (80 % of the 35 user connections) | 2 |
  | `mar-pv-pilot-pg-connections-critical` | `active_connections` ≥ 33 (95 %) | 1 |

  Both take the Maximum of the one-minute samples of the last 5 minutes and are evaluated every minute, so a single
  sample at the threshold sends the e-mail about 1–3 minutes later. They are stateful: one e-mail when an alert fires
  and one when it resolves, to the same action group and address as OBS-002. The thresholds are 80 % and 95 % of the
  35 user connections of the B1ms server, as AI-15 `connection_pool_utilization` asks (0.8 and 0.95). They are literal
  in the template and guard P21 pins them, so `observability.parameters.json` cannot change them.
- **Metric:** `active_connections` of `Microsoft.DBforPostgreSQL/flexibleServers`, a count sampled every minute
  ([supported metrics](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-dbforpostgresql-flexibleservers-metrics)).
  Microsoft defines it as every connection in `pg_stat_activity`, whatever its state
  ([monitoring](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/concepts-monitoring)).
- **What 28 means:** the runtime pools hold 24 at most (API `paqueteria_app` 10 and `paqueteria_realtime` 4, Worker
  `paqueteria_worker` 10). The other 11 are for the migrate, verify and logins jobs (`Maximum Pool Size=4`), the
  restore drill (2) and administration. The count reaches 28 only when the pools are full and other sessions are open
  at the same time, or when something holds more than its budget.
- **Cost:** 2 time series, inside the 10 free per resource, otherwise about 0.10 USD per series per month, whatever
  the frequency ([Azure Monitor pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/)).
- **When it fires:**
  1. Look at the shape in Azure Monitor: the server → *Metrics* → *Active Connections* with aggregation *Max*, and
     *Failed Connections* (`connections_failed`, which counts the connections PostgreSQL refused).
  2. Check whether a revision change, a run of the pilot workflow, a `job-pv-pilot-*` execution or the restore drill
     was running then (`az containerapp revision list`, `az containerapp job execution list`). A revision change can
     hold two sets of pools for a moment; the alert then resolves by itself and sends the resolve e-mail.
  3. Check for a manual pool override on both apps:
     `az containerapp show -n <app> -g <rg> --query "properties.template.containers[0].env[?starts_with(name, 'DatabasePools__')]"`
     for `ca-pv-pilot-api` and `ca-pv-pilot-worker`. The Bicep sets none, so any value there was set by hand. All three
     variables must be identical on both apps and sum to 24 at most (§6.7); otherwise re-run the workflow for the
     current SHA, which redeploys the apps without them.
  4. Close administration or diagnostic sessions that are no longer needed.
  5. If none of that explains it, the runtime is using its whole budget under load. Lowering the pools or moving to
     B2s (+≈41 USD/month, over the budget) needs an owner decision.

  At the critical threshold only 2 user connections are left. PostgreSQL then refuses new connections with
  `too many connections`, which shows in the API and Worker logs and in *Failed Connections*.
- **Residual risks:**
  - Microsoft does not document whether `active_connections` includes Azure's own `azure_superuser` connections. If it
    does, the alerts fire with fewer than 28 or 33 user connections, which is the safe direction. Check at the first
    deployment: while the apps are idle, compare *Active Connections* (Max) with
    `SELECT usename, count(*) FROM pg_stat_activity GROUP BY usename` run from an administration session, and record
    the difference.
  - The metric is sampled once a minute. A burst between two samples can be missed, and *Failed Connections* is
    where it would show.

### 10.3 PostgreSQL resources (SCL-007)

Decision SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20 (owner: "CPU, memoria y disco (Recommended)", "Un nivel, de aviso
(Recommended)", "CPU 70, memoria 80, disco 70 (Recommended)", "Sí, agregarla (Recommended)" and "Menos de 72 (25 %)
(Recommended)").

- **Rules:** four metric alerts in `observability.bicep`, deployed by stage 5 with the connection alerts (§10.2). Each
  has one level, the warning (severity 2), is evaluated every minute and is stateful: one e-mail when it fires and one
  when it resolves, to the same action group and address as OBS-002.

  | Rule | Fires when | Over |
  |---|---|---|
  | `mar-pv-pilot-pg-cpu-warning` | `cpu_percent` Average ≥ 70 % | 30 minutes |
  | `mar-pv-pilot-pg-memory-warning` | `memory_percent` Maximum ≥ 80 % | 5 minutes |
  | `mar-pv-pilot-pg-storage-warning` | `storage_percent` Maximum ≥ 70 % (22.4 of the 32 GiB) | 5 minutes |
  | `mar-pv-pilot-pg-cpu-credits-warning` | `cpu_credits_remaining` Minimum ≤ 72 (25 % of the 288 a B1ms banks) | 5 minutes |

  CPU takes AI-15 `db_cpu_utilization` (warning 0.7, window 30m, `docs/normative/v0.6/specs/AI-15_SCALABILITY_CONTRACT.yaml`
  lines 113-116), so a short peak does not fire it. The answer gives no window for the disk; it takes the Maximum over
  5 minutes every minute, as the connection alerts. The credits option was labeled "Menos de 72" and its text reads
  "Avisa con 72 créditos o menos", so the rule fires at 72 or fewer. The thresholds are literal in the template and guard
  P21 pins them, so `observability.parameters.json` cannot change them.
- **Metrics** of `Microsoft.DBforPostgreSQL/flexibleServers`, each sampled every minute with Average, Maximum and Minimum
  ([supported metrics](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-dbforpostgresql-flexibleservers-metrics)):
  `cpu_percent`, `memory_percent` and `storage_percent` are percentages (dimension `ServerName`); `cpu_credits_remaining`
  is a count, "Total number of credits available to burst" (dimension `LogicalServerName`). No rule filters a dimension:
  one server, one time series per rule.
- **Cost:** with the two connection alerts, 6 time series, inside the 10 free per resource, otherwise about 0.10 USD per
  series per month (§5).
- **What each one means and what to do first:**
  - **CPU** (`mar-pv-pilot-pg-cpu-warning`): the server averaged 70 % CPU or more for half an hour. On a Burstable B1ms
    that also spends CPU credits. First, find the load: the server → *Metrics* → *CPU percent* (Avg) next to *CPU
    Credits Remaining* (Min) and *Active Connections* (Max); whether a pilot workflow run, a `job-pv-pilot-*` execution
    or the restore drill was running; and the slow or long sessions, from an administration session:
    `SELECT usename, state, wait_event_type, now() - query_start AS running_for FROM pg_stat_activity WHERE state <> 'idle' ORDER BY running_for DESC NULLS LAST`.
    The workbook (§10) shows whether API traffic or the outbox lanes grew at the same time. If it is the pilot's real
    load, a larger size needs an owner decision (B2s +≈41 USD/month, over the budget).
  - **CPU credits** (`mar-pv-pilot-pg-cpu-credits-warning`): 72 credits or fewer are left. A B1ms banks at most 288 and
    up to 12 per hour: it gains credits while its CPU stays below the 20 % baseline and spends them above it. At full
    CPU it loses about 0.8 per minute, so 72 leave about an hour and a half. With none left the server is held at 20 %
    CPU, and Microsoft warns of "severe performance degradation, connection timeouts, and delays or transient failures
    in management operations until credits rebuild"
    ([compute](https://learn.microsoft.com/en-us/azure/postgresql/compute-storage/concepts-compute)). First, the same
    load check as for CPU. Microsoft advises not to restart or scale the server while credits are near zero; if they
    run low often, a larger Burstable size or another tier needs an owner decision. Expect this alert right after the
    first deploy: the Bv1-series page gives a B1ms 30 initial credits, under the 72 threshold, so it can fire at once and
    resolves by itself once the server banks more than 72 (about 3.5 hours at 12 per hour under light load). It may
    do the same after the server is stopped and started; note what happens at the first deploy (§9).
  - **Memory** (`mar-pv-pilot-pg-memory-warning`): some minute of the last 5 reached 80 % of the 2 GiB. First, *Memory
    percent* with Max and Avg, to tell a peak from a steady level, next to *Active Connections* (each connection takes
    memory) and *CPU percent*, and the long sessions as for CPU. If it stays high under normal load, a larger size
    needs an owner decision.
  - **Storage** (`mar-pv-pilot-pg-storage-warning`): 70 % of the disk is used, 22.4 of the 32 GiB. Autogrow is on: when
    less than 20 % is free (80 % used) Azure doubles the disk to 64 GiB, and "that doubling also doubles the storage
    cost" (about 4.05 to 8.10 USD per month, §5); storage only scales up, never down. The server turns read-only "when
    the storage usage reaches 95 percent, or when the available capacity is less than 5 GiB", about 84 % of 32 GiB, and
    autogrow does not trigger when the space goes to write-ahead log
    ([storage](https://learn.microsoft.com/en-us/azure/postgresql/compute-storage/concepts-storage),
    [autogrow](https://learn.microsoft.com/en-us/azure/postgresql/scale/how-to-auto-grow-storage)). First, see what
    grows: *Storage percent*, *Storage used* and *Transaction Log Storage Used* over the last days, and the database size
    from an administration session (`SELECT pg_size_pretty(pg_database_size('paqueteria'))`); then check that the
    OPS-004 retention runs (the workbook and `sqr-pv-pilot-job-failure`). Autogrow then happens on its own at 80 %;
    whether to clean up first or accept the larger disk, which doubles its cost for good, is an owner decision.
- **First-deploy checks:**
  - The credit numbers (288 at most, 12 per hour, 20 % baseline) are those of the Bv1-series VM page
    ([Bv1-series](https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/general-purpose/bv1-series)); the
    PostgreSQL pages do not restate them. On the real server, read *CPU Credits Remaining* (Max) after a quiet day, when
    it should approach 288, and record the value. If it is different, 72 is no longer 25 % and the threshold needs a new
    owner decision.
  - The storage percentages assume the 32 GiB disk of `platform.bicep`; P21 refuses another `storageSizeGB` in the
    template. Check the real size (`az postgres flexible-server show -n <server> -g <rg> --query storage`, where
    `<server>` is `PILOT_POSTGRES_SERVER_NAME` or, while it is empty, `pg-pv-pilot-<suffix>`).
    After an autogrow the disk is 64 GiB, so 70 % means 44.8 GiB; the threshold then needs a new owner decision.
