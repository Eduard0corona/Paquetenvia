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
  that size refuses to build, and the error does not echo the connection string.
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
| Data Protection | the pool named by `DataProtection:ConnectionStringName` (`Paqueteria` in the API, `PaqueteriaWorker` in the Worker) | the keyed pool; any other name fails the start |

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

Both processes bind all three values, so raising one value in one process alone also refuses that process's start.
A value that is not a number refuses the start too. The defaults live in code and in both `appsettings.json` files.
The Bicep sets none, so the pilot runs with the defaults. The pilot secrets keep `Maximum Pool Size=5`; the runtime
replaces it.

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
- `DataProtection:ConnectionStringName` accepts only `Paqueteria` and `PaqueteriaWorker`.
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
  connection string's; a missing connection string fails closed on first use, naming only its entry; plugins reach only
  their role's pool, once; role names; Data Protection connection names.
- Architecture (`Scl007PoolPerRoleArchitectureTests`): no runtime code builds a pool or a connection (only the pooling
  folder and the migrator side may); every runtime EF Core context uses a shared role pool; nothing registers or
  resolves an unkeyed pool; every injected pool names its role.
- Integration (`Scl007PoolPerRoleCompositionTests`): the real API holds exactly the `paqueteria_app` and
  `paqueteria_realtime` pools, the real Worker exactly the `paqueteria_worker` pool, with no unkeyed data source; every
  EF Core context of each host uses that one pool; the sizes replace the pilot secrets' `Maximum Pool Size=5`; a size
  above the budget refuses either start.
- PostgreSQL (`Scl007SharedPoolSessionStateContractTests`) and the TEN-002 regression on the shared-pool builder, as
  above.

## Pending

- An Azure Monitor metric alert on PostgreSQL connections is not added. It waits for the owner's alert decision and the
  alert e-mail (`alertEmailAddress`, still `OWNER_DECISION_REQUIRED`). The threshold would be compared with AI-15
  `connection_pool_utilization` (0.8 warning, 0.95 critical), and its monthly cost with PILOT-BUDGET-ACCEPT-98-108.
- The sizes are a starting point inside the budget. Pool waits or exhaustion seen in the pilot are a reason to revisit
  them within the 24.

## Rollback

Revert the pull request. There is no schema, role, grant, migration, secret or Bicep change. To shrink the pools
without a release, set `DatabasePools__ApplicationMaximumPoolSize`, `DatabasePools__RealtimeMaximumPoolSize` or
`DatabasePools__WorkerMaximumPoolSize` to smaller values; the start validation refuses anything above the budget.
