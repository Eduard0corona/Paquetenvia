# OBS-003 PR 7: trace context through the outbox

Phase 4 PR 7 (`docs/development/phase4-platform-plan.md`, "PR 7: contexto de traza a través del outbox (OBS-003)"),
backlog item **OBS-003** in `AI-08_BACKLOG.yaml`. Decision row: `OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13`.

The owner's question and answer, literal (2026-10-13):

- Question: "PR 7: cuando el Worker procesa un evento del outbox (notificaciones, avisos en tiempo real), ¿cómo lo
  ligamos con la petición que lo originó?"
- Answer: "Columna nueva (Recommended)", whose option read "Una columna con la traza en las dos tablas del outbox
  (migración con ida y vuelta probadas). Los logs del Worker llevan la traza de la petición original, así que una sola
  búsqueda en Log Analytics muestra API y Worker juntos, aunque haya muestreo. No toca el contenido del evento ni lo que
  reciben los clientes. Cumple AI-03 §18 tal como está."

The options not chosen and the context shown to the owner are in the decision row.

## Before and after

Before, every outbox dispatch cycle ran in its own root trace (PR 1), and every message of the cycle shared it. A log
line written while the Worker sent a notification carried the cycle's trace id, not the trace id of the request that
created the order.

After, each claimed message runs in its own activity, a child of the trace that wrote the row. Its log lines carry the
trace id of that request or job. The dispatch cycle keeps its own trace for its own lines (the lane summary, the claim
and the requeue), and it is still never exported.

## Representation

`platform.outbox_events.trace_parent` and `platform.location_outbox_events.trace_parent`: `text`, nullable, no default.
The value has the shape of a W3C `traceparent` of version 00 and nothing else:

```text
00-{32 lowercase hex trace id}-{16 lowercase hex span id}-{00|01}
```

- The span id is the producer's own activity, so the consumer's activity is its child.
- The last field is the producer's sampled flag. The export needs it (see "Export" below), so a bare trace id is not
  enough.
- Never `tracestate`, baggage, a tenant, a user or request data. The column is never part of the immutable `payload`.
- A CHECK on each table (`outbox_events_trace_parent_check`, `location_outbox_events_trace_parent_check`) holds every
  value to that exact shape, with neither id all zero. The runtime writes and parses the same pattern
  (`OutboxTraceContext.Pattern`).
- NULL means no context: rows written before the column existed, or written outside any W3C activity.
- Once written, it never changes: the AI-06 content guards (`platform.protect_outbox_content()` and
  `platform.protect_location_outbox_content()`, the `BEFORE UPDATE` triggers of both tables) compare it with the
  content, so an UPDATE that changes it fails like one that changes the payload. No lifecycle function sets it; this is
  defence in depth.
- The value is a random identifier of a trace, not of a person or an organization, like the trace id in the logs
  (PR 1).

Code:

- `src/BuildingBlocks/Paqueteria.Infrastructure/Observability/OutboxTraceContext.cs`: the value (`Current()`, `Of`,
  `IsTraceParent`, `TryParse`). It never touches the database (the OBS-002 rule for observability code).
- `src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox/OutboxTraceContextColumn.cs`: the column, the
  `@trace_parent` parameter every producer binds (`Parameter()`), the reader of a claimed row (`Read`) and the
  transaction-local setting `app.trace_parent`.
- The EF outbox model maps the column with `ValueGeneratedNever()`.

## Producers

Every producer writes the value itself, from its own `Activity.Current`, inside its own transaction, with the rest of
the row (no RETURNING, no read back). Outside a trace it writes NULL.

| Producer | Path | Rows |
| --- | --- | --- |
| Order transitions | `Orders.Infrastructure/Orders/PostgreSqlOrderTransitionService.cs` | 3 INSERTs |
| Quote to order | `Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs` | 1 |
| Assignment to order (owner path) | `Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs` | 3 |
| Assignment lifecycle reactor (Worker) | `Dispatch.Infrastructure/Lifecycle/PostgreSqlAssignmentLifecycleReactor.cs` | 1 |
| External offers | `Dispatch.Infrastructure/ExternalOffers/PostgreSqlExternalOfferService.cs` | 1 |
| Manual routes | `Routing.Infrastructure/Routes/PostgreSqlRouteService.Persistence.cs` | 1 |
| GPS ingestion (location outbox) | `Drivers.Infrastructure/Locations/PostgreSqlDriverLocationIngestionService.cs` | 1 |
| Operator outbox definer | `security.append_operator_order_outbox` (Dispatch lane) | via `app.trace_parent` |
| Notification expansion | `security.expand_order_created_notifications` (Notifications lane) | copies the source row's |
| Notification status changes | `security.emit_notification_status_changed` (Notifications lane) | copies the originating row's |

- **Operator outbox definer.** It keeps its published signature, owner, ACL, `SECURITY DEFINER` and `search_path`
  (DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03). The caller sets the transaction-local `app.trace_parent` with
  `set_config(..., true)` right before each call (`OperatorOwnerEventWriter`), as it sets `app.current_org_ids` and
  `app.current_user_id`, and the definer stores `NULLIF(current_setting('app.trace_parent', true), '')`. An application
  version that predates the column keeps calling the same signature during a deploy and stores NULL. The executor
  gains `INSERT (trace_parent)` on `platform.outbox_events` and nothing else.
- **Notifications writers.** The expansion copies the `trace_parent` of the `orders.created` row it holds under its
  lease. The status-change emitter copies the `trace_parent` of the outbox event that created the notification
  (`notifications.notifications.source_event_id`), read in the same transaction; once that event is purged, the new row
  has none. Every caller of the emitter (expansion, outcome, max-attempts finalization, stale-lease recovery) therefore
  writes the originating trace, also from a cycle that has no message activity. The payloads are byte-identical.
- **Chains.** The lifecycle reactor writes its row while processing a claimed message, so its `Activity.Current` is
  the message's activity: the follow-up row carries the same trace id.

`OutboxTraceContextArchitectureTests` fails when:

- a runtime INSERT into either outbox table does not write the column;
- the latest definition of any SQL function (AI-06, then the Up SQL every EF migration of every lane actually runs, in
  migration order) inserts into either table without naming the column. The table may be qualified or not (resolved
  through `search_path`), quoted or not, and an INSERT without a column list counts as missing. Two self-tests feed it a
  later redefinition without the column and an unqualified INSERT, and both are caught;
- a claim reader does not read the column or a consumer does not process in it;
- payload, SignalR or notification delivery code names it.

The PostgreSQL contract test checks the same on the installed catalog: every function in `pg_proc` whose body inserts
into either table names the column in each INSERT's column list.

## Claim functions

Every claim and recovery function returns the full row type of its table (`RETURNS SETOF platform.outbox_events` or
`SETOF platform.location_outbox_events`, `RETURNING o.*`): `claim_realtime_outbox`, `claim_notifications_outbox`,
`claim_unowned_outbox`, `claim_dispatch_outbox`, `claim_location_outbox`, `claim_outbox` and
`recover_stale_notifications_outbox`. Each returns the new column with no change to its signature, owner, grants or
body. Settle, requeue and purge never read or change it. The stores read it by name (`OutboxTraceContextColumn.Read`).

## Consumers

| Lane | Process | Code |
| --- | --- | --- |
| `notifications` | Worker | `NotificationsOutboxDispatcher` |
| `dispatch` | Worker | `AssignmentLifecycleProcessor` |
| `realtime_business` | API | `RealtimeOutboxProcessor.ProcessBusinessAsync` |
| `realtime_location` | API | `RealtimeOutboxProcessor.ProcessLocationAsync` |

Each message runs in `TraceCorrelation.StartOutboxMessage(lane, message.TraceParent)`, disposed when the message is
settled:

- operation `Paqueteria.OutboxMessage`, kind `Consumer`, display name the fixed lane name;
- with a stored context, a **child** of it: same trace id, parent span id the producer's span, the producer's sampled
  flag;
- without one (or with a malformed one, which the CHECK prevents), a child of the current dispatch cycle, as before.

A child, not a span link:

- the log lines carry only the current trace id (`State.TraceId`, PR 1), so only a child puts the originating trace id
  in the message's lines, which is what the owner chose;
- PR 6 never exports a span that carries a link, so a linked span would never reach Application Insights.

The context is parsed as a **local** parent, never a remote one. It was written by this platform's own producers
(inbound trace headers are still ignored, PR 1), and PR 6's sampler drops every remote parent.

## Export

The export (PR 6, off by default) uses `ParentBasedSampler(ExportedRootSampler(ratio 0.10))` with local parents
followed and remote parents dropped:

- a message whose producing trace was sampled (a sampled API request or scheduled job cycle) is exported as a child span
  of that trace, named after its lane, with no attribute (`TelemetryExportPolicy` allows none for it);
- the message's own children go out with it. The main case is the outgoing HTTP call of a notification to its provider
  (WhatsApp Cloud API, Azure Communication Services e-mail), under the PR 6 allowlist: method, server address and port,
  status and error type, never the URL, query, headers or body. Before PR 7 these calls ran under dispatch cycles, which
  are never exported;
- a message whose producing trace was not sampled, or that has no stored context, is never exported;
- a message's span is never a root, so it never reaches the ratio sampler on its own;
- dispatch cycles stay dropped.

So Application Insights shows the message's processing inside the request that caused it, at the request's own 10 %.
The cost follows the sampled requests. Each sampled request that writes outbox rows adds one span per row it wrote,
plus that message's outgoing HTTP calls, usually one provider call for a notification. GPS batches write one location
row per position, so a sampled GPS request adds up to 20 spans. All of it stays within the 10 % sampling and the pilot's
0.3 GB daily cap.

## AI-03 §18 and AI-12

AI-03 §18 ("Toda petición/job/evento debe llevar `trace_id`, `correlation_id`, tenant y actor técnico redactado")
needs no wording change for the two identifiers this PR covers. The `trace_id` of an event is now the trace id of the
request or job that produced it, and `correlation_id` keeps its meaning (PR 1 made the W3C trace id the single
identifier of logs, audit rows and problems). The tenant pseudonym and the actor role kind are not delivered here: the
Worker's per-message lines still lack the pseudonym, an open question below.

AI-12 does not change. The trace context never reaches a SignalR message: the realtime envelope is built from the
AI-12 fields only, and the Notifications templates render only their allowed variables.

## Log Analytics

One query shows the request and everything the Worker and the realtime lanes did for it, also when the trace was not
sampled. Take the trace id from the problem `traceId` the client received or from an API log line:

```kql
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(1d)
| extend e = parse_json(Log_s)
| where tostring(e.State.TraceId) == "<trace id>"
| project TimeGenerated, ContainerAppName_s, Category = tostring(e.Category), EventId = toint(e.EventId), Message = tostring(e.Message)
| order by TimeGenerated asc
```

## Migrations

| Lane | Step | Up | Down |
| --- | --- | --- | --- |
| Dispatch | `20261013000100_AddOutboxTraceContext` | adds (or adopts) both columns and checks; installs the content guards that compare the column; grants the operator outbox executor `INSERT (trace_parent)`; installs the definer body that stores `app.trace_parent` | refuses while the Notifications step is recorded; restores the published content guards (before the column is dropped) and the published definer; revokes the grant; drops both checks and columns |
| Notifications | `20261013000200_CarryOutboxTraceContext` | requires the column; installs the emitter and expansion bodies that copy the originating context | restores the published NTF-001 bodies |

- The outbox tables belong to no module (AI-03 §8). The Dispatch lane carries the column because it is the earliest
  lane in the migrator's order whose functions write it, so every later writer finds it.
- AI-06 declares both columns and checks, so a fresh installation has them before any lane runs (the Notifications
  lane is applied on its own in some tests). The Dispatch step adopts them there and refuses a column or check of any
  other shape, never altering it. The published operator outbox step's adoption check accepts this one column.
- Expand only: the column is nullable without a default, so no row is read or rewritten. Adding the CHECK scans the
  table under the lock the column change takes; retention keeps the outbox small.
- AI-18 keeps the published executor `GRANT INSERT (...)` statement, which the published Dispatch step verifies
  exactly, and documents the added grant; `DatabaseBaselineAssertions` expects it once the Dispatch step is recorded
  (recorded history decides, like every E-002 reader).
- Each function body is the published one plus reviewed edits (`FunctionEdits`, `EmitEdits`, `ExpandEdits`,
  `ContentGuardEdits`), applied exactly once; unit tests revert the edits and compare with the published text byte for
  byte, and check that AI-06 declares exactly the installed content guards.
- `OutboxTraceContextPostgreSqlContractTests` runs Up, Down and Up again on PostgreSQL and compares the catalog (columns,
  checks, triggers, indexes, table and column grants, every function of schemas `security` and `platform` with owner,
  ACL, configuration and body, and the ACL of schema `security`) at each step.

### Rollback

1. Roll the application back first: this version writes the column.
2. Roll the Notifications step back (EF Down of `20261013000200_CarryOutboxTraceContext`).
3. Roll the Dispatch step back (EF Down of `20261013000100_AddOutboxTraceContext`). It refuses with
   `OBS003_TRACE_CONTEXT_DOWNGRADE_ORDER` while step 2 is recorded.

Every outbox row stays. The trace context of existing rows is lost with the column. Nothing else changes.

## Open questions (not decided here)

- Worker audit rows (`PostgreSqlAssignmentLifecycleReactor`, `ProofValidationProcessor`) still write
  `request_id = NULL` (PR 1). They could write the message's trace id now. That changes audit evidence, so it needs the
  owner.
- The Worker's per-message log lines do not carry the organization pseudonym or the actor role kind (PR 6 left the
  Worker without them), which AI-03 §18 asks for when there is an organization context. A message has an owner
  organization, so they could. That widens OBS-003-TENANT-PSEUDONYM-2026-10-12, so it needs the owner.

## Tests

- `tests/Paqueteria.UnitTests/Operations/Obs003OutboxTraceContextTests.cs`: the stored shape, parsing, the message
  activity as a child of the producer or of the cycle, the Notifications and dispatch lanes' log lines in the
  producer's trace, the realtime lane publishing in the producer's trace with an envelope of AI-12 fields only, and no
  trace member on any payload, envelope or notification type.
- `tests/Paqueteria.UnitTests/Operations/Obs003OutboxTraceContextMigrationTests.cs`: the CHECK is the runtime pattern and
  AI-06's; each function and content guard differs from the published one only by its reviewed edits; the one added
  executor grant.
- `tests/Paqueteria.ArchitectureTests/OutboxTraceContextArchitectureTests.cs`: no producer (runtime INSERT or latest SQL
  function definition), claim reader or consumer is missed, with self-tests for a later redefinition and an unqualified
  INSERT; the value never reaches payload, SignalR or notification code.
- `tests/Paqueteria.ContractTests/PostgreSql/OutboxTraceContextPostgreSqlContractTests.cs`: columns, checks and
  privileges; every installed function that inserts into an outbox table names the column; the exact shape on both
  tables; every claim function returns it, the lifecycle and purge leave it and the content guards refuse changing it;
  the Notifications writers copy it and no payload carries it; Up, Down and Up again with an identical catalog.
- `tests/Paqueteria.ContractTests/PostgreSql/DispatchOutboxTraceContextPostgreSqlContractTests.cs`: a real assignment
  stores the producing trace through the owner INSERT and through the operator definer, and NULL outside a trace.
- `tests/Paqueteria.ContractTests/PostgreSql/EfOutboxInsertContractTests.cs`: the EF model writes the column.
- `tests/Paqueteria.IntegrationTests/Observability/Obs003TelemetryExportTests.cs`: with the PR 6 pipeline and a test
  exporter, a message span is exported exactly when its producing trace was sampled, as a child with no attribute, and
  its outgoing HTTP call goes out with it with only the allowlisted method; dispatch cycles, messages without a context
  and remote parents never are.
