# Fix: infrastructure failures answer 503 with one redacted log line

Pilot defect found by the REL-001 research, no backlog item and no normative change. Branch
`fix/infrastructure-failures-503`. Owner decision Z3 (2026-11-01, "PR aparte ya"): "Transiciones, rutas, asignación,
ofertas externas y paradas responden 503 con una línea de log sin datos personales." Code tag:
`REL-001-INFRASTRUCTURE-503-2026-11-01`.

## Defect

When the infrastructure under twelve operations failed, the endpoint answered 409 (or 403 on the reads) and wrote no log
line. AI-05 declares `503 ServiceUnavailable` for all twelve. So in the pilot:

- a database failure on these paths looked like a domain conflict or a refusal, and the OBS-002 API 5xx alert
  (`sqr-pv-pilot-api-5xx`, which counts the HTTP status class) never saw it;
- the web showed its "conflict" text (or, on the reads, "forbidden") instead of "unavailable" (`classifyFailure`), and
  the driver app treated a failed offline transition replay as a conflict instead of retrying it (`driver-sync-api.ts`:
  409 is `conflict`, any 5xx is `recoverable`);
- nobody could tell from the logs that anything had failed.

## Fix

Each endpoint maps its module's infrastructure exception to the 503 problem it already declared (`ProducesProblem(503)`)
and writes one error line. Genuine domain outcomes keep their status: guard codes, idempotency conflicts, optimistic
concurrency and the PostgreSQL unique, serialization and deadlock conflicts the services already classify stay 409;
capability refusals stay 403; cancellation is still rethrown.

| Operation (AI-05) | Exception | Before | After |
| --- | --- | --- | --- |
| `transitionOrder` | `OrderTransitionInfrastructureException` | 409 (no code) | 503 |
| `assignDriver` | `AssignmentInfrastructureException` | 409 `CONFLICT` | 503 |
| `createRoute` | `RoutingUnavailableException` | 409 `CONFLICT` | 503 |
| `listRoutes` | `RoutingUnavailableException` | 403 | 503 |
| `getRoute` | `RoutingUnavailableException` | 403 | 503 |
| `addRouteStop` | `RoutingUnavailableException` | 409 `CONFLICT` | 503 |
| `removeRouteStop` | `RoutingUnavailableException` | 409 `CONFLICT` | 503 |
| `reorderRouteStops` | `RoutingUnavailableException` | 409 `CONFLICT` | 503 |
| `createExternalOffer` | `ExternalOfferInfrastructureException` | 409 `CONFLICT` | 503 |
| `acceptExternalOffer` | `ExternalOfferInfrastructureException` | 409 `CONFLICT` | 503 |
| `listMyEligibleExternalOffers` | `ExternalOfferInfrastructureException` | 403 | 503 |
| `listMyStops` | `DriverStopsInfrastructureException` | 403 | 503 |

The problem follows each file's existing 503: `type`, `title`, `status` and `traceId`, no `code` and no `detail`
(`Service Unavailable.` in Dispatch and Routing, `Service unavailable.` in Orders, as their other 503s already write).

## The log line

`src/BuildingBlocks/Paqueteria.Application/Diagnostics/InfrastructureFailureLog.cs` holds the template and the
redaction; each endpoint file writes it with a source-generated `[LoggerMessage]`:

```text
EventId 4701 InfrastructureFailureUnavailable, Error
Operation {Operation} answered 503 after an infrastructure failure ({ErrorClass}, SQLSTATE {SqlState}).
```

- `Operation` is the fixed AI-05 operation id.
- `ErrorClass` is the type name of the innermost exception of the `InnerException` chain (at most 16 read), for
  example `PostgresException`, `TimeoutException`, `SocketException`, or the module exception itself when it has no
  inner exception (an update that touched no row, a missing result set).
- `SqlState` is the first SQLSTATE of the chain that a `DbException` reports and that is exactly five digits or
  uppercase ASCII letters, otherwise `none`.

Nothing else is read: no exception message, `Detail`, `Hint`, `Where`, internal query, constraint, parameter or row
value (a unique-violation detail can carry row values), and the exception object is never passed to the logger, so the
JSON console writes no `Exception` field. In an organization context the logger factory adds `TraceId`,
`OrganizationPseudonym` and `ActorRoleKind` as on every API line; no tenant, actor, user or resource id is written
(OBS-003-TENANT-PSEUDONYM-2026-10-12, `LogTemplateIdentifierArchitectureTests`). The id is outside the OBS-002 block
(4600-4699), so no alert or workbook reads it and the pseudonym enrichment applies; the 503 itself is what the 5xx alert
counts.

## What does not change

- **The services.** Which failures become the infrastructure exception is unchanged: Orders wraps `PostgresException`,
  `NpgsqlException` and `DbUpdateException` (as `createOrder` already does for its 503), Routing wraps
  `NpgsqlException`, and Dispatch raises its exceptions for inconsistent store results (an update that touched no row, a
  missing result set, an unreadable policy version, an audience beyond the fanout bound).
- **Failures that never reach those exceptions.** An EF Core `RetryLimitExceededException` (all three modules use
  `EnableRetryOnFailure`), and in Dispatch a raw `NpgsqlException`, still escape to the host exception handler and
  answer 500. That is already a 5xx the alert counts, logged by the framework. `PostgreSqlDriverStopsQuery` never raises
  `DriverStopsInfrastructureException`, so the `listMyStops` mapping only takes effect once it does. Changing what the
  services wrap is a separate change.
- **The existing 503s.** `createOrder`, `listOrders`, `getOrder`, `listAssignableDrivers` and `listOrganizationDrivers`
  already answered 503; they keep their behaviour and write no new line.
- No migration, schema, role, contract, configuration, dependency or web change.

## Tests

- `tests/Paqueteria.IntegrationTests/Observability/InfrastructureFailureHttpTests.cs`, one case per operation (12). The
  stubbed service throws the module exception around a `PostgresException` 23505 whose message, detail, hint, where and
  internal query carry an e-mail, a phone number and ids. Each case asserts the 503 problem (exact members, no value of
  the failure), then reads the request's lines as the API's own JSON console writes them (`JsonConsoleCapture`) and
  asserts exactly one `Error` line, with event id 4701, the exact message, `ErrorClass` `PostgresException`, `SqlState`
  `23505`, no `Exception` field, only the template, trace, pseudonym and role-kind properties, and no e-mail, phone,
  UUID, idempotency key, request text or exception text; no other line of the request carries the exception text.
  Against the previous endpoints all 12 cases fail.
- `tests/Paqueteria.UnitTests/Operations/InfrastructureFailureLogTests.cs` (18): the innermost type and the SQLSTATE of
  a real `PostgresException`, a transport failure without SQLSTATE, a chain without inner exception, the first
  well-formed SQLSTATE wins and a malformed one is never written, the depth bound, the SQLSTATE shape and the template
  placeholders.

## Rollback

Revert the commit. Nothing persistent is involved; a rollback only brings back the 409/403 answers without a log line.
