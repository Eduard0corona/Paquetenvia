# SCL-007 PR 8: one connection pool per database role

Phase 4 PR 8 (`docs/development/phase4-platform-plan.md`, "PR 8: un pool de conexiones por rol (SCL-007)"), backlog
item **SCL-007** in `AI-08_BACKLOG.yaml`. Decision row: `SCL-007-POOL-PER-ROLE-2026-10-13`, which applies
`PHASE4-PLATFORM-APPROVAL-2026-10-12` (owner's answer: "Sí, avanza con la fase 4 (Recommended)"; the question named "un
pool de conexiones por rol"). No new owner question. The adaptive outbox wait is PR 9.

## Before and after

Before, 22 code sites built an `NpgsqlDataSource`. At run time that made these pools:

| Process | Pools | Built by |
| --- | --- | --- |
| API | 5 | the unkeyed `NpgsqlDataSource` of whichever module registered first (Identity; the `TryAdd` of Drivers, Dispatch, Orders, Organizations, Realtime, Custody, Incidents, Reporting, Finance and Routing lost), Locations (own, NetTopologySuite), Pricing (own), Data Protection (own), the realtime dispatchers (own, `ConnectionStrings:PaqueteriaRealtime`) |
| Worker | 9 | Custody proof validation (`ConnectionStrings:Paqueteria`), OPS-003 cleanup, OPS-004 retention, Data Protection, Notifications, the Dispatch lifecycle lane, the Orders jobs (claim window and auto close), the Identity and Organizations audience readers (all `ConnectionStrings:PaqueteriaWorker`, the same login) |

The pilot runtime secrets carry `Maximum Pool Size=5`, so the two processes could open 14 × 5 = 70 connections. The
B1ms server has 35 user connections.

After:

| Process | Role | Pool | `DatabasePools` setting | Default |
| --- | --- | --- | --- | --- |
| API | `paqueteria_app` | `ConnectionStrings:Paqueteria` | `ApplicationMaximumPoolSize` | 10 |
| API | `paqueteria_realtime` | `ConnectionStrings:PaqueteriaRealtime` | `RealtimeMaximumPoolSize` | 4 |
| Worker | `paqueteria_worker` | `ConnectionStrings:PaqueteriaWorker` | `WorkerMaximumPoolSize` | 10 |

Three pools, 24 connections at most.

## Design

`src/BuildingBlocks/Paqueteria.Infrastructure/Database/Pooling/`:

- `RuntimeDatabaseRole` names the three roles, their connection strings and their AI-18 roles.
- `RuntimeDataSource` is the pool of one role. It is built on first use, so a host with a disabled provider never needs
  the connection string. Then it fails closed: `ConnectionStrings:<name> is required by the <role> connection pool.`
  It replaces any `Maximum Pool Size` of the connection string with the configured size. A `Minimum Pool Size` above
  that size refuses to build, and so does a connection string that does not parse
  (`ConnectionStrings:<name> is not a valid connection string.`); neither error echoes the connection string.
- Whatever the connection string or a plugin says, every pool is built with `Pooling=true` (no unbounded physical
  connections), `Multiplexing=false` (one command per connection, so `set_config` and `SET LOCAL ROLE` stay on the
  transaction's connection), `Include Error Detail=false` (server detail carries row values, which can be personal data,
  into exceptions and logs) and `Max Auto Prepare=0` (a prepared statement is session state that outlives the lease).
  The settings are forced rather than refused, the same way as the size.
- `AddRuntimeDataSource(configuration, role)` is the only registration. The first call registers the role's
  `RuntimeDataSource`, and its `NpgsqlDataSource`, as keyed singletons (key: the role). Every later call reuses them.
  A process therefore holds one pool per role, whatever the order in which it composes its modules.
- `AddRuntimeDataSourcePlugin` adds a type plugin to a role's pool. It is applied when the pool is built, so it reaches
  the pool whichever module registered first. Locations and Drivers register NetTopologySuite on the `paqueteria_app`
  pool. The building block does not reference that package, and adding it would need a dependency admission. Before
  this change the Drivers plugin was lost to Identity's `TryAdd`; Drivers writes its points with `ST_MakePoint`, so
  nothing broke.

Every module asks for the role it runs as:

| Module | Role | How it resolves the pool |
| --- | --- | --- |
| Identity, Organizations, Orders, Dispatch, Drivers, Locations, Pricing, Routing, Finance, Incidents, Reporting, Realtime (hub authorization) | `paqueteria_app` | `serviceProvider.GetRuntimeDataSource(RuntimeDatabaseRole.Application)` for EF Core; `[FromKeyedServices(RuntimeDatabaseRole.Application)]` for ADO.NET readers |
| Custody | `paqueteria_app` in the API; `paqueteria_worker` with the proof validation worker | the role follows `addValidationWorker` |
| Realtime outbox dispatchers | `paqueteria_realtime` | `RealtimeDispatcherConnectionFactory` over the keyed pool |
| Notifications, Dispatch lifecycle lane, Orders jobs, Identity and Organizations audience readers, OPS-003 cleanup, OPS-004 retention | `paqueteria_worker` | the module's connection factory over the keyed pool |
| Data Protection | the pool named by `DataProtection:ConnectionStringName`, paired with `DataProtection:RuntimeRole`: `Paqueteria` with `paqueteria_app` (API) or `PaqueteriaWorker` with `paqueteria_worker` (Worker) | the keyed pool; any other name or pairing registers no pool and fails the start |

No `NpgsqlDataSource` is registered without a key, so a consumer that does not name its role cannot be built.

## Connection budget

| Quantity | Value | Source |
| --- | --- | --- |
| B1ms `max_connections` | 50 | Azure limits for Azure Database for PostgreSQL flexible server |
| Reserved by Azure (replication and monitoring) | 15 | same page: "Maximum user connections" is 35 for B1ms |
| User connections | 35 | 50 − 15 |
| Runtime budget | 24 | floor(35 × 0.70), AZR-001-INV-014 (`docs/operations/azr-001/azr-001-v0.8.md` §19) |
| Runtime pools | 10 + 4 + 10 = 24 | `DatabasePools` defaults (one API replica and one Worker replica, GATE-013) |
| Left for everything else | 11 | migrator jobs (`pg-migrate-connection`, `Maximum Pool Size=4`), restore drill (`Maximum Pool Size=2`), administration and diagnostics (5) |

`DatabasePools` is validated at start in both processes:

- each size is between 1 and 24;
- the sum of the three is at most 24 (`DatabasePoolOptions.RuntimeConnectionBudget`).

Each process validates the three values of its own configuration, and neither process can see the other's. The API
opens only the `paqueteria_app` and `paqueteria_realtime` pools and the Worker only the `paqueteria_worker` pool, so
the check bounds the total of both processes only when the API and the Worker carry the same three values. With the
same values in both, a sum above 24 refuses both starts. With different values it may not: a Worker given 1, 1 and
22 and an API left at 10, 4 and 10 both start, and together may hold 22 + 10 + 4 = 36 connections, above the budget
and above the 35 user connections. The sum error therefore says that the three values must be identical in the API
and the Worker.

A value that is not a number refuses the start too. The defaults live in code and in both `appsettings.json` files,
and `Scl007PoolBudgetConsistencyArchitectureTests` keeps the two `DatabasePools` sections identical and equal to the
code defaults. The Bicep sets none, so the pilot runs with the defaults. A change of sizes sets all three
`DatabasePools__*` variables, with identical values, on both container apps (see "Rollback"). The pilot secrets keep
`Maximum Pool Size=5`; the runtime replaces it.

Revision changes: Container Apps (single revision mode) starts the new replica before it removes the old one. During
that overlap both revisions may hold pools. They start empty (`Minimum Pool Size=0`) and the old one drains, so
the overlap is short and rarely full. Two full sets would be 48, above the 35 user connections. That is a residual
risk, written in the PR. A larger tier or PgBouncer (GATE-014; Burstable servers have no built-in PgBouncer) would
remove it.

## Session state on shared pools

The runtime keeps no session state, so a shared pool cannot leak one tenant's context or role to the next lease:

- every tenant unit of work runs in an explicit transaction. `set_config(..., true)` and `SET LOCAL ROLE` come after
  `BEGIN` (`TenantTransactionContext`, `WorkerTenantTransactionContext`, every ADO.NET reader and executor call);
- there is no `SET ROLE`, `SET SESSION`, `set_config(..., false)`, `PREPARE`, prepared command, temporary table or
  `LISTEN` in runtime code (only the migrator's baseline uses `SET ROLE` and `RESET ROLE`);
- every advisory lock is `pg_advisory_xact_lock`.

`Scl007SharedPoolSessionStateContractTests` (PostgreSQL) proves it on the real pools. It composes the three pools with
`AddRuntimeDataSource`, one physical connection each, with Npgsql's reset on and off (`No Reset On Close=true`, as a
transaction pooler would behave):

- tenant A, then tenant B, three times on the same backend. Each sees only its own client accounts and its own
  `app.current_user_id` and `app.current_org_ids`. Between leases the session equals a fresh connection's: the login is
  `current_user` and `session_user`, `role` is `none`, both settings are empty, and there is no prepared statement,
  temporary relation, session-sourced setting, advisory lock or LISTEN channel. With no context, `paqueteria_app` reads
  no tenant row;
- the same for Worker units of work on the `paqueteria_worker` pool;
- on every role pool: an aborted transaction (`1 / 0`, then `25P02`), a transaction abandoned with an advisory lock and
  no commit or rollback, and a cancelled statement each leave the next lease equal to a fresh connection;
- a failing tenant unit of work through `TenantTransactionContext` rolls back, and the next tenant reuses the clean
  connection.

The TEN-002 pooled-connection regression (`TransactionalRlsPoolingContractTests`) now runs on the shared-pool builder:
`PostgreSqlContractFixture.CreateAppDataSource` builds through `RuntimeDataSource.Create` with NetTopologySuite.

## Each pool authenticates as exactly one role

Unchanged and still enforced. Each pool has one connection string and one login, a NOINHERIT NOBYPASSRLS member of its
role, and its transactions assume that role with `SET LOCAL ROLE`. A login without that membership fails there. The
readiness checks run on the shared pools:

- `realtime_outbox_dispatcher` keeps failing on a wrong realtime login shape (membership or attributes);
- `custody_worker_database_role` keeps failing unless the Worker pool assumes `paqueteria_worker` without BYPASSRLS. It
  now opens the pool inside its `try` and reports Unhealthy, instead of throwing, when the Worker connection is missing;
- `data_protection_keyring` and the Orders, Dispatch, Notifications and OPS-003 checks work as before.

## Credentials outside the runtime pools

They keep their own credentials and are out of scope:

| Consumer | Credential | Notes |
| --- | --- | --- |
| `job-pv-pilot-migrate`, `job-pv-pilot-verify` (DatabaseMigrator) | `pg-migrate-connection` (`PAQUETERIA_MIGRATION_CONNECTION`, `Maximum Pool Size=4`) | E-002 baseline and module lanes |
| `job-pv-pilot-logins` | `pg-migrate-connection` plus the three SCRAM verifiers | creates the runtime logins |
| `deploy/azure/pilot/restore-drill.sh` | administrator (`Maximum Pool Size=2`) | OPS-002 restore drill |
| `tools/Paqueteria.DevSeed` | local administrator and app logins | local Development and DEV_SYNTHETIC only |
| `paqueteria_bootstrap`, `paqueteria_maintenance` and the other executor roles | none | NOLOGIN; reached only by EXECUTE of their functions through the runtime pools |

## Behaviour changes

- The Worker's Custody proof validation reads `ConnectionStrings:PaqueteriaWorker`, the Worker role's pool, instead of
  `ConnectionStrings:Paqueteria`. The pilot maps the same secret to both names (`apps.bicep`, guard P06), so nothing
  changes there. The Worker now reads no `ConnectionStrings:Paqueteria`; the mapping stays because guard P06 requires
  it, and dropping it is a later cleanup.
- A Worker module whose connection string is missing fails closed with the named error. Before, it fell back to a
  placeholder host (`Database=disabled`).
- `DataProtection:ConnectionStringName` accepts only `Paqueteria` with `DataProtection:RuntimeRole` `paqueteria_app`,
  and `PaqueteriaWorker` with `paqueteria_worker`. Before, either canonical role could be paired with either name, so a
  Worker configured with `Paqueteria` would have built a second pool on its `ConnectionStrings:Paqueteria` entry. Both
  `appsettings.json` and both ScaleReady profiles already use the allowed pairs.
- A connection string can no longer turn pooling off, multiplex, include server error detail or auto-prepare. The
  pilot's runtime secrets (`deploy-azure-pilot.yml`) set none of them; only test connection strings to closed ports set
  `Pooling=false`, and they still fail the same way.
- The `paqueteria_app` pool maps PostGIS types (NetTopologySuite) for every API module. Only Locations and Drivers
  read or write geometry through EF Core; the others read none.

## Connection count during OPS-001

`Ops001DeliverySimulationTests.Two_consecutive_runs_deliver_twenty_orders_with_worker_recovery` samples
`pg_stat_activity` every 100 ms during both runs (`Ops001ConnectionSampler`). It counts the composed runtime pools by
application name: the API's `paqueteria_app` and `paqueteria_realtime` pools and the proof worker's pool. It asserts
each peak is within its configured size and the total within 24. The harness's own checks and administration
connections are not runtime pools and are not counted. The peaks are written to the test output (TRX).

## Tests

- Unit (`Scl007RuntimeDataSourceTests`): budget arithmetic and defaults; each invalid size, an excessive sum and a
  non-numeric value fail the start; repeated registrations share one pool per role; the configured size replaces the
  connection string's; `Pooling=false`, `Multiplexing=true`, `Include Error Detail=true` and `Max Auto Prepare` (alone,
  combined, or set by a plugin) are overridden; a missing or unparseable connection string fails closed on first use,
  naming only its entry; plugins reach only their role's pool, once; role names; each Data Protection name and role
  pairing, and a mispaired Worker key ring registers no second pool and fails the start.
- Architecture (`Scl007PoolPerRoleArchitectureTests`): no runtime code builds a pool or a connection (only the pooling
  folder and the migrator's baseline deployer may). The guard reads the source (including target-typed `new`,
  qualified names and object initializers) and the compiled IL of every catalogued assembly: any constructor of
  `NpgsqlConnection`, `NpgsqlDataSourceBuilder`, `NpgsqlSlimDataSourceBuilder` or `RuntimeDataSource`,
  `NpgsqlDataSource.Create`, `NpgsqlConnection.GlobalTypeMapper`, `RuntimeDataSource.Create` and every EF Core
  `UseNpgsql` not given a data source, in lambdas and async methods too. A design-time factory is exempt only in a file
  named `*DbContextFactory.cs` (plus `PlatformEvolutionDbContext.cs`, listed by name) and only for the connection string
  it gives EF Core. Fixtures prove each bypass form is caught. Every runtime EF Core context uses a shared role pool;
  nothing registers or resolves an unkeyed pool; every injected pool names its role.
- Architecture (`Scl007PoolBudgetConsistencyArchitectureTests`): the `DatabasePools` sections of
  `src/Paqueteria.Api/appsettings.json` and `src/Paqueteria.Worker/appsettings.json` are identical and equal to the
  code defaults (every settable size of `DatabasePoolOptions`, read by reflection), and no other `appsettings*.json`
  profile of either host sets `DatabasePools`. The comparison is on parsed values: a missing, extra, repeated,
  differently cased or non-numeric key, a flat `DatabasePools:` key and a section that is not an object all fail it.
  Its cases include the asymmetric override of the review finding (Worker 1, 1 and 22 against API 10, 4 and 10), and
  the profile check is proven on a nested, a differently cased and a flat `DatabasePools` key.
- Integration (`Scl007PoolPerRoleCompositionTests`): the real API holds exactly the `paqueteria_app` and
  `paqueteria_realtime` pools, the real Worker exactly the `paqueteria_worker` pool, with no unkeyed data source; every
  EF Core context of each host uses that one pool; the sizes replace the pilot secrets' `Maximum Pool Size=5`; a sum
  above the budget in a process's own configuration refuses that process's start, in either process.
- PostgreSQL (`Scl007SharedPoolSessionStateContractTests`) and the TEN-002 regression on the shared-pool builder, as
  above.

## Connection alert (follow-up)

Decision `SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14`. The owner answered "Sí, 80 % y 95 % (Recommended)" ("Dos
reglas sobre las 35 conexiones de usuario: aviso en 28 y crítico en 33, como pide AI-15. Son 2 series, dentro de las 10
gratis por recurso; si no, unos 0.10 USD por serie al mes. Avisa al correo de alertas que pongas al desplegar.") and,
for the calculation, "Máximo en 5 min, cada minuto (Recommended)". The pilot now has two Azure Monitor metric alerts in
`deploy/azure/pilot/observability.bicep`, on `active_connections` of `pg-pv-pilot-<suffix>`:

| Rule | Fires when | Severity |
| --- | --- | --- |
| `mar-pv-pilot-pg-connections-warning` | 28 or more connections (80 % of the 35 user connections) | 2 |
| `mar-pv-pilot-pg-connections-critical` | 33 or more (95 %) | 1 |

- **Evaluation:** the Maximum of the one-minute samples of the last 5 minutes, every minute. They are stateful and
  e-mail the existing pilot action group `ag-pv-pilot-ops`. Its address is still the deploy parameter
  `alertEmailAddress` (`OWNER_DECISION_REQUIRED`).
- **Metric:** `active_connections` counts every connection in `pg_stat_activity`, whatever its state
  ([supported metrics](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-dbforpostgresql-flexibleservers-metrics),
  [monitoring](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/concepts-monitoring)).
- **What the thresholds mean:** with the default sizes the runtime pools hold 24. So 28 means the pools are full and
  jobs or administration hold 4 more, or something is over its budget: a manual `DatabasePools__*` override that
  differs between the apps, or two sets of pools during a revision change. 33 leaves 2 connections before PostgreSQL
  refuses new ones.
- **Cost:** 2 time series, inside the 10 free per resource, otherwise about 0.10 USD per series per month, whatever the
  frequency ([Azure Monitor pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/)).
- **Guards:** guard P21 (`tools/azr-001/env001_pilot_guards.py`) pins the rules: target server, metric, aggregation,
  window, frequency, thresholds, severities and action group, with no e-mail address. Every other metric alert is
  refused.

**When it fires** (runbook in `docs/operations/env-001-pilot/README.md` §10.2):

1. Check whether a revision change, a pilot workflow run, a `job-pv-pilot-*` execution or the restore drill was running
   then. A revision change resolves by itself.
2. Look for `DatabasePools__*` on `ca-pv-pilot-api` and `ca-pv-pilot-worker`. The Bicep sets none. Any manual values
   must be all three, identical on both apps and at most 24 in total; otherwise re-run the workflow for the current SHA.
3. Close administration sessions that are no longer needed.
4. If the runtime alone is using its whole budget under load, revisit the sizes within the 24 or the tier. That needs an
   owner decision.

## Pending

- The sizes are a starting point inside the budget. Pool waits or exhaustion seen in the pilot are a reason to revisit
  them within the 24.

## Residual risks

No action in this PR; each is written here so the pilot can watch it.

- Revision changes can briefly hold two sets of pools (see "Connection budget"); two full sets would exceed the 35 user
  connections. The PostgreSQL connection alerts fire at 28 and 33 (see "Connection alert (follow-up)").
- OPS-001 does not sample the real Worker pool. Its proof worker composes Custody the way the API does, so the
  connection count covers the API's `paqueteria_app` and `paqueteria_realtime` pools and that worker, not the Worker
  host's `paqueteria_worker` pool under load.
- No readiness check covers the shape of the `paqueteria_app` login (membership, NOINHERIT, NOBYPASSRLS), unlike the
  realtime and Worker logins. This predates the PR; a login without the membership still fails at its first
  `SET LOCAL ROLE`, but other drift of its attributes is not reported by readiness.
- Each process now has less pool capacity than before: the modules of a process compete for one pool of 10 (or 4)
  instead of holding several pools of 5. Pool waits under load are the signal to revisit the sizes within the 24.
- A manual override that differs between the two container apps is not detectable by either process. Each validates
  only its own `DatabasePools`, so, for example, `DatabasePools__*` set to 1, 1 and 22 on the Worker alone starts both
  processes with up to 36 connections (see "Connection budget"). The architecture test covers only the committed
  `appsettings*.json` files, and the operations rule (all three variables, identical, on both apps) is a procedure, not
  a check. The backstops are PostgreSQL `max_connections`, which refuses connections beyond the 35 user connections
  (`too many connections`), and the two Azure Monitor alerts on PostgreSQL connections at 28 and 33
  (`SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14`, see "Connection alert (follow-up)").
- Microsoft does not document whether `active_connections` includes Azure's own `azure_superuser` connections. If it
  does, the alerts fire with fewer than 28 or 33 user connections, which is the safe direction. At the first deployment,
  while the apps are idle, compare the metric (Max) with `pg_stat_activity` grouped by user, from an administration
  session.
- The metric is sampled once a minute, so a burst between two samples can go unseen by the alerts. *Failed
  Connections* (`connections_failed`) counts the connections PostgreSQL refused.

## Rollback

Revert the pull request. There is no schema, role, grant, migration, secret or Bicep change. To change the pool sizes
without a release, set all three variables, `DatabasePools__ApplicationMaximumPoolSize`,
`DatabasePools__RealtimeMaximumPoolSize` and `DatabasePools__WorkerMaximumPoolSize`, with identical values on both
container apps (`ca-pv-pilot-api` and `ca-pv-pilot-worker`), never on one alone and never only some of them. Today the
Bicep sets none. Each process refuses a sum above 24 in its own configuration, but only identical values on both apps
keep the total of both processes within the budget.
