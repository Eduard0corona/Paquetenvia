# SEC-004 PR 3: limits on the heavy endpoints (server, contracts and driver app)

Phase 4 PR 3 (`docs/development/phase4-platform-plan.md`, "PR 3: límites en los endpoints pesados"), backlog item
**SEC-004** in `AI-08_BACKLOG.yaml`. It applies the decided list on top of the PR 2 plumbing
(`docs/development/sec-004-rate-limit-plumbing.md`).

PR 3 ships in two pull requests. This one contains:

- the policies and their configuration;
- the contracts: AI-05, the ADR-OBS-001 amendment, AI-08, and AI-07 for what ships here;
- the sign-in page;
- the per-minute log summary;
- the driver app's wait.

The panel part shipped in its own pull request (`docs/development/sec-004-panel-limits.md`,
`SEC-004-PANEL-SCREENS-2026-10-13`):

- the message and the countdown on each limited screen, and their AI-07 entries (owner answers Q6 and Q11 to Q16);
- the realtime reload spacing (owner answer Q9): the panel gathers realtime notices and reloads at most once every
  3 seconds per tab. The 60 per minute limits do not change.

The driver app is in this one because, before it, the app classified a 429 on a photo step as an invalid answer, and
the sync scheduler turned that into rejected evidence: the photo held for the driver's attention. The photo limits
cannot ship without it.

Before the panel part, a panel screen that met a limit showed its existing error for an unanswered request (for example
"Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.") instead of the
approved text.

## Owner decisions

Decision rows in `decision-log.md`, with the literal questions, answers and options:

| Row | Question | Answer |
| --- | --- | --- |
| `SEC-004-RATE-LIMITS-2026-10-12` | Which endpoints are limited | "Lista corta y generosa (Recommended)": "Por usuario: CSV 10/min (1 importación a la vez), exportar liquidación 10/min, crear dirección (cuesta Google) 30/min, fotos 30–60/min, tablero y conteos 60/min, lista de órdenes 120/min, cotizar 60/min, repartidores asignables 60/min; inicio de sesión 20/min por red. Se ajusta tras 2 semanas con métricas." |
| `SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12` | Q1 CSV | "Compartido, 1 a la vez (Recommended)" |
| | Q2 fotos | "60 cada paso (Recommended)" |
| | Q3 tablero y conteos | "Uno cada uno (Recommended)" |
| | Q5 inicio de sesión | "Solo /auth/login (Recommended)" |
| | Q6 texto ante un 429 | "Mensaje común con espera (Recommended)" |
| `OBS-001-DASHBOARD-429-2026-10-12` | Q4 dashboard 429 | "Enmendar ADR-OBS-001 (Recommended)" |
| `SEC-004-RETRY-AFTER-2026-10-13` | Q7 `Retry-After` on public tracking | "Sí, agregarlo (Recommended)" (shipped by #251; AI-05 declares the header here) |
| `SEC-004-REJECTION-SUMMARY-2026-10-13` | Q8 visibility before PR 6 | "Resumen en los logs (Recommended)" |
| `SEC-004-RELOADS-AND-BULK-2026-10-13` | Q9 dashboard and counts against realtime reloads | "Espaciar recargas (Recommended)": "El panel junta los avisos y recarga como máximo cada 3 segundos por pestaña. Los límites de 60/min no cambian. Los cambios se ven hasta 3 segundos después. Va en el PR de pantallas del panel." |
| | Q10 assignable drivers in bulk assignment | "Dejarlo en 60/min (Recommended)": "En el piloto, tres lotes de 20 en un minuto es raro. Si pasa, el panel pide esperar unos segundos. Se revisa con las métricas en 2 semanas." |

Q9 and Q10 were asked after the review of #252: the Q3 premise ("se refrescan cada 30 segundos") was incomplete,
because the dashboard and the inbox also reload on every realtime notice and every reload also reads the counts.

## Policies

Per user means the hash of the authenticated subject (`SubjectPartition`); a request without a subject spends its
client network's budget, as in PR 2 (the limiter runs before authorization). Every window is fixed, 60 s, with no
queue and automatic replenishment, like the PR 2 policies. Numbers are `RateLimits:<Setting>:PermitLimit` and
`WindowSeconds` in the API's `appsettings.json`, with the same numbers as code defaults.

| Policy | Operations | Limit | Setting | Partition |
| --- | --- | --- | --- | --- |
| `Orders.CsvImport` | `previewOrderCsv` (`POST /api/v1/orders/csv/preview`) | 10 per minute, shared with the commit | `CsvImport` | user |
| `Orders.CsvImportCommit` | `commitOrderCsv` (`POST /api/v1/orders/csv/commit`) | spends the `Orders.CsvImport` window; one commit in flight per user | `CsvImport` | user |
| `Finance.SettlementExport` | `exportSettlementCsv` | 10 per minute | `SettlementExport` | user |
| `Locations.Create` | `createLocation` | 30 per minute | `LocationCreate` | user |
| `Custody.ProofUploadSession` | `createProofUploadSession` | 60 per minute | `ProofUploadSession` | user (the driver) |
| `Custody.ProofFinalize` | `finalizeProof` (`POST /api/v1/orders/{orderId}/proofs`) | 60 per minute | `ProofFinalize` | user (the driver) |
| `Reporting.OperationsDashboard` | `GET /api/v1/operations/dashboard` (outside AI-05) | 60 per minute | `OperationsDashboard` | user |
| `Reporting.QueueCounts` | `getOperationsQueueCounts` | 60 per minute | `QueueCounts` | user |
| `Orders.List` | `listOrders` | 120 per minute | `OrderList` | user |
| `Pricing.Quote` | `createQuote` | 60 per minute | `Quote` | user |
| `Dispatch.AssignableDrivers` | `listAssignableDrivers` | 60 per minute | `AssignableDrivers` | user |
| `Identity.SignIn` | `startAuthCenterSignIn` (`GET /auth/login`) | 20 per minute | `SignIn` | client network (IPv4 address or IPv6 /64) |

The PR 2 policies keep their numbers and their own settings: `PublicTrackingLookup` 60/min per network,
`Realtime.Connections` 20/min, `Drivers.LocationIngestion` 30/min. Recipient calls keep their database limit.

Not limited: `/signin-authcenter` (Q5), `GET /api/v1/orders/{orderId}/proofs` (listing photos is not finalizing
one), and every other operation.

### How the CSV rules work

ASP.NET Core gives each policy its own partitions, so two policies cannot share a window by themselves. The registry
now keeps that state in `RateLimitBudgets` (`src/BuildingBlocks/Paqueteria.Infrastructure/Security/RateLimiting/`):

- one partitioned fixed window for `Orders.CsvImport`, which both policies spend (`SharesWindowOf`);
- one partitioned concurrency limiter, one permit per user, for `Orders.CsvImportCommit` (`InFlightLimit = 1`).

For a commit, the limiter first takes the in-flight permit, then a window permit. The in-flight permit is held until
the commit has answered: the framework releases it when the request leaves the pipeline.

- **Another commit of the same user while one runs.** It is rejected under `Orders.CsvImportCommit`. It spends
  nothing from the window. Its `Retry-After` is the CSV window (60 s): an upper bound, since the web gives a commit at
  most 60 s.
- **The shared window is used up.** The 429 is counted under `Orders.CsvImport`, from either endpoint, with the
  window's own `Retry-After`.

A rejected lease names the policy that rejected it (`RateLimitLeaseMetadata.RejectedBy`), and the writer counts that
name. The registry refuses:

- a policy that shares an unregistered window, its own window or a window that is itself shared;
- a shared window with another partition kind or another `Limit` delegate;
- an in-flight limit below 1.

The other new policies, like the PR 2 ones, keep the framework's own fixed window partitions.

### Configuration

`ApiRateLimitOptions` (`src/Paqueteria.Api/Http/ApiRateLimitPolicies.cs`) is bound from `RateLimits` and validated at
start. `PermitLimit` must be 1 to 10000 and `WindowSeconds` 1 to 3600, the bounds of `Drivers:LocationIngestion`. Any
other value, or text that is not a number, stops the API at start with the setting's name. A missing section keeps
the owner's numbers. "Se ajusta tras 2 semanas con métricas": a later owner decision re-sets them in `appsettings.json`
or through `RateLimits__<Setting>__PermitLimit` environment variables. The deploy templates set none of these keys. The
one-commit rule is a rule, not a rate, so it is a constant (`ApiRateLimitPolicies.CsvCommitsInFlight`).

## The 429s

| Where | Answer |
| --- | --- |
| Every new `/api/v1` policy and the dashboard | AI-05 `TooManyRequests`: `application/problem+json` `{"type":"about:blank","title":"Too Many Requests","status":429,"traceId":"<32 hex>"}`, `Retry-After` (whole seconds, the policy's window: 60), `Cache-Control: no-store` |
| `GET /auth/login`, `Accept` naming `text/html` with q > 0 (a browser navigation) | `text/html; charset=utf-8`: a page whose only text is "Demasiados intentos de inicio de sesión desde esta red. Espera un minuto e intenta de nuevo." (its first sentence is the `<title>`), with `Retry-After`, `Cache-Control: no-store`, `Content-Security-Policy: default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'` and `X-Content-Type-Options: nosniff` |
| `GET /auth/login`, any other `Accept` (`*/*` alone included) | the `TooManyRequests` problem above |

The page has no script, style, image, link, form or branding (`RateLimitRejectionPage` in
`Paqueteria.Application.Security`; the text is `AuthCenterDefaults.SignInRateLimitMessage`). The AuthCenter BFF answers
its own navigation failures with redirects to `/login?error=...`. This 429 is a page of its own because the owner chose
"una página simple", and adding an error code to the web's login screen would be web text that was not decided.

## Contracts

- **AI-05.**
  - The ten `/api/v1` operations above declare `'429': TooManyRequests`.
  - `startAuthCenterSignIn` declares its 429 with both bodies.
  - The CSV descriptions state the shared budget and the one-commit rule.
  - `TooManyRequests` declares `Retry-After`: required, an integer of at least 1 (Q7).
  - `x-pilot-contract-deltas` gains `SEC-004-HEAVY-ENDPOINT-LIMITS`.
  - Paths and schemas stay 69 and 125; internal references go from 651 to 662. The AI-05 hash is re-pinned.
- **ADR-OBS-001** gains "Enmienda 2026-10-13: límite de tasa del tablero y su 429". It states the policy, the problem
  shape, and a line that lists the dashboard's error responses (`400`, `401`, `403`, `429`, `503`).
  `HttpSurfaceOpenApiCoverageTests.KnownUndeclaredOperations` now maps the dashboard to that ADR. The error test reads
  that line and requires every status the endpoint emits to be on it, so the dashboard is no longer skipped.
- **AI-08 SEC-004.** The three pending owner decisions of PR 3 are recorded as decided, with Q7 and Q8.
- **AI-07.** `screen_contracts.driver_stop.rate_limit` (the driver queue's wait) and the `/login` route (the sign-in
  page) record what ships here. The panel screens' entries come with the panel part.

## Rejection summary (OBS-002, owner answer Q8)

`RateLimitSummaryReporter` (`src/BuildingBlocks/Paqueteria.Infrastructure/Observability/RateLimitRejectionSummary.cs`)
follows `HttpStatusReporter`:

- Once a minute it writes event **4604 `RateLimitSummary`** at Information, one line per policy that rejected at least
  one request in the window: `Rate limit {Policy} summary: rejected={Rejected} window_s={WindowSeconds}`.
- **A minute without rejections writes nothing**, as `HttpStatusSummary` writes nothing for an empty window (the
  convention chosen). The final flush at shutdown writes `window_s=0`, like the HTTP summary.
- `Policy` is the registry's fixed name and `Rejected` the count. Both are now in `TelemetryDimensions.Allowed`.
  `PolicyName` would fail the forbidden-fragment rule.

How `Policy` stays a registry name:

- The counts come from the one rejection writer, which already counted by policy name.
- `RateLimitRejectionSummary` is built only by the registry, from its own names. It refuses any other name.
- The architecture tests pin both, and that the reporter logs only what the summary holds. No partition key, subject,
  hash or address can reach it.

No alert, workbook or deployment guard reads 4604, `Policy` or `Rejected` (`observability.bicep` and guard P21 are
unchanged; an architecture test checks it). They wait for a later owner decision. In Log Analytics:

```kql
ContainerAppConsoleLogs_CL
| extend e = parse_json(Log_s)
| where toint(e.EventId) == 4604
| summarize Rejected = sum(tolong(e.State.Rejected)) by Policy = tostring(e.State.Policy), bin(TimeGenerated, 1h)
```

Cost: nothing while no limit rejects. The worst case is 15 policies rejecting every minute: about 21,600 short lines a
day, a few MB, inside the pilot's 0.3 GB/day log cap.

## Driver app (owner answers Q2 and Q6, this PR)

"Si se pasa, la app espera lo que indique el servidor y sigue sola" (Q2); "la cola espera lo que indique el servidor y
reintenta sin mensaje" (Q6). In `apps/web/src/driver/offline/`:

- `driver-sync-api.ts` reads a 429 on `createProofUploadSession` or `finalizeProof` as `rate-limited`, with the wait from
  `Retry-After` (`src/lib/retry-after.ts`: whole seconds, at least 1, capped at one hour; anything else is no wait).
  Before, it was `invalid-contract`, which the scheduler held as `EVIDENCE_REJECTED`.
- `driver-sync-scheduler.ts` keeps the action `RETRY_WAIT` with its keys and its progress (a granted session, an
  uploaded photo) and no `safeError`, so the driver sees no message.
  - Nothing of the queue is sent until the wait has passed. The limits are per driver, so another order's photo
    would only meet the same 429.
  - "Sincronizar ahora" does not send during the wait either.
  - A timer resumes the queue when the wait ends.
  - Without a usable `Retry-After`, it waits the existing bounded backoff (1 s, 2 s, 5 s, 10 s, 30 s).
  - The wait is held in memory. After a reload, the limited action still waits its own `nextAttemptAt`.
- AI-07 records it in `screen_contracts.driver_stop.rate_limit`, and the sign-in page in the `/login` route.

## Screens that call a limited operation

Owner answer Q6, "Mensaje común con espera":

- **Panel** (the panel part, `docs/development/sec-004-panel-limits.md`). "Demasiadas solicitudes. Espera N segundos e
  intenta de nuevo.", with N from `Retry-After`. The button re-enables itself when N runs out; the screens that refresh
  themselves reload once when it does (Q11).
- **Driver app** (this PR). The queue waits what the server says and retries without a message.
- **`/auth/login`** (this PR). The page above.

| Screen | Operations |
| --- | --- |
| `/ops/orders/import` | `previewOrderCsv`, `commitOrderCsv` |
| `/finance/settlements` | `exportSettlementCsv` |
| `/ops/orders/new` | `createQuote` |
| `/ops/dashboard`, `/ops/inbox`, the order detail | dashboard, `getOperationsQueueCounts` |
| `/ops/orders`, `/finance/cod` (pending COD list) | `listOrders` |
| order detail and inbox (driver picker, "Asignar seleccionadas…") | `listAssignableDrivers` |
| top bar "Buscar guía", `/ops/routes` "Agregar entrega" (owner answer Q13) | `listOrders` |
| driver app offline queue | `createProofUploadSession`, `finalizeProof` |

No web screen calls `createLocation` today.

## Choices not written in the task

- **Policy names.** `Orders.CsvImport`, `Orders.CsvImportCommit`, `Finance.SettlementExport`, `Locations.Create`,
  `Custody.ProofUploadSession`, `Custody.ProofFinalize`, `Reporting.OperationsDashboard`, `Reporting.QueueCounts`,
  `Orders.List`, `Pricing.Quote`, `Dispatch.AssignableDrivers` and `Identity.SignIn`, after `Drivers.LocationIngestion`
  and `Realtime.Connections`. Each module constant is `<Class>.<Name>RateLimitPolicy`. The architecture test now
  accepts a prefix before `RateLimitPolicy`, since a module can own several policies.
- **One configuration section in the host.** The PR 2 numbers live in each module's options. The decided list lives in
  one host section, `RateLimits`, so the two-week re-tuning is one place. Modules only own the policy names.
- **The one-commit rule's rejection.** It is counted under its own name, `Orders.CsvImportCommit`. Its `Retry-After`
  is the CSV window (60 s), and it spends nothing from the shared window. A replay of the same `Idempotency-Key` while
  the first commit runs is also 429 (the limiter runs before idempotency), and can be sent again afterwards.
- **The in-flight rule is not configurable** (`CsvCommitsInFlight = 1`): the owner's "1 a la vez" is a rule, not a
  rate.
- **The window type.** A fixed window, `QueueLimit` 0, as asked; no reason to change it was found. The CSV budget
  uses the same fixed window options (`RateLimitingServiceCollectionExtensions.FixedWindow`) inside the registry's
  partitioned limiter.
- **The sign-in page.**
  - Spanish (`lang="es-MX"`, like the web), the approved text in one paragraph, its first sentence as the title.
  - A strict CSP and `nosniff`.
  - No trace id, branding or link. A link back to `/login` or branding would need an owner decision.
  - HTML only when `Accept` names `text/html`; `*/*` alone gets the problem.
- **The "un minuto" text** matches the default 60 s window. If a later decision changes `RateLimits:SignIn:WindowSeconds`,
  the text needs the owner too. `Retry-After` always says the real wait.
- **`ProducesProblem(429)`** is added to each limited endpoint's metadata, as on GPS ingestion.
- **The log summary** writes one line per policy per minute, and nothing for an empty window. It is written at
  Information: a rejection is expected behavior, not a warning.
- **The pilot guard's state-property list** (`tools/azr-001/env001_pilot_guards.py`) is unchanged. It lists what the
  alert queries may read, and they must not read `Policy` or `Rejected` yet.
- **The split.** The driver app's wait ships with the server limits, not with the panel texts (see the top of this
  note).
- **The driver queue waits as a whole** after a photo 429, and "Sincronizar ahora" waits too. The owner's text is "la
  cola espera"; the limits are per driver, so sending another order's photo would only meet the same 429.
- **`Retry-After` is capped at one hour on the web** (`maximumRetryAfterSeconds`), so a broken header cannot park the
  queue. The API never sends more than its window (60 s).

## Tests

- `tests/Paqueteria.IntegrationTests/Security/Sec004HeavyEndpointLimitsTests.cs` (assembled API, no database):
  - each new `/api/v1` policy, the dashboard included: 429 `problem+json` with `traceId`, `Retry-After: 60` and
    `no-store` past its limit. The same user from another network is limited too; another user is not; the rejections
    are counted under the policy;
  - separate budgets for the dashboard and the counts (Q3), and for the two photo steps (Q2); listing photos is not
    limited;
  - the shared CSV budget across preview and commit (Q1), counted under `Orders.CsvImport`;
  - one commit at a time (Q1). A first commit is held in flight; a second of the same user is 429 under
    `Orders.CsvImportCommit`. A preview of the same user and another user's commit pass. After the first answers, the
    user commits again;
  - admitted again after the window (AI-08 SEC-004 `required_tests[0]`), with `WindowSeconds` 1:
    - the shared CSV window (the registry's budget limiter): a preview spends it, the commit is 429 with
      `Retry-After: 1`, and once the window has passed the commit and then the preview are admitted again;
    - a plain per-user window (`Orders.List`): 429 with `Retry-After: 1`, then admitted again.

    Each test waits the `Retry-After`, then retries every 100 ms for up to 5 s, and fails if the limit never admits
    the request again. It repeats the first pair if a window boundary falls between its two requests;
  - sign-in (AuthCenter host):
    - 20 per minute per network, here 2;
    - a navigation gets the exact page with its headers;
    - a JSON client gets the problem;
    - another network passes;
    - the callback is never limited;
    - only `/auth/login` has the page;
  - an invalid number (0, above the maximum, negative, not a number) stops the API at start, naming the setting;
  - the deployed numbers and partitions of the twelve policies, and the code defaults;
  - the summary: one 4604 line per rejecting policy, in name order, with `Policy`, `Rejected` and `WindowSeconds` only.
    The policy names are the registry's; no log line has the subject, its hash, the addresses, their networks or their
    keys; an empty window writes nothing.
- `tests/Paqueteria.UnitTests/Security/Sec004HeavyEndpointLimitTests.cs`:
  - the registry's refusals;
  - the shared window and its rejection metadata;
  - the in-flight rule, its release and idle reporting;
  - a refused commit spends nothing;
  - the writer counting and waiting by the rejecting policy, and refusing an unknown one;
  - HTML or problem by `Accept`;
  - the page's content and escaping;
  - the summary's refusals, lines and reset.
- `tests/Paqueteria.ContractTests/Sec004RateLimitContractTests.cs`:
  - exactly the limited operations declare 429, the callback does not;
  - `TooManyRequests` declares `Retry-After`;
  - the sign-in 429's two bodies;
  - the pilot delta.
- `HttpSurfaceOpenApiCoverageTests`: the dashboard's statuses are checked against ADR-OBS-001, and at least 13
  rate-limited `/api/v1` operations are seen.
- Architecture:
  - 17 `RequireRateLimiting` calls, each naming a module constant in the policy table;
  - the summary built only by the registry, from its names;
  - the writer records the counted policy;
  - 4604 and its two dimensions reach no alert or workbook;
  - the new reporter is reviewed in the root-trace test.
- Web (vitest):
  - `src/lib/retry-after.test.ts`: whole seconds, the cap, and everything else ignored;
  - `src/driver/offline/driver-sync-api.test.ts`: a 429 on either photo step is `rate-limited` with the server's wait;
  - `src/driver/offline/driver-sync-scheduler.test.ts`:
    - one 429 and the whole queue waits the `Retry-After`, without a message or a rejected photo, "Sincronizar ahora"
      included;
    - the queue resumes by itself with the same keys;
    - a finalize 429 keeps the granted session and the uploaded photo;
    - no `Retry-After` means the bounded backoff;
  - `src/driver/offline/rate-limit-policy.test.ts`: AI-07, AI-05 and the code agree.
- Browser: `DriverOfflineOperationsPwaPlaywrightTests.Photo_429_waits_the_server_and_syncs_by_itself_without_a_message`.
  The first upload session answers 429 with `Retry-After: 2`; the photo waits with no attention notice, then syncs by
  itself with the same key, one upload and one finalize.
- Changed existing tests:
  - `CsvOrderImportHttpTests`: the concurrent commits of one batch key come from four colleagues, since one user's
    second commit now waits (owner answer Q1);
  - `OrderHttpWebApplicationFactory` and `LocationHttpWebApplicationFactory` raise `RateLimits:CsvImport` and
    `RateLimits:LocationCreate`: their classes send more than a minute's budget as one mock user, and they test the
    operations, not the limits.
- Updated pins:
  - OpenAPI internal references (662) and the AI-05 hash;
  - the response lists of `getOperationsQueueCounts`, `listAssignableDrivers` and the CSV operations;
  - PR 2's "existing limits keep their numbers" (now over its three policies);
  - PR 2's "every policy is used" (sign-in is mapped only with AuthCenter).

## Residual risks

- **Bulk assignment spends the driver-list budget.** `bulk-assignment-controller.ts` reads `listAssignableDrivers`
  once per order of the batch: up to `bulkSelectionLimit` = 20 orders, each up to `maximumDriverPagesPerOrder` = 10
  pages of 100 drivers. One 20-order bulk preview spends at least 20 of the 60 per minute of
  `Dispatch.AssignableDrivers`. It stays at 60 per minute by the owner's decision (Q10, "Dejarlo en 60/min"), and is
  reviewed with the metrics in 2 weeks.
- **Realtime refreshes spend the dashboard and counts budgets.** Besides the 30 s polling:
  - the dashboard and the inbox reload on every realtime order event (`OrderStatusChanged`,
    `OrderTimelineEventAdded`, `AssignmentChanged`, `RouteChanged`, `IncidentCreated`, `ExternalOfferChanged`), with a
    250 ms trailing debounce;
  - each of those reloads also reads `getOperationsQueueCounts`;
  - the dashboard also reloads its list (not the counts) on `DriverLocationUpdated`, with 500 ms;
  - an inbox reload reads one dashboard page per status of its queue (up to 4, "Requiere atención");
  - the order detail reloads its dashboard row on the same events;
  - the budgets are per user, so every tab the user has open spends the same 60 per minute.

  A burst of events (a bulk assignment, a CSV commit) could therefore spend the 60 per minute of
  `Reporting.OperationsDashboard` and `Reporting.QueueCounts`. The owner's decision (Q9, "Espaciar recargas") mitigates
  it in the panel part: at most one reload every 3 seconds per tab, with the limits unchanged
  (`docs/development/sec-004-panel-limits.md`). Several tabs of one user still share the per-user budgets.
- **Forward deploy of the driver app.** A phone still running the previous web build reads a photo 429 as rejected
  evidence: the action needs attention (`EVIDENCE_REJECTED`) and the photo stays on the phone. That lasts until the
  phone loads the new build:
  - How the build reaches drivers: the service worker (`apps/web/public/sw.js`, unchanged here) serves `/driver/stops`
    network first, and the new build's scripts have new hashed names. The next time the app is opened or reloaded
    with network, it runs the new build. No service-worker update is involved.
  - What stays old: a page left open keeps the old scripts until it is reloaded. Opening the app offline serves the
    shell of the build last loaded online.
  - What the operator does: after the deploy, ask drivers to close and reopen the app (or reload it) with network
    before their next delivery. A driver who already sees a rejected photo waits a minute and retries it from the
    notice. "Descartar" deletes the photo from the phone.
- **Sign-in per network behind shared addresses.** The owner chose per client network (Q5): one IPv4 address, or one
  IPv6 /64. Everyone behind one office NAT or one carrier-grade NAT address of a mobile network shares 20 sign-in
  starts per minute:
  - a shift start can lock out legitimate users for up to a minute;
  - one user, or a script, on a shared address can block the others.

  Event 4604 shows it as rejections under `Identity.SignIn`. The remedy is `RateLimits:SignIn:PermitLimit`, after an
  owner decision.
- **The client address depends on the routing.** The chosen pilot routing gives the API the real client address:
  Container Apps rule-based routing, one Envoy hop, `KnownNetworks` `10.60.0.0/23` and `ForwardLimit=1`.
  - The documented fallback, Next.js rewrites (`PAQUETENVIA_API_PROXY_ORIGIN`), adds a hop: Web reaches the API through
    the API's internal ingress, which adds Web's address to `X-Forwarded-For`. Next.js's rewrite proxy forwards the
    header unchanged.
  - With `ForwardLimit=1`, the API would take that subnet address for every client, and every sign-in would share one
    bucket.
  - The fallback needs `Http__ForwardedHeaders__ForwardLimit=2`. The middleware stops at the first address outside
    `KnownNetworks`, so a client cannot pass a forged address with it.
  - `docs/operations/env-001-pilot/README.md` has the note next to the fallback row, and a post-deploy check (§6.1 step
    11) that the API sees a client address outside the subnet.
- **Metric names differ for a shared-window rejection.** When the shared CSV window rejects a commit:
  - event 4604 and the `rate_limit.rejected` counter count it under `Orders.CsvImport`, the window's owner (the
    lease's `RejectedBy` metadata);
  - ASP.NET Core's `aspnetcore.rate_limiting.requests` tags it with the endpoint's policy, `Orders.CsvImportCommit`
    (`aspnetcore.rate_limiting.result=endpoint_limiter`).

  A one-commit-at-a-time rejection is `Orders.CsvImportCommit` in both. For PR 6 (Application Insights): 4604 says
  which budget ran out, the framework metric which endpoint refused. Adding the two counts a rejection twice.
- **CORS.** The REST CORS policy (`Http:Cors`) exposes no response headers, so a cross-origin client could not read
  `Retry-After`. The pilot is same-origin (`Http:Cors:AllowedOrigins` is empty), so this does not apply.

## Verify locally

```bash
dotnet build Paqueteria.sln -c Release
dotnet test tests/Paqueteria.UnitTests -c Release --no-build --filter "FullyQualifiedName~Sec004|FullyQualifiedName~Obs002"
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "FullyQualifiedName!~PostgreSql"
dotnet test tests/Paqueteria.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Sec004|FullyQualifiedName~HttpSurfaceOpenApiCoverage"
python3 docs/normative/v0.6/tools/validate_contracts.py
cd apps/web && pnpm test && pnpm typecheck && pnpm lint
```

## Rollback

- Revert the commit. There is no schema, migration, role, grant or package change. Limiter state and the summary
  counts are in memory, so nothing is left behind.
- Without a revert, a limit can be relaxed by raising `RateLimits:<Setting>:PermitLimit`, up to 10000, and restarting
  the API.
- After a revert:
  - the ten operations and `/auth/login` are unlimited again;
  - AI-05 loses their 429 and the `Retry-After` declaration, though every remaining 429 still sends the header (PR 2);
  - the dashboard goes back to being skipped by the coverage test;
  - event 4604 stops;
  - the driver app takes a photo 429 for rejected evidence again. That is harmless once the photo limits are gone, but
    the web must not be rolled back alone while the API keeps them.
- Actions a phone queued while waiting keep their normal shape: a `RETRY_WAIT` with no `safeError`. An older web build
  reads them as before.
