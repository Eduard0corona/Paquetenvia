# ARC-002 validation report

## Result

| Field | Verified value |
|---|---|
| Date | 2026-07-21 |
| Validated commit | `6bab4077bef5712f967c38c4695ddf07943486c6` |
| Status | Partial - normative purge contradiction remains |
| Image | `postgis/postgis:17-3.5` |
| Digest | `sha256:404171ea9058c801f405af25d63b3b8e5c9e50f2759e49390dbcc3c7ee533f4d` |
| PostgreSQL | `17.5 (Debian 17.5-1.pgdg110+1)` |
| PostGIS | `3.5 USE_GEOS=1 USE_PROJ=1 USE_STATS=1` |
| AI-06 then AI-18 bootstrap | 234 ms locally |
| ContractTests | 30 passed, 0 failed, 0 skipped |
| Static ContractTests | 11 passed |
| PostgreSQL ContractTests | 19 passed |
| Complete .NET test run | 51 passed, 0 failed, 0 skipped |
| PostgreSQL invocation | approximately 6.5 seconds locally |
| Frontend | lint, typecheck, 1 test and production build passed |
| Local infrastructure | Windows PowerShell smoke passed in 66.5 seconds |
| Package audit | .NET clean; pnpm reports one high `sharp` advisory |
| GitHub Actions | All five jobs passed in run `29877096491` |

## Gate evidence

| Contract area | Result | Evidence |
|---|---|---|
| Frozen baseline | Pass | Canonical validator returned `VALIDATION_OK`; normative diff is empty |
| AI-06 identity | Pass | SHA-256 `4b5fe5397ff088b63e0c288770903512665c5fe8a8dc7401d7e4d3af64643505` |
| AI-18 identity | Pass | SHA-256 `7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd`, equal to `CHECKSUMS_SHA256.txt` |
| Empty database bootstrap | Pass | AI-06 and AI-18 executed directly and unmodified in that order |
| Extensions/schemas | Pass | Live `pg_extension`, `pg_namespace`, digest execution and privilege checks |
| Roles/ownership | Pass | Live roles, memberships, owners, table/column/function privileges and negative `SET ROLE` |
| RLS/tenant context | Pass | Live non-superuser logins, forced policies, A/B/multiple/empty/absent contexts and pooled transactions |
| Provisioning | Pass | Active memberships, fail-closed lookup, preauthorized creation, rollback and no partial rows |
| Outbox direct access | Pass | Both lanes deny select/update/delete and `RETURNING`; producer inserts work |
| Claim/settle/requeue | Pass | Both lanes, lease negatives, retry/dead transitions, stale recovery and two-worker concurrency |
| Purge dry-run/cutoffs | Pass | Both lanes report terminal candidates and reject unsafe cutoffs |
| Purge deletion | **Blocked** | Both functions return PostgreSQL `42501`; AI-06 `FOR UPDATE` conflicts with AI-18 `SELECT,DELETE` grant |
| Tracking crypto/projection | Pass | C#/PostgreSQL vector equality and indistinguishable null projections for invalid tokens |
| Acceptance | Pass | AI-24 bytes/hashes, stored bytea, RLS and append-only negatives |
| Quote/order snapshot | Pass | All 17 copied comparisons, unique consumption, atomic commit and clean rollback |
| Money/external offers | Pass | bigint catalog, >int32 value, total constraint and external-offer acceptance constraint |
| OpenAPI/YAML/SignalR | Pass | Parsed contracts, counts, references, status mapping and fail-closed schema checks |
| Manifest | Pass | Exact set of 72 files, byte lengths and SHA-256 values |

Negative coverage includes mutated and padded tracking tokens, noncanonical
acceptance bytes, empty/null/malformed tenant context, cross-tenant writes,
privileged role assumption, runtime `CREATE`, direct outbox access,
`INSERT ... RETURNING`, wrong and expired leases, repeated settle, active-state
purge preservation, expired/revoked tracking, private timeline events,
append-only update/delete, second quote consumption, inconsistent totals,
invalid external-offer acceptance, and an unmapped public status.

## Differences and contradictions

1. AI-06 real purge uses `SELECT ... FOR UPDATE SKIP LOCKED`; AI-18 grants the
   owning maintenance role `SELECT, DELETE` but not `UPDATE`. PostgreSQL 17
   therefore rejects real purge with `42501`. No normative file was changed and
   no compensating grant was introduced.
2. AI-06's header comment identifies a PostgreSQL 18 baseline. The exact image
   mandated by FND-002 and this task executes PostgreSQL 17.5.

No inconsistent status was found between AI-04, OpenAPI, SignalR and live SQL.
No manifest, hash, OpenAPI reference, RLS catalog or cryptographic-vector drift
was found.

## CI and pending work

The workflow keeps the four existing jobs and adds `Validate runtime contracts`.
The general .NET job runs `Category!=PostgreSqlContract`; the runtime job runs
`Category=PostgreSqlContract` on `ubuntu-latest`, with locked restore, Release
build, a 20-minute timeout, Testcontainers diagnostics, failed `.trx` upload,
and unconditional cleanup. GitHub Actions run `29877096491` passed all five
jobs: normative baseline, .NET, web, local infrastructure, and runtime contracts.

The only remaining ARC-002 gate is successful real deletion through both purge
functions after an authorized normative correction. SEC-001, SEC-002, TEN-001,
DBA-001, migrations, persistent API/Worker integration and product behavior are
not ARC-002 work and remain pending.

The package review also found GitHub advisory `GHSA-f88m-g3jw-g9cj`, published
on the validation date, against `sharp 0.34.5` inherited from Next.js. The
patched line starts at 0.35.0, while current Next.js 16.2.11 still declares
`sharp ^0.34.5`. ARC-002 does not force an unsupported transitive override; this
is recorded for separate frontend dependency maintenance.

## Safety confirmation

- `docs/normative/v0.6/` was not modified.
- AI-06 and AI-18 ran only in the disposable Testcontainers database.
- No migration was created and the FND-002 persistent database was untouched.
- API and Worker were not connected to PostgreSQL.
- No business logic, real PII, production provider, secret or deployment was
  introduced.
