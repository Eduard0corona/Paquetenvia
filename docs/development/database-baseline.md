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
AI-06 1b7729b8901aadd362bf35cd144349ae25471c933967af901b62591ab795091c
AI-18 5fc45998c5f64c88a49ad894d88e89d1775279a8cfd57415e4e03b047110287e
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

TEN-001 now adopts the existing Identity and Organizations tables with one
DbContext and migrations assembly per module. The baseline still runs first;
then the independent migrator records the two non-destructive adoption
migrations in migrator-owned platform histories. API and Worker never migrate
at startup. See [tenant-context-rls.md](tenant-context-rls.md) for planning,
history drift, transaction guards and rollback rules.

POD-001 agrega `CustodyDbContext` y la adopción
`20260725_AdoptCanonicalCustodyProofsBaseline` en
`platform.__ef_migrations_history_custody`. Se ejecuta después de Dispatch y
solo comprueba las tablas canónicas de sesiones/Proof, RLS forzado, políticas,
índices y trigger append-only; no contiene DDL de negocio. Consulta
[pod-001-secure-proof-upload.md](pod-001-secure-proof-upload.md) para el flujo,
planificación y rollback conservador.

Las decisiones del owner del 2026-09-27 (IDENTITY-ORG-ACTIVE-REQUIRED,
AI05-TIMELINE-ORDER y AI06-PILOT-INDEXES) están en AI-06/AI-18 para las
instalaciones nuevas y en la lane `PlatformEvolution`
(`20260927000100_ApplyPilotContractDeltas`, historial
`platform.__ef_migrations_history_platform_evolution`) para las existentes. La
lane corre **al final**, después de Orders, porque la migración RTM-002 de Orders
reescribe `security.get_public_tracking_projection(text)` en una instalación
nueva. Solo cambia los cuerpos de las dos funciones de `paqueteria_bootstrap`
(conservando dueño, ACL y `search_path`), agrega `SELECT(id,status)` sobre
`organizations.organizations` y `SELECT(aggregate_version)` sobre
`orders.order_events` a bootstrap y crea los ocho índices; verifica el conjunto
exacto de columnas de bootstrap y la definición de cada índice, y rechaza un
índice homónimo con otra forma. Su `Down` devuelve el contrato previo a la
decisión (cuerpos, grants e índices) y solo debe acompañar al release anterior.
