# ENV-001 pilot environment on Azure

ENV-001 (`docs/normative/v0.6/specs/AI-08_BACKLOG.yaml`) is the pilot **with real people**
(`PILOT-REAL-PEOPLE`). It is separate from the AZR-001 DEV-SYNTHETIC environment. AZR-001 and
AZR-001-DEC-001 (synthetic data only, public PostgreSQL) do not apply here, and none of the AZR-001
files (`deploy/azure/*.bicep`, `deploy-core.ps1`, `deploy-azure-dev.yml`) or guards were changed.

| Piece | Location |
|---|---|
| Templates | `deploy/azure/pilot/{security,platform,jobs,apps}.bicep` |
| Pilot-only images | `deploy/azure/pilot/Dockerfile.db-ops` (migrator only), `deploy/azure/pilot/Dockerfile.web` (AUTH-001 BFF build) |
| API and Worker images | `deploy/azure/Dockerfile.api`, `deploy/azure/Dockerfile.worker` (shared, unchanged) |
| Business settings reviewed by the owner | `deploy/azure/pilot/apps.settings.json` |
| Deployment workflow | `.github/workflows/deploy-azure-pilot.yml` (`workflow_dispatch`, GitHub Environment `azure-pilot`) |
| Static guards | `tools/azr-001/env001_pilot_guards.py` (21 guards) and `test_env001_pilot_guards.py` |
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
| API (`id-pv-pilot-api`) | `pg-api-runtime-connection` → `ConnectionStrings:Paqueteria`; `authcenter-paquetenvia-client-secret` → `AuthCenter:ClientSecret`; `paquetenvia-email-lookup-key-1` → `EmailLookup:Keys:1` |
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
| `pg-restore-drill-connection` | `restore-drill.sh` | verify job, during a drill only |

**Reserved names (not created, for later work):**

| Secret | For |
|---|---|
| `google-maps-api-key` | GATE-003-PROVIDER-GOOGLE (Locations geocoding/routing adapter) |
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
  - Every module provider set to `PostgreSql`.
  - `Realtime__Provider=SignalR`, `Backplane=InProcess`, allowed origins.
  - ADP-001: `AZURE_CLIENT_ID`, `Locations__PiiProtector` and `Incidents__PiiProtector` set to
    `AzureKeyVault`, `PiiProtection__AzureKeyVault__KeyId`, `ProofStorage__Provider=AzureBlob`,
    `ProofStorage__ThreatScanner=DefenderForStorage`, `ProofStorage__AzureBlob__ServiceUri`,
    `DataProtection__Provider=PostgreSql` plus the `DataProtection__KeyEncryption__*` settings.
  - `Locations__GeocodingProvider=Manual` until the GATE-003 Google adapter exists.
- **Worker:** `ConnectionStrings__Paqueteria` and `ConnectionStrings__PaqueteriaWorker`, the same
  ADP-001 proof-storage and Data Protection settings, `Dispatch__AssignmentLifecycle`,
  `Notifications`, `Orders__ClaimWindowFinalization__Enabled=true`, and `Urls=http://+:8080` for the
  probes.
- **Web:** `NODE_ENV=production` and `PAQUETERIA_CSP_CONNECT_SOURCES=<blob endpoint>`, so drivers can
  PUT proofs directly. The image is built with `NEXT_PUBLIC_AUTH_MODE=bff` and **without**
  `NEXT_PUBLIC_API_BASE_URL`.

> The shared `deploy/azure/Dockerfile.web` cannot produce a BFF build. It always defines
> `NEXT_PUBLIC_API_BASE_URL`, as `""` when the argument is omitted, and `next.config.ts` then fails
> with `TypeError: Invalid URL`. This was reproduced with `pnpm build`. The pilot therefore uses
> `deploy/azure/pilot/Dockerfile.web`.

`apps.settings.json` holds the business and policy values the owner must choose. It is appended to the
API and Worker settings. It cannot override any platform-managed prefix, and it cannot carry a
mock/synthetic value or a secret-looking name (guard P17). The workflow refuses to deploy while any
value is still `OWNER_DECISION_REQUIRED`:

- `Pricing__PricingPolicyVersion`
- `Dispatch__AssignmentPolicyVersion`
- `Drivers__Eligibility__PolicyVersion`

Driver eligibility maps (`Drivers__Eligibility__RequiredDocumentTypesByVehicleType__<TYPE>__0`,
`Drivers__Eligibility__VehicleCapacity__<TYPE>__MaximumPackageCount`, …) go in the same file.

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
| **Total** | | **≈ 98 typical, ≈ 108 if the log cap is hit every day** |

**This design sits at the budget line and does not fit ~100 USD with margin.** The owner accepted this
sizing (PILOT-BUDGET-ACCEPT-98-108, 2026-09-28) without applying any lever. The largest item is the
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
3. The owner replaces every `OWNER_DECISION_REQUIRED` in `deploy/azure/pilot/apps.settings.json`
   (PR → `development` → `main`).
4. Promote to `main` and wait for the Foundation CI push run on `main` (13/13 green). Note its run id
   and head SHA.
5. Actions → *Deploy Azure PILOT* → Run workflow on `main`: `tested_git_sha=<head SHA>`,
   `foundation_run_id=<run id>`, `custom_domain_phase=none`.
6. The first run creates the vault, then stops with *"lacks authcenter-paquetenvia-client-secret"*.
   Write the AuthCenter secret straight into the vault (§6.6) and run again.
7. The run summary shows the DNS records. At the DNS host of `paquetenvia.com`, create:
   - `A` `@` → the environment static IP;
   - `TXT` `asuid` → the custom-domain verification id.

   Remove any other `A`/`AAAA`/`CNAME` on the apex. Wait for propagation
   (`dig +short paquetenvia.com`, `dig +short TXT asuid.paquetenvia.com`).
8. Run again with `custom_domain_phase=bind`. The route binds the apex and the managed certificate is
   issued by HTTP validation. Check with
   `az containerapp env certificate list -g <rg> -n <env> --managed-certificates-only -o table`
   (status `Succeeded`), then `curl -I https://paquetenvia.com/`.
9. Check by hand, in a browser on `https://paquetenvia.com`:
   - Log in through AuthCenter.
   - In DevTools → Network → WS, `/hubs/...` shows `101 Switching Protocols`.
   - Upload a proof photo, then check that the blob has the Defender scan-result index tag.
   - `http://paquetenvia.com` redirects to HTTPS or is refused. HTTP-to-HTTPS on a route custom domain
     is not verified yet.
10. Register the production AuthCenter client URIs (auth-001 §10.1, all on `https://paquetenvia.com`).
11. Run the restore drill (§6.4) and keep its `EVIDENCE` line. The ENV-001 criterion "restore drill
    evidence is current before pilot start" requires it.

### 6.2 Redeploy

Promote, wait for 13/13 on `main`, then dispatch with the new SHA and run id. Keep
`custom_domain_phase=bind` once the domain is bound: `none` would unbind the apex. On every run the
workflow:

- redeploys the four templates idempotently;
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
bash deploy/azure/pilot/kv-firewall.sh close <kv>
```

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
- **PostgreSQL connections:** B1ms allows about 50. API and Worker pools are capped at 5 per data
  source (`Maximum Pool Size=5;Minimum Pool Size=0`). `too many connections` means lowering those
  or moving to B2s (+≈41 USD/month, over budget).
- **TLS:** connection strings use `SSL Mode=VerifyFull`. If the image's CA bundle ever lacks the
  PostgreSQL certificate chain, the migrate job fails at connect. Fix the CA bundle; do not downgrade
  to `Require` without an owner decision.

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

The 21 guards (P00–P20) run on the compiled ARM output and the workflow:

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

The unit tests compile the real templates whenever the pinned Bicep CLI is present (the azr-static CI
job installs it). They mutate the ARM output to prove that each guard fails closed.

The AZR-001 guards only glob `deploy/azure/*` and never read `deploy/azure/pilot/`; they still pass
31/31.

## 9. Verified here vs. pending

**Verified without Azure:**

- `bicep build` and `bicep lint` pass with the pinned v0.47.16, with no warnings.
- 21/21 pilot guards (P00–P20) and 31/31 AZR-001 guards pass, along with the guard unit tests, the CI tooling
  tests and the gitleaks scan.
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
- The ADP-001 adapters themselves, which are not merged yet. Until they are, the API refuses to start
  with `AzureKeyVault`, `AzureBlob` or `DefenderForStorage`, by design.
