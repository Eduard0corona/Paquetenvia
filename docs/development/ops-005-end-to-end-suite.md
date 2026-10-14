# OPS-005: platform end-to-end suite

Phase 4 PR 10 (`docs/development/phase4-platform-plan.md`, "PR 10: pruebas de punta a punta de la plataforma"), backlog
item **OPS-005** in `AI-08_BACKLOG.yaml`. Decision row: `OPS-005-JOURNEYS-2026-10-13`.

The owner's question and answer, literal (2026-10-13):

- Question: "PR 10: además de las comprobaciones de plataforma (traza, 429, conexiones, aviso tras reposo, nada sale
  sin exportación), ¿qué recorridos de negocio corre la suite de punta a punta?"
- Answer: "Recorrido principal (Recommended)", whose option read "Cotizar, crear y confirmar la orden, asignar
  repartidor, recoger, entregar con foto y ver 'Entregada' en el seguimiento público. Un recorrido completo; suma unos
  minutos al job de CI existente."

The options not chosen and the context shown to the owner are in the decision row.

## What is assembled

`tests/Paqueteria.EndToEndTests` reuses the harness of `tests/Paqueteria.IntegrationTests` through a project reference
(`InternalsVisibleTo`), so nothing of it is copied:

| Piece | What runs | Harness |
| --- | --- | --- |
| PostgreSQL | PostgreSQL 18/PostGIS 3.6 with the v0.6 baseline, the module migrations and one `NOBYPASSRLS` login per runtime role (`paqueteria_app`, `paqueteria_realtime`, `paqueteria_worker`) | `PostgreSqlSecurityWebApplicationFactory` (Testcontainers) |
| Storage | The S3-compatible container the driver PWA job already uses (bucket CORS for the web origin), behind a loopback TLS tunnel because the driver app only accepts HTTPS signed uploads | `CorsCapableS3Fixture`, `LoopbackTlsTunnel` |
| API | The real `Program` on Kestrel over loopback TLS, with the realtime dispatchers on their own login | `RealtimeKestrelWebApplicationFactory` |
| Worker | The real `WorkerProgram` (alias `WorkerHost`): proof validation, the dispatch assignment lifecycle and notifications, as the pilot turns them on | `WebApplicationFactory<WorkerProgram>` |
| Web | The production Next.js build served by `next start`, built for the API origin | `DriverStopsNextServer` |
| Browser | Chromium, one context per person (dispatcher, driver, recipient) | Playwright |

The collection fixture (`PlatformEndToEndFixture`) starts PostgreSQL, storage and the web once. Each test starts its own
API and Worker (`PlatformHosts`) on the same API port, so a test-only setting never reaches another test and the web
build is reused.

Configuration is test-host configuration only, through settings the hosts already validate: module providers on
PostgreSQL with the synthetic geocoder and PII protector (as OPS-001), the realtime dispatchers' waits read back from
the API's own `appsettings.json` (the harness would poll every 50 ms), the hosting request lines at Information, the
public tracking link built on the web's loopback origin (allowed in Testing), and the export settings below. Sign-in
uses the Mock identity provider's bearer sessions (`active-dispatcher`, `active-driver`), installed before each page
loads, as every existing browser suite does.

Synthetic data only (SEC-003). The fixture adds to the PostgreSQL fixture's seed the synthetic dispatcher membership,
a service area, an operating zone and a SAME_DAY tariff (12,000 centavos) around the synthetic coordinates, and the
active driver's service area and valid identity document. The wizard's addresses, names and descriptions say
"SINTETICO"; the phone numbers start with 0000.

## The journey (owner answer Q-PR10)

`MainJourneyEndToEndTests` drives one complete journey through the real screens:

1. Dispatcher, "Nueva orden" (`/ops/orders/new`): origin and destination with coordinates, one package, SAME_DAY,
   "Calcular precio", payer and acceptance, "Crear y confirmar orden", then "Abrir orden".
2. Dispatcher, order detail: "Liberar para recolección" (the state machine's step between CONFIRMED and an
   assignment), then "Asignar repartidor" with the eligible own driver and a cost, confirmed with "Asignar".
3. Dispatcher, order detail: "Ver enlace" gives the order's public tracking link.
4. Driver PWA (`/driver/stops`, "Ver detalle"): "Llegué a recolección", the pickup photo, "Iniciar traslado", "Llegué
   al destino", the delivery photo. Each photo goes through the signed upload to storage; the Worker validates it and
   the PWA finalizes it. The queue sends itself; the test presses "Sincronizar ahora" every 5 s while it waits.
5. Recipient: the link opens public tracking, which shows the order delivered: "Entregado".

The option's text says 'Entregada'. The public tracking label of `DELIVERED` is "Entregado" (AI-07,
`publicStatusLabels`); "Entregada" is the driver app's label. The suite asserts the shipped label and no label
changes.

At the end it checks the database: the order is `DELIVERED` with the tariff total in centavos, its status changes are
exactly CONFIRMED, READY_FOR_PICKUP, ASSIGNED, AT_PICKUP, PICKED_UP, IN_TRANSIT, DELIVERING, DELIVERED, it has a
PICKUP_PHOTO and a DELIVERY_PHOTO proof whose objects are in the bucket, two consumed upload sessions and the
assignment with its cost in centavos.

Every step has a screen; no step uses the API directly.

### Defect found (not fixed here)

An order whose packages have no measurements (optional in the wizard) cannot be assigned to any own driver: the
picker shows "No disponible: Los paquetes de la orden no tienen peso o medidas válidos". The order stores the missing
measurements as JSON nulls (`QuoteSnapshotToOrderCoordinator`), and the eligibility read treats a present null as an
invalid measurement (`PostgreSqlAssignmentToOrderCoordinator.TryReadDimensions`), while DSP-001 checks measurements
only "cuando existan". The journey types measurements (300 × 200 × 150 mm), as a dispatcher can. The fix belongs to
its own task.

## The platform checks (AI-08 OPS-005)

| Check | Where | How it is measured |
| --- | --- | --- |
| One browser action, one trace id | journey: "Asignar"; 429 test: "Calcular precio" | The browser sends exactly one write request for the click. Its audit rows (ASSIGNMENT_CREATED and ORDER_STATUS_CHANGED) share one `request_id`, a W3C trace id; every API log line of that request (found by its hosting request scope, not by the trace id) carries it, and no line of another request does. On an error, the `traceId` of the 429 problem is on every log line of its request, and that request wrote no audit row. |
| 429 and the approved text | `RateLimitEndToEndTests` | With `RateLimits:Quote:PermitLimit` 1 and `WindowSeconds` 45 (validated settings, this test only), the second "Calcular precio" answers 429 `application/problem+json` (`type`, `title` "Too Many Requests", `status`, `traceId`) with `Retry-After` and `Cache-Control: no-store`. The wizard shows "Demasiadas solicitudes. Espera N segundos e intenta de nuevo." with the server's N (or one second less, read right away) and "Calcular precio" is `aria-disabled`. 45 is below the panel's 60 s fallback, so N on screen can only come from the header. |
| Connections within the SCL-007 budget | journey | `ConnectionBudgetSampler` reads `pg_stat_activity` every 100 ms from the test's administration connection for the whole workflow (hosts' start included), grouped by runtime login. Each role's peak stays within its pool as the hosts composed it (10, 4, 10), each role is seen at least once, and the peak sum stays within `DatabasePoolOptions.RuntimeConnectionBudget` (24). |
| An outbox event after an idle period within AI-15 | journey: "Asignar" | Before the click the test waits until no row of either realtime lane is pending and the last one was created and settled more than 3 s ago (both lanes are then in their 1 s wait). The time runs from the click to the arrival of the assignment's `OrderStatusChanged` at a SignalR client of the operations hub (WebSockets, the dispatcher's identity); its `event_id` is the outbox row that click committed. Budget: `event_delivery_p95_seconds`, 2 s. |
| Nothing leaves the process with the export off | journey | Both hosts get `TelemetryExport__Enabled=false` and a connection string, as the pilot gives them (process environment, read while composing). Its ingestion endpoint is a loopback capture server. The test asserts both hosts read that string, built no tracer or meter provider and run no OpenTelemetry hosted service, that the capture received nothing, and that every real HTTP request of the test process during the workflow (seen by the runtime's `HttpHandlerDiagnosticListener`) went to loopback; the storage requests are among them, so the record is not empty. No tool needs a new image. |

Two instrument tests (`PlatformInstrumentTests`, uncategorized) prove against the real runtime that the outbound
recorder sees a real request of the process and that the API log reader gets the method, path and multipart content
type of a real Kestrel request. They run in the `dotnet` job, so the project always yields results there.

Local results (two consecutive runs, this machine): journey 20 s and 25 s, 429 test 5 s each; delivery after 3 s idle
725 ms and 651 ms; connection peaks app 4/10, realtime 4/4, worker 2/10, total 10/24; 3 log lines and 2 audit rows
per trace; Retry-After 45 shown as 45; 25 and 26 outgoing requests, all on loopback.

## CI

The suite runs inside the existing `driver-stops-pwa` job of Foundation CI and PR Validation; no job is added, so
Foundation keeps 13 jobs.

- Why this job: it already restores and builds the IntegrationTests harness, installs the frozen web dependencies and
  Chromium, starts the same storage container for the real offline pipeline, and holds the driver PWA the journey
  drives. `operations-dashboard` and `public-tracking` have no storage container; `delivery-simulation` has no web
  dependencies or browser.
- Steps added: "Restore locked platform end-to-end dependencies", "Build platform end-to-end suite" and "Execute
  platform end-to-end suite (OPS-005)" (`--filter "Category=PlatformEndToEnd"`, TRX `platform-end-to-end.trx`). The
  job's `collect-results` step adds that TRX as `tests/Paqueteria.EndToEndTests/Paqueteria.EndToEndTests.csproj|
  PlatformEndToEnd`, so REL-000 execution evidence of `driver-stops-pwa` carries the suite's results.
- The `dotnet` jobs (Foundation, PR Validation and the development push smoke) exclude `Category!=PlatformEndToEnd`.
- `tools/ci/change-domains.json`: `tests/Paqueteria.EndToEndTests/**` is its own domain (`TESTS_END_TO_END`) routed to
  `dotnet` and `driver-stops-pwa`, instead of every backend job.
- Tests: `tools/ci/test_platform_end_to_end_wiring.py` (the host job's steps, their order, the evidence spec run
  through rel000 `collect_execution_results`, the `dotnet` exclusions, the category constant) and
  `test_end_to_end_suite_runs_in_its_host_job` in `tools/ci/test_classify_changes.py`.
- Minutes added: see the PR (measured on the CI run).

A pull request that changes only operations or tracking web screens does not run `driver-stops-pwa` in PR Validation
(their domains route to `operations-dashboard` and `public-tracking`); the suite runs there on the next change that
routes to it and always on the promotion to `main`.

## Running it locally

Needs Docker (the suite pulls `postgis/postgis:18-3.6` and the `quay.io/ceph/demo` image on first use), Node.js with
pnpm, and Chromium for Playwright.

```bash
pnpm --dir apps/web install --frozen-lockfile
dotnet build tests/Paqueteria.EndToEndTests/Paqueteria.EndToEndTests.csproj --configuration Release
pwsh tests/Paqueteria.EndToEndTests/bin/Release/net10.0/playwright.ps1 install chromium   # or PLAYWRIGHT_BROWSERS_PATH
MSBUILDDISABLENODEREUSE=1 dotnet test tests/Paqueteria.EndToEndTests/Paqueteria.EndToEndTests.csproj \
  --configuration Release --no-build --filter "Category=PlatformEndToEnd" --logger "console;verbosity=normal"
```

The test output prints each measurement. `next start` rewrites nothing tracked: the Next.js fixture restores
`apps/web/next-env.d.ts`.

## Harness changes in IntegrationTests

- `Properties/AssemblyInfo.cs`: `InternalsVisibleTo("Paqueteria.EndToEndTests")`.
- `RealtimeKestrelWebApplicationFactory`: optional `corsExposedHeaders` for its test CORS policy. The pilot serves the
  web and the API on one origin, where a script reads every response header; the harness serves them on two, so the
  suite exposes `Retry-After`. Existing callers are unchanged.
- `DriverStopsNextServer.StartProductionAsync`: optional server-only runtime environment for `next start` (the order
  wizard's `PAQUETERIA_TERMS_VERSION` and `PAQUETERIA_PRIVACY_VERSION`); the build does not depend on it.
- `CorsCapableS3Fixture`: `CEPH_ARGS=--mon_data_avail_crit=1`. The storage monitor refuses to start when the host disk
  has less than 5 % free, which a large developer disk misses with gigabytes free; the test cluster stores a few
  objects.

## Rollback

Revert the pull request. The suite adds no product code, SQL, migration, role, grant, secret, Bicep or package; the
harness options it adds default to the previous behaviour.
