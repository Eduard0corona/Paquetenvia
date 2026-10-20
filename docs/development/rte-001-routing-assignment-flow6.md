# RTE-001: route stop ↔ assignment.route_id as atomic flow 6, and the cross-module write catalog

Backlog item **RTE-001** in `AI-08_BACKLOG.yaml`. Decision row: `RTE-001-ROUTING-ASSIGNMENT-FLOW6-2026-10-20`.
ADR: `docs/adr/ADR-RTE-001-ROUTE-ASSIGNMENT-FLOW6.md`. Source: the 2026-10-19 evaluation, finding R3/BA-02.

The owner's question and answer, literal (2026-10-20, question G1):

- Question: "Rutas: al agregar o quitar una parada, el módulo de Rutas escribe directo en las asignaciones de
  Despacho, en la misma transacción. Lo exige RTE-001, pero no está entre los 5 flujos permitidos (AI-13 §4) y no hay
  prueba que detecte escrituras nuevas entre módulos. ¿Qué hacemos?"
- Answer: "Registrarlo como flujo 6 (Recommended)", whose option read "Fila de decisión y ADR que lo agregan a la
  lista, más la prueba que cataloga cada escritura entre módulos y falla con cualquiera nueva. Sin cambio de
  comportamiento."

The option not chosen and the context shown to the owner are in the decision row.

## Why

`addRouteStop` and `removeRouteStop` write `dispatch.assignments.route_id` in the Routes transaction
(`PostgreSqlRouteService.Persistence.cs`, `LinkAssignmentAsync` and `UnlinkAssignmentAsync`). AI-08 RTE-001 requires
it ("route stop and assignment.route_id are updated atomically") and the owner merged it (RTE-001-MERGE-PR53), but AI-13
§4 listed five flows, AI-03 §25.9 said "Solo cinco flujos" and AI-01 §4.18 forbids a sixth without an ADR and tests. No
test caught a new write into another module's schema: the Orders guard covers Orders → Pricing only, the Dispatch guard
checks one constant, and `DatabaseSchemaCatalogTests` checks schema names only.

Now the flow is the sixth AI-13 §4 flow (`route_stop_to_assignment_route_id` in the AI-04
`transaction_coordinator_allowlist`), and `tests/Paqueteria.ArchitectureTests/CrossSchemaWriteCatalogTests.cs` holds
every cross-module write. Runtime behaviour does not change; no file under `src/` changes.

## What the test reads

- Every `*.cs` file under `src/`, without `Migrations/`, the database `Baseline/`, `bin/` and `obj/`. C# comment lines
  are ignored.
- Every `INSERT INTO`, `UPDATE`, `DELETE FROM` or `MERGE INTO` whose target is schema-qualified, quoted or not, with or
  without `ONLY`, across line breaks, in any case.
- A file's own schema comes from its directory under `src/Modules` through its AI-03 §8 module in
  `DatabaseSchemaCatalog` (Routing is Routes; Realtime has none). Files of the API, the Worker and the BuildingBlocks
  have none. Clients and Allies have no code module, so a write into `clients.*` or `allies.*` is always cross-module.
- The shared schemas `platform`, `security` and `extensions` belong to no module (AI-03 §25.1): outbox, audit and
  idempotency rows are written in the module's own transaction.
- A write whose target is any other schema (another module, `public` or an unknown one) is a site. It is compared with
  the catalog as (file, statement, `schema.table`), one entry per statement, so a second identical statement in the same
  file is a new site.

It fails on a new site, on a catalogued site that is gone and on an entry that names no AI-04 flow or listed ADR. It
also checks that the allowed flows are exactly the six of AI-04 (and that AI-13 §4 numbers six), that each ADR file
exists, that every directory under `src/Modules` has its schema mapping, that no runtime code changes the
`search_path` (an unqualified target then resolves to `"$user", public`, which holds no module table), and that no write
target is an interpolation hole or a concatenated string.

## The eight sites

| # | File | Statement | Allowed by |
| --- | --- | --- | --- |
| 1 | `src/Modules/Orders/Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs` | `UPDATE pricing.quotes` (`status='USED'`) | flow 1 `quote_snapshot_to_order` |
| 2 | `src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs` | `UPDATE orders.orders` (`ASSIGNED`) | flow 2 `assignment_to_order_status_event` |
| 3 | same | `INSERT INTO orders.order_events` | flow 2 |
| 4 | `src/Modules/Dispatch/Dispatch.Infrastructure/ExternalOffers/PostgreSqlExternalOfferService.cs` | `UPDATE orders.orders` (`ASSIGNED`) | flow 5 `external_offer_acceptance_to_assignment` |
| 5 | same | `INSERT INTO orders.order_events` | flow 5 |
| 6 | `src/Modules/Routing/Routing.Infrastructure/Routes/PostgreSqlRouteService.Persistence.cs` (`LinkAssignmentAsync`) | `UPDATE dispatch.assignments SET route_id=@route` | flow 6 `route_stop_to_assignment_route_id` |
| 7 | same (`UnlinkAssignmentAsync`) | `UPDATE dispatch.assignments SET route_id=NULL` | flow 6 |
| 8 | `src/Modules/Organizations/Organizations.Infrastructure/Provisioning/PostgreSqlInitialOrganizationProvisioner.cs` | `INSERT INTO identity.users` | ADR-031 (provisioning, AI-01 §4.4) |

Sites 4 and 5 are the order transition and order event of flow 2 that the external offer acceptance performs in its own
transaction (AI-08 EXT-001 "aceptación atómica"); the catalog names flow 5, the flow that owns that transaction. Flows 3
and 4 have no direct SQL write into another module's schema today: Orders only reads `custody.proofs` and
`finance.cod_transactions` as guards.

## How to add a site

1. If the write belongs to an existing flow or ADR (for example, a flow 2 write moved to another Dispatch file), add
   its entry to `CrossSchemaWriteCatalogTests.Catalog` with that flow, update the table above, and say so in the PR.
2. Otherwise it is a new cross-module flow: AI-01 §4.18 requires an ADR and an owner decision first. Then add the flow
   to AI-13 §4 (and §15's count), AI-03 §25.9 and the AI-04 `transaction_coordinator_allowlist`, update the counts in
   AI-01 §4.18, AI-11 and `CLAUDE.md`, add its constant to `Flows` and its entries to `Catalog`, record the decision row
   and CHANGELOG entry, and regenerate the integrity files. An ADR that allows a write without a flow goes into `Adrs`
   with its file.
3. Removing a write: delete its entry and its row here.

## Limits

- Text scan: SQL built in pieces that split the verb from the table name is not seen.
- EF: a `SaveChanges` write through a mapping to another schema is not covered. Today the only such mapping is Pricing's
  read-only `clients.client_accounts` projection (`AsNoTracking`, `ExcludeFromMigrations`).
- `SECURITY DEFINER` functions in `security` write other schemas under AI-18 and their own ADRs; calls to them are not
  catalogued here.

## Verification

- `dotnet test tests/Paqueteria.ArchitectureTests --filter FullyQualifiedName~CrossSchemaWriteCatalogTests`.
- `The_check_fails_on_a_new_site_a_removed_site_and_a_site_without_a_flow` runs the same check over in-memory sources:
  a Notifications file that updates `orders.orders`, a third `UPDATE dispatch.assignments` in the Routes file, the
  unlink statement removed, and the provisioning entry renamed to an unknown flow; each fails with its message.
- A scratch mutation on a real file (an `UPDATE orders.orders` constant appended to
  `PostgreSqlNotificationsStore.cs`, then reverted) failed the main test with "New cross-module write
  ...PostgreSqlNotificationsStore.cs:342 UPDATE orders.orders: it is not catalogued. A write into another module's
  schema needs an ADR (AI-01 §4.18) ...".
