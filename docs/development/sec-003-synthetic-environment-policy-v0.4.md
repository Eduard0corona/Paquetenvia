# SEC-003 Synthetic Environment Policy v0.4

Contract status: `APPROVED_FROZEN`. Implementation base:
`03b0928cd981021a9dbd905ef0c9236a2b58d26f`. The active post-v0.6 backlog
registration is Issue #55; SEC-003 is intentionally absent from the frozen AI-08
baseline.

## Identity and authority

Backend DevSynthetic deployment uses the exact process identity:

```text
DOTNET_ENVIRONMENT = DevSynthetic
ASPNETCORE_ENVIRONMENT = DevSynthetic
PAQUETERIA_DEPLOYMENT_CLASS = DEV_SYNTHETIC
```

The framework host must therefore report `EnvironmentName = DevSynthetic`.

Deployment class authority is read by `SyntheticEnvironmentPolicy` directly
through `Environment.GetEnvironmentVariable`. `IConfiguration`, appsettings,
command-line providers, HTTP input and persisted configuration cannot supply or
override it. `IsDevelopment()` and Testing keep their framework meanings;
DevSynthetic is neither one.

The web `/dev` policy independently reads server-side
`process.env.PAQUETERIA_DEPLOYMENT_CLASS`. No `NEXT_PUBLIC_*` value or request or
browser input is authoritative.

## Repository-wide guard inventory

The inventory covered `IsDevelopment()`, exact Testing checks, .NET and Node
environment variables, dev-portal flags, deployment class, Mock/Synthetic
providers, DevSeed opt-ins, proof storage/scanner readiness, identity bootstrap,
and Development-specific appsettings.

| Classification | Guard or surface | SEC-003 disposition |
| --- | --- | --- |
| `MIGRATED_TO_DEV_SYNTHETIC` | `Identity.Endpoints/DependencyInjection.cs`: `Authentication:Provider=Mock` | Adds only exact authorized DevSynthetic; Development and Testing are unchanged. |
| `MIGRATED_TO_DEV_SYNTHETIC` | `Locations.Infrastructure/DependencyInjection.cs`: `Locations:GeocodingProvider=Mock` | Adds only exact authorized DevSynthetic. |
| `MIGRATED_TO_DEV_SYNTHETIC` | `Locations.Infrastructure/DependencyInjection.cs`: `Locations:PiiProtector=Mock` | `DEV_SYNTHETIC_ONLY` outside local/test use; `NOT_STAGING_OR_PRODUCTION_PATTERN`; no real PII is authorized. |
| `MIGRATED_TO_DEV_SYNTHETIC` | `ProofStorageHealthCheck`: disabled storage readiness | Healthy only for Development, Testing, or exact authorized DevSynthetic. All proof operations remain fail-closed. |
| `MIGRATED_TO_DEV_SYNTHETIC` | `ProofScannerHealthCheck`: disabled scanner with disabled storage readiness | Healthy only for Development, Testing, or exact authorized DevSynthetic. Scanner and storage operations remain disabled. |
| `MIGRATED_TO_DEV_SYNTHETIC` | Web `dev-portal-policy.ts` and `/dev` page | Retains `NODE_ENV=development` plus opt-in; additionally permits production Node builds only with exact server-side class and opt-in. All other cases return 404. |
| `UNCHANGED_DEVELOPMENT_ONLY` | API `Program.cs`: OpenAPI mapping under `IsDevelopment()` | DevSynthetic does not map Development OpenAPI. |
| `UNCHANGED_DEVELOPMENT_ONLY` | Developer Exception Page | Not configured in the API or Worker and not introduced by SEC-003. |
| `UNCHANGED_DEVELOPMENT_ONLY` | `appsettings.Development.json` | Loaded only by normal framework Development configuration; not copied or inherited by DevSynthetic. |
| `UNCHANGED_DEVELOPMENT_ONLY` | DevSeed `bootstrap`, `seed`, `status`, `tracking` with `PAQUETERIA_LOCAL_DEV_SEED_ENABLED=true` | Existing Development behavior is preserved, including the local one-time tracking token output. |
| `UNCHANGED_DEVELOPMENT_ONLY` | `tools/dev-platform.ps1`: .NET hosts and web `NODE_ENV` | The local platform still sets Development explicitly; it is not a DevSynthetic deployment launcher. |
| `UNCHANGED_DEVELOPMENT_ONLY` | Web `next.config.ts`: Development CSP `unsafe-eval` | Remains keyed only to `NODE_ENV=development`; the production DevSynthetic `/dev` allowance does not weaken the production CSP. |
| `UNCHANGED_TESTING_ONLY` | `IdentityTestEndpoints.MapIdentityTestProbes` | Exact `EnvironmentName == Testing`; DevSynthetic remains rejected. |
| `UNCHANGED_TESTING_ONLY` | `PublicTrackingTestEndpoints.MapPublicTrackingTestProbe` | Exact `EnvironmentName == Testing`; DevSynthetic remains rejected. |
| `UNCHANGED_TESTING_ONLY` | `OrganizationTestEndpoints.MapOrganizationTestProbes` | Exact Testing-only surface; DevSynthetic remains rejected. |
| `UNCHANGED_TESTING_ONLY` | `Realtime.Infrastructure/DependencyInjection.cs`: SignalR projection-validation shortcut | Still exact Testing only; DevSynthetic must use the productive PostgreSQL projection configuration. |
| `UNCHANGED_SECURITY_RULE` | `Identity.Infrastructure/DependencyInjection.cs`: `IdentityBootstrap:Provider=Mock` | Still Development or Testing only. |
| `UNCHANGED_SECURITY_RULE` | `Custody.Infrastructure/DependencyInjection.cs`: `ProofStorage:ThreatScanner=Synthetic` | Still Development or Testing only; DevSynthetic cannot enable it. |
| `UNCHANGED_SECURITY_RULE` | `Custody.Infrastructure/DependencyInjection.cs`: insecure HTTP S3-compatible endpoints | Still allowed only in Development or Testing; DevSynthetic requires HTTPS. |
| `UNCHANGED_SECURITY_RULE` | Disabled proof object storage and threat scanner implementations | Every actual proof/POD operation remains unavailable and fail-closed. Only readiness semantics changed. |
| `UNCHANGED_SECURITY_RULE` | Productive public tracking token service | Rotation still owns tenant GUCs, `SET LOCAL ROLE paqueteria_app`, RLS-compatible transaction and canonical audit. |
| `UNCHANGED_SECURITY_RULE` | Productive PostgreSQL public projection reader | Anonymous lookup still owns its transaction and role, without tenant GUCs. |
| `UNCHANGED_SECURITY_RULE` | Web production URL handling in `next.config.ts`, driver operations and public tracking API | Production HTTPS and production-mode behavior remain keyed to `NODE_ENV=production`; deployment class cannot relax them. |
| `UNCHANGED_SECURITY_RULE` | Notifications `SyntheticInAppProvider` and other `Synthetic` fixture/policy labels | These are not environment authority or SEC-003 capabilities and were not changed. |

No sixth backend runtime guard migration was required.

## Configuration inventory

The sole versioned Development-specific runtime file is
`src/Paqueteria.Api/appsettings.Development.json`. It contains:

```text
Realtime:Provider = Disabled
Realtime:Backplane = InProcess
Realtime:AllowedOrigins[0] = https://web.synthetic.local
```

DevSynthetic does not inherit this file. A future deployment may supply the same
differences, if they are actually desired, with the standard .NET process
environment mapping:

```text
Realtime__Provider=Disabled
Realtime__Backplane=InProcess
Realtime__AllowedOrigins__0=https://web.synthetic.local
```

SEC-003 does not define an Azure source for those values and does not add
`appsettings.DevSynthetic.json`. API and Worker continue using their safe
versioned defaults and existing option validation; productive providers still
fail validation when their required connection or provider settings are absent.

## DevSeed

Development plus `PAQUETERIA_LOCAL_DEV_SEED_ENABLED=true` retains `bootstrap`,
`seed`, `status`, and `tracking <order-id>`. Exact authorized DevSynthetic adds
only `seed`, and additionally requires
`PAQUETERIA_SYNTHETIC_SEED_ENABLED=true`. Authorization is checked before opening
a database connection. That path calls only the deterministic prerequisite seed;
it never calls `BootstrapRuntimeRolesAsync`, `EnsureLoginAsync`, or the tracking
helper and never creates LOGIN roles. DevSynthetic `bootstrap`, `tracking`, and
`status` are rejected before side effects.

## TrackingVerify core

`tools/Paqueteria.SyntheticVerification` is a reusable Azure-agnostic .NET
library. It requires the exact process triple:

```text
DOTNET_ENVIRONMENT=DevSynthetic
PAQUETERIA_DEPLOYMENT_CLASS=DEV_SYNTHETIC
PAQUETERIA_SYNTHETIC_TRACKING_ENABLED=true
```

The caller supplies ActorId, OrganizationId, OrderId and a non-secret RunId.
One call generates distinct non-secret request IDs, performs rotation A and B
against the same order through `IPublicTrackingTokenService.RotateAsync`, and
looks up both grants through `IPublicTrackingProjectionReader`. It then verifies
that token A is invalid and token B is still valid.

The verifier contains no SQL, database package, tenant-context implementation,
audit write, privileged credential, Azure dependency, logger or console/file
output. The grant plaintext exists only in local managed-memory references while
the productive reader needs it. The result includes token row identifiers and
outcomes, never plaintext, prefix, suffix, reversible encoding, hash or
fingerprint.

## Governance and CI

The architecture regressions pin three v0.6 governance files byte-for-byte
(`AI-08_BACKLOG.yaml`, `MANIFEST.json`, `CHECKSUMS_SHA256.txt`). At SEC-003
merge they equalled the SEC-003 base; they are not frozen. `MANIFEST.json` and
`CHECKSUMS_SHA256.txt` have since been re-pinned with every authorized
normative change (CSV-001, INC-001, FIN-001, SET-001, LIF-001 and later
governance records), always with values regenerated by
`validate_contracts.py --write-integrity`. The test
`Governance_integrity_files_match_pinned_values` (formerly
`Frozen_governance_files_are_byte_identical_to_SEC003_base`) therefore detects
unreviewed drift, not a frozen baseline. The regressions also assert absence of `appsettings.DevSynthetic.json`, preserve all explicit
non-migrations, inspect the verifier boundary, and assert Foundation remains
exactly 13 jobs. SEC-003 adds no Azure infrastructure, deployment artifact,
functional-scenario extraction, production setting, real-data path or new CI
job.
