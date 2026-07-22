# DBA-001 controlled database baseline

## Purpose and boundaries

DBA-001 turns the frozen v0.6 SQL into a reproducible initial deployment and
verification path. It does not add product persistence, connect API or Worker
to PostgreSQL, integrate AuthCenter, implement tenant context, or start
SEC-002/TEN-001/TEN-002. GATE-007 remains blocking before real PII is used.

The implementation reads the canonical files directly. AI-06 creates the
physical catalog; AI-18 immediately applies the ownership and least-privilege
role model. Their mandatory hashes and order are declared in
`database/migrations/v0.6-baseline.json`:

```text
AI-06 c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96
AI-18 7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd
```

## Migrator and commands

`tools/Paqueteria.DatabaseMigrator` is an independent .NET process registered
in the solution. It depends on the infrastructure building block, not API,
Worker or business modules. The PowerShell 7 wrapper resolves the repository
root and propagates the process exit code:

```powershell
pwsh .\tools\database-baseline.ps1 Verify
$env:PAQUETERIA_DEPLOYMENT_DB = "Host=...;Database=...;Username=...;Password=..."
pwsh .\tools\database-baseline.ps1 Plan -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB
pwsh .\tools\database-baseline.ps1 Apply -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB -ConfirmInitialBaseline
pwsh .\tools\database-baseline.ps1 Assert -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB
```

Only the environment-variable name is accepted on the command line. `verify`
does not open a connection. Output identifies only host, port, database and
user; it never prints the connection string or password. `apply` requires the
explicit confirmation flag.

The state detector reports:

- `Clean`: none of the baseline database objects exist. Cluster-wide roles may
  already exist when another isolated database shares the same server.
- `Applied`: all critical schemas, tables, functions and roles exist. The SQL
  is not rerun; assertions execute and the result is `AlreadyApplied`.
- `Partial`: only a subset exists. Deployment fails closed and reports present
  and missing critical objects; it never silently repairs the database.

AI-06, AI-18 and the assertions run in one PostgreSQL transaction. The migrator
obtains a transaction-scoped advisory lock, detects state after acquiring it,
executes both files in strict order, runs assertions, and commits only if all
checks succeed. Any failure rolls back and returns a nonzero exit code.

## Physical catalog and assertions

`DatabaseSchemaCatalog` maps the 15 normative modules one-to-one:

```text
Identity/identity                  Organizations/organizations
Clients/clients                    Locations/locations
Pricing/pricing                    Orders/orders
Dispatch/dispatch                  Drivers/drivers
Routes/routes                      Custody/custody
Incidents/incidents                Finance/finance
Allies/allies                      Notifications/notifications
Reporting/reporting
```

`platform`, `security` and `extensions` are shared application schemas.
`public` is external/shared and intentionally hosts PostGIS. Architecture tests
compare this immutable catalog with controlled parsing of AI-06 and AI-18 and
reject uncataloged module schema declarations.

Executable assertions query real PostgreSQL catalogs and check PostgreSQL 18,
PostGIS 3.6 in `public`, pgcrypto in `extensions`, digest execution, schemas,
role flags/memberships, ownership, forced RLS, triggers, function owners,
PUBLIC revocations and the exact outbox grant matrix. They inspect
`pg_default_acl` and create then roll back a synthetic table in every module
schema to prove future table and sequence privileges. No probe table remains.

## Credentials and security model

- Deployment is a separately managed privileged login. It creates roles,
  changes ownership and runs assertions, and is never used by API or Worker.
- API is an external non-superuser/NOBYPASSRLS login that may assume only
  `paqueteria_app`.
- Worker is an external non-superuser/NOBYPASSRLS login that may assume only
  `paqueteria_worker`.

AI-18 roles are NOLOGIN. Runtime cannot assume migrator, bootstrap, executor or
maintenance, does not own objects, and cannot create in `public`. Bootstrap
owns only approved identity/tracking functions. Executor owns
claim/settle/requeue and has only SELECT/UPDATE on both outbox lanes.
Maintenance owns purge and has only SELECT/DELETE; it has no UPDATE. Producers
have INSERT but no direct SELECT/UPDATE/DELETE and Worker invokes lifecycle
only through approved security functions.

Real contracts prove append-only behavior on `orders.order_events`,
`orders.order_acceptances`, `custody.proofs` and `platform.audit_logs`, including
trigger rejection for a privileged deployment login. Both outbox lanes reject
direct lifecycle access and `INSERT ... RETURNING` for runtime producers.

## Minimal EF mapping

`PlatformOutboxDbContext` is internal to the infrastructure building block and
maps only `platform.outbox_events` and `platform.location_outbox_events`. UUIDs,
timestamps, status and all inserted values come from application code; every
mapped property uses `ValueGeneratedNever`. There are no EF defaults,
sequences, identity columns, generic repository or tracked lifecycle updates.

Testcontainers tests perform `SaveChangesAsync` for both lanes, capture the
executed command through an interceptor, prove it is INSERT without `RETURNING`,
verify the row through a privileged connection, prove runtime cannot read it,
and remove the synthetic data through the test administrator.

## Test environment and CI

Runtime contracts use only the pinned ephemeral image:

```text
postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d
```

Testcontainers assigns a dynamic port and destroys the container. Tests create
isolated databases for clean, applied, partial and concurrent cases. They never
use the persistent FND-002 Compose database or store connection strings as
artifacts. DBA-001 stays in the existing `PostgreSqlContract` category used by
`Validate runtime contracts`; no sixth CI job is introduced.

## Runbook and rollback

1. Preflight: validate the 73-file normative baseline and hashes; confirm the
   target, maintenance window, empty/known state and a tested backup.
2. Run `Verify`, then `Plan`. A `Partial` result is an immediate no-go.
3. Review the sanitized target and run `Apply` with the deployment login.
4. Require `Applied`/`AlreadyApplied` and successful assertions before go-live.
5. On any failure, stop. Do not auto-repair ownership or grants.
6. In CI/Testcontainers, rollback means destroy the ephemeral database. In an
   empty non-production environment, an authorized operator drops and recreates
   the whole database. Future production rollback is backup restore or a
   controlled forward-fix, never a selective destructive `Down` script.
7. Diagnose, restore/recreate when authorized, repeat preflight, then retry the
   entire atomic baseline.

Future module work will use one DbContext and migrations assembly per module,
expand/contract changes and the migrator credential in the pipeline. API and
Worker never migrate at startup. A module may not mutate another module's
tables; cross-module changes require explicit coordination. TEN-001 will add
functional Identity/Organizations persistence. DBA-001 creates no fictional EF
snapshots or entities for the other canonical tables.
