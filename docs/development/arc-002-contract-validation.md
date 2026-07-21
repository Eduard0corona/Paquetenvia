# ARC-002 runtime contract validation

## Purpose and status

ARC-002 turns the frozen v0.6 contracts into executable checks without adding
application use cases, migrations, production persistence, API endpoints, or a
functional Worker. The implementation is **partial**: every unaffected static
and runtime scenario passes, but real deletion through both purge functions is
blocked by a contradiction between AI-06 and AI-18 described below.

## Isolated database

The PostgreSQL tests use Testcontainers with this immutable image:

```text
postgis/postgis:17-3.5@sha256:404171ea9058c801f405af25d63b3b8e5c9e50f2759e49390dbcc3c7ee533f4d
```

Each test run creates a fresh database named `paqueteria_contracts`, uses
synthetic random credentials and a dynamic host port, and destroys the
container afterward. It never uses the persistent FND-002 database. The shared
xUnit fixture serializes all PostgreSQL tests; scenarios use unique UUIDs and
remove their synthetic rows.

The fixture reads the frozen files directly, verifies their SHA-256 values, and
executes them unmodified in this order:

1. `docs/normative/v0.6/database/AI-06_SCHEMA.sql`
2. `docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql`

Verified hashes:

```text
AI-06  4b5fe5397ff088b63e0c288770903512665c5fe8a8dc7401d7e4d3af64643505
AI-18  7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd
```

After bootstrap, the fixture creates two `NOINHERIT`, `NOBYPASSRLS`,
non-superuser logins. One can assume only `paqueteria_app`; the other can assume
only `paqueteria_worker`. Npgsql data sources use real pooling, and tenant
context is parameterized as a transaction-local typed `uuid[]` value.

## Executable coverage

- Normative baseline: all 10 YAML documents parse; backlog identifiers,
  dependencies and DAG are checked; all 72 manifest entries, lengths and hashes
  match the files on disk.
- OpenAPI: 3.1.0, contract 0.6.0, 23 paths, 47 schemas, unique operation IDs,
  180 resolved internal references, `integer/int64` cents, and the uniform
  non-enumerating tracking response.
- Bootstrap: 18 application schemas, 36 tables, 35 forced-RLS tables and
  policies, append-only triggers, and all eight lifecycle entrypoints are
  queried from PostgreSQL catalogs.
- Extensions and security: PostGIS remains in `public`, pgcrypto is in
  `extensions`, schema-qualified UTF-8 digest works, and runtime cannot create
  objects in `public`.
- Roles and ownership: NOLOGIN/BYPASSRLS flags, memberships, schema/table/
  sequence/function owners, limited bootstrap column grants, specialized
  executor/maintenance grants, and denied privileged `SET ROLE` operations.
- RLS and tenant context: organizations A, B, A+B, empty and absent context;
  blocked cross-tenant insert/update/delete; commit, rollback and pooled
  connection isolation; typed UUID and null rejection.
- Bootstrap and provisioning: active/default/multiple memberships, suspended
  exclusion, fail-closed identity lookup, preauthorized first-user and
  organization creation, atomic rollback, and no partial rows.
- Outbox: runtime insert-only behavior without `RETURNING`, denied direct
  lifecycle access, business and location claim/settle/requeue behavior,
  leases, retries, dead-lettering, limits, location uniqueness/no-FK, and a
  two-worker concurrency check. Dry-run purge and cutoff validation pass.
- Tracking: the permanent 32-byte Base64URL/SHA-256 vector matches C# and
  PostgreSQL; valid, mutated, missing, expired and revoked tokens are
  fail-closed; private events and payloads are absent; timelines are ordered.
- States and SignalR: the 17 AI-04 mappings are compared with OpenAPI, SignalR,
  the C# fail-closed reference, and the live SQL mapping.
- Acceptance and snapshots: the AI-24 canonical UTF-8 JSON and SHA-256 vectors,
  append-only evidence, cross-tenant denial, atomic quote/order/acceptance/event/
  outbox creation, unique quote consumption, copied snapshot fields, and clean
  rollback.
- Additional database contracts: all normative cents columns are `bigint`,
  values above `int32` work, inconsistent totals fail, external-offer restored
  columns and acceptance constraint execute, and sensitive functions have fixed
  search paths and no `PUBLIC` execute grant.

## Commands

Run the complete project (Docker is required):

```powershell
dotnet restore --locked-mode
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj
```

Run the CI partitions independently:

```powershell
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category!=PostgreSqlContract"
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category=PostgreSqlContract"
```

An explicit local-only escape hatch exists for a workstation without Docker:

```powershell
$env:PAQUETERIA_SKIP_POSTGRES_CONTRACT_TESTS = "true"
```

Skipped tests are not successful ARC-002 validation. CI never sets that
variable and therefore fails if Docker or the runtime contracts fail.

## Verified local result

On 2026-07-21, commit `6bab4077bef5712f967c38c4695ddf07943486c6`
executed the exact image and reported PostgreSQL
`17.5 (Debian 17.5-1.pgdg110+1)`, PostGIS
`3.5 USE_GEOS=1 USE_PROJ=1 USE_STATS=1`, and a 234 ms SQL/bootstrap phase.
The ContractTests project passed 30 tests: 11 static and 19 PostgreSQL, with no
skips. The PostgreSQL project invocation took about 6.5 seconds including
container startup and cleanup on the validating workstation.

## Normative contradiction and limitation

`security.purge_outbox` and `security.purge_location_outbox` in AI-06 select
their candidates with `FOR UPDATE SKIP LOCKED`. AI-18 makes the functions owned
by `paqueteria_maintenance` but grants that role only `SELECT, DELETE` on the two
outbox tables. PostgreSQL 17 requires table `UPDATE` privilege for the locking
clause, so both non-dry-run calls fail with SQLSTATE `42501` even though a direct
maintenance-role delete proves its `DELETE` grant works.

The tests preserve and expose that observed failure. They do not edit AI-06 or
AI-18, add an unauthorized grant, or claim that real purge passed. Consequently
ARC-002 cannot meet its completion gate that both outboxes complete purge.

A second documentation discrepancy is retained: AI-06's introductory comment
calls the baseline PostgreSQL 18, while FND-002 and ARC-002 require the immutable
PostgreSQL 17/PostGIS 3.5 image above.

## Rollback and future work

Rollback is a normal revert of the ARC-002 commits; there are no migrations or
persistent database changes to undo. A normative decision must reconcile the
purge function's lock requirement and maintenance privileges before the two
runtime assertions can be changed to require successful deletion. SEC-001,
SEC-002, TEN-001, DBA-001, product persistence, API/Worker integration, business
use cases and providers remain separate future work.

The 2026-07-21 package review was clean for all .NET dependencies. The frontend
audit reported high advisory `GHSA-f88m-g3jw-g9cj` in Next.js's inherited
`sharp 0.34.5`; current Next.js still constrains that optional dependency to the
affected 0.34 line. It is recorded for frontend dependency maintenance rather
than hidden behind an unsupported ARC-002 override.
