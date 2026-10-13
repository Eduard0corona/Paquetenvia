# SEC-004 PR 3, panel part: the panel screens under the heavy-endpoint limits

Phase 4 PR 3 (`docs/development/phase4-platform-plan.md`), backlog item **SEC-004** in `AI-08_BACKLOG.yaml`. The server
limits, the contracts, the sign-in page, the log summary and the driver app's wait shipped in #252
(`docs/development/sec-004-heavy-endpoint-limits.md`). This pull request is the panel part: what every panel screen does
when it meets one of those limits, and the realtime reload spacing. No limit number, API contract, schema, role or
package changes.

## Owner decisions

Literal questions, answers and the options not chosen are in `decision-log.md`.

| Row | Question | Answer |
| --- | --- | --- |
| `SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12` | Q6 text on a 429 | "Mensaje común con espera (Recommended)": "En el panel: "Demasiadas solicitudes. Espera N segundos e intenta de nuevo.", y el botón se reactiva solo." |
| `SEC-004-RELOADS-AND-BULK-2026-10-13` | Q9 realtime reloads | "Espaciar recargas (Recommended)": at most one reload every 3 seconds per tab |
| | Q10 bulk assignment | "Dejarlo en 60/min (Recommended)": "Si pasa, el panel pide esperar unos segundos." |
| `SEC-004-PANEL-SCREENS-2026-10-13` | Q11 screens that refresh themselves | "Reintenta sola (Recommended)": automatic reloads pause; at 0 the screen reloads once by itself |
| | Q12 one second | ""1 segundo" (Recommended)": "Espera 1 segundo e intenta de nuevo." |
| | Q13 "Buscar guía" and "Agregar entrega" | "Mensaje común (Recommended)" |
| | Q14 buttons of the screens that refresh themselves | "Se pausan (Recommended)": "Actualizar" and "Cargar más" disabled; a filter change is kept; Órdenes does not reload at 0 when more pages were loaded; Cobros pendientes reloads once at 0 |
| | Q15 driver picker | "La pide sola (Recommended)": the first list is asked for once by itself at 0 |
| | Q16 only the counts | "Toda la pantalla (Recommended)": one message, the whole screen pauses |

Technical note shown to the owner with Q11 and Q12, without a question: when the server sends no usable `Retry-After`
(today it always does), the panel waits 60 seconds, the window of every panel limit.

AI-07 records it as `panel_rate_limit` (the common rules and the screen list) and a `rate_limit` entry in each limited
screen: `/ops/dashboard`, `work_inbox`, `/ops/orders/:id` and its `assignment_picker`, `route_planner`, `create_order`,
`csv_order_import`, `cod_control`, `order_list` and `settlement_desk`.

## The message

- One text, from one helper (`rateLimitMessage` in `apps/web/src/lib/rate-limit-wait.ts`): "Demasiadas solicitudes.
  Espera N segundos e intenta de nuevo.", and "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo." at one.
- N comes from `Retry-After` (whole seconds, read by the driver app's `readRetryAfterSeconds`, capped at one hour). A
  missing header, or one that is not a positive whole number, gives 60.
- N counts down once per second, on the second, and the message disappears at 0.
- Accessibility: the visible countdown (`RateLimitNoticeView`) is `role="timer"`, which assistive technology reads on
  request but never announces by itself. The application shell holds one visually hidden `role="status"`
  `aria-live="polite"` region (`RateLimitAnnouncer`) that says the message once per wait, with the seconds the server
  asked for, and empties when the wait ends. A page that shows the countdown in two places therefore announces it once.

## The waits

`RateLimitWaits` (same file) keeps one wait per limited operation, in the tab's memory:

| Wait | Operation (policy) | Screens |
| --- | --- | --- |
| `csv_import` | `previewOrderCsv` and `commitOrderCsv` (one budget, Q1) | `/ops/orders/import` |
| `settlement_export` | `exportSettlementCsv` | `/finance/settlements` |
| `quote` | `createQuote` | `/ops/orders/new` |
| `order_list` | `listOrders` | `/ops/orders`, `/finance/cod`, "Buscar guía" (top bar), "Agregar entrega" (`/ops/routes`) |
| `assignable_drivers` | `listAssignableDrivers` | the driver picker (order detail, inbox), "Asignar seleccionadas…" |
| `dashboard` | operations dashboard | `/ops/dashboard`, `/ops/inbox`, `/ops/orders/:id` |
| `queue_counts` | `getOperationsQueueCounts` | `/ops/dashboard`, `/ops/inbox` |

- The API clients record the wait when a 429 arrives (`createTenantRequester` with the request's `rateLimit`, and
  `createOperationsApi`), and throw `rate_limited` with `retryAfterSeconds`.
- Only a request tagged with a limited operation reads a 429 as `rate_limited`. Every other operation keeps reading
  it as `unavailable`, as before SEC-004. A screen therefore never waits for a limit it has no wait for. `ScreenReloads`
  also ignores a `rate_limited` read when no wait runs for the screen. Either way, a 429 from an untagged operation
  can never make a screen reload in a loop. Examples: `getOrder` on the order detail, `organizationContexts` at the
  dashboard's start, `transitionOrder` in the wizard's confirmation.
- A screen waits for the operations it calls. A wait never holds an unrelated limit; every screen of the tab that calls
  the same operation waits with it (the server would only answer the same 429). An organization switch keeps the
  waits, because the limits are per user. Another tab has its own.
- A screen that reads two operations (dashboard and inbox) waits for either: one message, the whole screen (Q16).

## Screens

### Screens that send (Q6, Q13)

| Screen | Buttons held during the wait | Kept |
| --- | --- | --- |
| `/ops/orders/import` | "Previsualizar" and "Confirmar" (one budget) | the file, the report, the prohibited-goods tick, the batch key |
| `/finance/settlements` | "Exportar CSV" (the other actions are not limited) | the selected settlement |
| `/ops/orders/new` | "Calcular precio" (`aria-disabled`, like the other wizard buttons) | the draft, the quote key |
| top bar | "Buscar" (`aria-disabled`) | the typed guía |
| `/ops/routes` | "Agregar entrega" | the typed guía |

- The common message replaces the screen's own failure text for a 429.
- Nothing is sent again by itself.
- **Idempotency keys.** A 429 is a rejection before the handler. The panel keeps its existing rule for "the request
  never reached the handler / no valid answer": `TenantApiError.retryable` is true for `rate_limited`, as for `network`
  and `unavailable`. So a CSV commit pressed again sends the same batch key (it follows the previewed digest), and a
  quote pressed again with the same shipment sends the same key. A changed shipment or a new file gets a new key, as
  before. The export and the reads have no key. "Agregar entrega" keeps its rule: every change has a fresh key, and the
  429 can only come from the guía lookup, before any change.

### Screens that refresh themselves (Q11, Q14, Q16)

`ScreenReloads` (`apps/web/src/operations/state/screen-reloads.ts`) is the one piece every such screen uses:

- `admit()` refuses every read during the wait: 30 s polling, realtime, the page becoming visible or focused, the read
  after an action, "Actualizar", "Cargar más" and a filter change. It keeps one reload instead.
- `rateLimited()` keeps one reload after a read that met the 429.
- When the wait ends the screen reloads once, by itself, with what is on screen then (its filters included).

| Screen | Waits | Held | At 0 |
| --- | --- | --- | --- |
| `/ops/dashboard` | `dashboard`, `queue_counts` | polling, realtime, visibility, the read after "Publicar oferta externa"; "Actualizar", "Cargar más"; a filter change is kept | the list and the counts, once; a session start that met the wait starts once, realtime included, reading the filters on screen |
| `/ops/inbox` | `dashboard`, `queue_counts` | polling, realtime, visibility, the read after an assignment; "Actualizar", "Cargar más"; a queue or chip change is kept | the queue (with the view on screen) and the counts, once |
| `/ops/orders/:id` | `dashboard` | polling, realtime, visibility, the read after a change below; "Actualizar" | the order, once |
| `/ops/orders` | `order_list` | visibility and focus; "Actualizar", "Cargar más"; a status, period or guía change is kept (its rows go at once, as before) | the first page once, unless "Cargar más" loaded further pages: then nothing is read and only the buttons re-enable |
| `/finance/cod` | `order_list` | "Actualizar lista", "Cargar más", the read after a write | the pending list, once (it does not reload by itself otherwise) |

- A 429 keeps what is on screen: the rows, and the counts shown before (the counts no longer go "Sin dato" on a 429).
- The filter controls stay usable during the wait.

### Driver lists (Q10, Q15)

- **Picker** (order detail and inbox): the first list, which the picker asks for by itself, is not read during the wait
  and is asked for once, by itself, at 0. "Cargar más repartidores" is `aria-disabled` during the wait and re-enables
  itself; it is never sent by itself. The listed drivers, the chosen one and the typed cost stay.
- **"Asignar seleccionadas…"**: the per-order read is unchanged. A 429 on one order leaves it failed with its existing
  text, "No fue posible cargar los repartidores de esta orden. Intenta de nuevo.", and its "Reintentar", disabled until
  the wait ends and never sent by itself. The batch shows the common message once. The orders not read yet wait for the
  same wait before their read.

## Realtime reload spacing (Q9)

`ScreenReloads.notice()`:

- runs the first reload after the screen's existing debounce (250 ms for an order notice, 500 ms for a driver
  position), and no sooner than 3 seconds after the screen's previous reload of any kind;
- a reload already pending absorbs further notices and keeps its time, so a steady stream reloads every 3 seconds;
- a notice that arrives during the spacing is read by the reload that ends it, so none is lost;
- during a server wait the notices are read by the one reload at 0.

Screens:

- `/ops/dashboard`. An order notice reloads the list and the counts. A driver position reloads the list only: it reads
  the dashboard (`Reporting.OperationsDashboard`), so it joins the spacing. A gathered reload reads the counts when any
  of its notices was an order notice.
- `/ops/inbox` (driver positions are ignored there, as before).
- `/ops/orders/:id` (both kinds reload the order and its dashboard row).
- `/ops/routes`: `RouteChanged` reads the routes, which have no limit. Q9 says "el panel", so it is spaced too: the
  list once, and the route shown when a gathered notice named it. Every read of the list counts for the spacing:
  start, "Actualizar", a reconnect, the read after a change and the notices.

**Reconnect.** The mandatory resynchronization after a realtime reconnect still runs, and it is never spaced. During a
server wait it first waits the wait out, then reads, so it does not meet the same 429 and drop the hub.

## Choices not written in the task

- **Keying.**
  - One wait per limited operation per tab, shared by every screen of the tab (a store, not per screen), so a 429 on
    one screen holds the other screens that call the same operation, and nothing else.
  - On `/ops/orders`, `/finance/cod` and `/ops/routes` the top-bar "Buscar" waits with the page (all `listOrders`), so
    the countdown shows in both places. It is announced once.
- **Announcement.** One polite live region in the application shell, rather than one per notice, so two countdowns of
  one wait are not read twice. Every limited screen is under the shell (`/ops/*`, `/finance/*`).
- **Spacing counts every reload.** The 3 seconds count from the screen's previous reload of any kind (polling and
  "Actualizar" included). The reload at the end of a server wait is not spaced: it runs at 0.
- **Leading edge with a trailing catch-up.** The first notice after a quiet period reloads after its debounce; the
  rest are gathered into one reload when the 3 seconds end.
- **The reload at 0 follows each screen's existing rules for an automatic read.** It does not run while the page is
  hidden, because the page becoming visible reads it. Órdenes never drops "Cargar más" pages (Q14).
- **Rows on a filter change during the wait.** Each screen keeps its existing behaviour: the dashboard keeps the
  previous rows until the read at 0, and Órdenes and the inbox drop them at once.
- **A 429 on the counts keeps the counts shown.** Before, a failed counts read showed "Sin dato". Q16 pauses the whole
  screen, so the counts stay with the list until both are read at 0.
- **Initial read during a wait.** A screen opened while its wait runs sends nothing and reads once at 0. The dashboard
  starts its session, realtime included, at 0. Until then it holds no API, so polling and visibility read nothing.
  That start reads the filters on screen. Any other start (first load, organization switch) reads the first page
  without filters, as before.
- **Driver picker text while waiting.** "Cargando repartidores…" is hidden while the common message shows.
- **Bulk assignment.** The common message is shown once for the batch, above the rows. The failed row keeps its
  existing text.
- **429 of an operation outside the list.** It stays `unavailable`, with that operation's existing text, as before
  SEC-004. The wizard's confirmation also gained an exhaustive check: a new failure category cannot fall through to
  its generic text.
- **"Buscar" in the top bar.** Whenever a `listOrders` wait disables it, the countdown shows next to the search (Q13),
  wherever the wait started. It replaces a "No encontramos esa guía" still on screen.
- **The announcement.** It names only the newest wait of the tab. When that wait ends first, an older, longer wait is
  not announced again: it was already announced, and its N would be stale.

## Tests

vitest (`cd apps/web && pnpm test`):

- `src/lib/rate-limit-wait.test.ts`:
  - the text, singular at 1;
  - `Retry-After` parsing: missing or invalid gives 60, the one-hour cap;
  - one wait per operation, never shortened;
  - the countdown to 0 with no timer left;
  - the timer cleared on unsubscribe (unmount);
  - the sleep until the wait ends, extended or aborted.
- `src/components/ui/rate-limit-notice.test.ts`:
  - the countdown is a `timer` and not a live region;
  - the announcement once per wait, empty after it;
  - one announcer in the shell, which every limited layout uses.
- `src/operations/state/screen-reloads.test.ts`:
  - spacing: 40 notices in 2 s give two reloads, 3 s apart;
  - a notice during the spacing gives one reload after it;
  - kinds are merged, and every reload counts;
  - during a wait every read is refused, and exactly one reload runs at 0;
  - a counts-only wait pauses the screen, and an unrelated limit does not;
  - an extended wait is waited again;
  - nothing is left after `cancel`.
- `src/operations/api/rate-limit-api.test.ts`:
  - each of the ten limited calls is `rate_limited` with its `Retry-After` and records only its own wait;
  - 60 s without a usable header;
  - `retryable` (the key rule);
  - a 429 of `getOrder`, `organizationContexts` or `transitionOrder` stays `unavailable` with no wait, and the
    confirmation keeps its unanswered-request text;
  - every failure category of the confirmation is handled.
- `screen-reloads.test.ts` also checks that a 429 which left no wait for the screen never keeps a reload, so it cannot
  loop.
- Controllers:
  - `inbox-controller.test.ts`:
    - a 429 keeps the rows;
    - polling, realtime, visibility, buttons and the assignment refresh send nothing during the wait;
    - one read with the counts at 0;
    - a counts-only 429 pauses the whole inbox and keeps the counts (Q16);
    - a view change is read at 0 (Q14);
    - realtime spacing with no notice lost;
    - a reconnect waits out the wait and resynchronizes;
    - no timer is left after stop.
  - `order-list-controller.test.ts`:
    - a 429 keeps the rows, nothing is sent during the wait, one read at 0;
    - a filter and guía change is kept and read at 0;
    - no reload at 0 when "Cargar más" loaded more pages, and that "Cargar más" re-enables;
    - the first page is read again when "Cargar más" met the limit on page one;
    - no timer is left.
  - `cod-controller.test.ts`:
    - the pending list is kept on a 429, held during the wait and read once at 0;
    - a start during a wait reads at 0;
    - an unrelated limit does not hold it.
  - `csv-import-controller.test.ts`:
    - file, report and key kept on a 429;
    - both buttons held during the wait;
    - the same key after it;
    - the preview re-enabled.
  - `settlements-controller.test.ts`: no message of its own, "Exportar CSV" held, never sent by itself.
  - `create-order-controller.test.ts`: the draft and key are kept, nothing is sent during the wait, the same key after
    it, and a new key for a changed shipment.
  - `driver-assignment-controller.test.ts`:
    - the first list is read once by itself at 0, also when opened during a wait;
    - "Cargar más repartidores" is held, the list is kept, and it is never sent by itself;
    - no timer is left.
  - `bulk-assignment-controller.test.ts`: only the limited order fails, the next order waits for the wait, and
    "Reintentar" is refused during it and works after it.
  - `manual-routes-controller.test.ts`:
    - "Agregar entrega" keeps the guía, has no message of its own and is held during the wait;
    - `RouteChanged` notices are gathered and none is lost;
    - every read of the list counts for the spacing, "Actualizar" included.
  - `queue-counts-loader.test.ts` (a 429 keeps the counts) and `order-transition-controller.test.ts` ("Buscar guía"
    tells a 429 apart).
- Screens:
  - `operations-orders-shell.test.ts`: the countdown, the held buttons, the rows kept and the filters usable;
  - `manual-routes-shell.test.ts`: "Agregar entrega" held with the countdown and the guía kept.
- Sources and contracts (`src/operations/panel-rate-limit-policy.test.ts`):
  - the text has one writer;
  - every limited call is tagged;
  - the dashboard and order detail hooks admit, space and resynchronize through `ScreenReloads`;
  - every limited screen shows the countdown and holds its buttons;
  - AI-07 and the decision-log row agree with the code.
- Changed: `manual-routes-page-policy.test.ts` (the hook sends `RouteChanged` to the gatherer).
- Browser:
  `OperationsDashboardRateLimitPwaPlaywrightTests.A_status_chosen_during_the_wait_is_read_when_the_dashboard_starts_at_zero`.
  1. The dashboard's first read answers 429 with `Retry-After: 2`.
  2. "Actualizar" is held and the filters stay usable.
  3. A status chosen during the wait is not sent.
  4. At 0 the dashboard starts once, by itself, and that read carries `status=DELIVERED`.
- Browser:
  `OrdersListPwaPlaywrightTests.Orders_list_waits_out_a_429_with_the_countdown_and_reads_once_by_itself`.
  1. The first `listOrders` answers 429 with `Retry-After: 2`.
  2. "Espera 2 segundos" shows, then "Espera 1 segundo".
  3. It is announced once.
  4. "Actualizar" and the top bar's "Buscar" are held.
  5. Nothing is sent during the wait.
  6. The list is read once by itself at 0, and nothing more after it.

## Residual risks

- **Bulk assignment spends the driver-list budget** (Q10, unchanged): a 20-order batch reads at least 20 of the 60 per
  minute. It is reviewed with the metrics (event 4604) in 2 weeks.
- **Another tab of the same user** spends the same per-user budgets, but its waits are not shared (per-tab memory, by
  decision). A tab can meet a 429 another tab already met. It then waits its own `Retry-After`.
- **A reconnect that meets a 429 the tab did not know about** (another tab spent the budget) fails its mandatory
  resynchronization as before: the hub stops ("Sin conexión") and polling continues until the page is reloaded.
- **`Retry-After` is an upper bound.** The API's fixed window always reports its full window (60 s), so the panel can
  wait longer than the server needs. That is the price of never sending too early.
- **Two countdowns on one page** ("Buscar guía" and "Órdenes", "Cobros pendientes" or "Agregar entrega") while
  `listOrders` waits. Both controls are held for the same reason, and the message is announced once.
- **The end-of-wait reload is not spaced.** It runs at 0 and carries the realtime notices gathered during the wait. If
  the screen read less than 3 seconds before, it reads again at 0; that can only happen with a `Retry-After` under
  3 s, and the API always sends 60.
- **A reconnect during a wait** waits it out, then runs the mandatory resynchronization and the held reload back to
  back: two reads at 0 instead of one. The resynchronization is mandatory, and the held reload keeps the Q11 rule.

## Verify locally

```bash
cd apps/web && pnpm test && pnpm typecheck && pnpm lint
python3 docs/normative/v0.6/tools/validate_contracts.py
dotnet test tests/Paqueteria.IntegrationTests -c Release \
  --filter "FullyQualifiedName~OrdersListPwaPlaywright|FullyQualifiedName~OperationsDashboardRateLimitPwaPlaywright"
dotnet test tests/Paqueteria.ArchitectureTests -c Release --filter "FullyQualifiedName~SyntheticEnvironment"
```

## Rollback

- Revert the pull request. There is no server, schema, migration, role, grant, configuration, package or API contract
  change; the waits live in the tab's memory, so nothing is left behind.
- After a revert:
  - a panel screen that meets a limit shows its existing failure text again (for example "Sin respuesta válida del
    servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.");
  - the realtime reloads go back to one per notice (250 ms and 500 ms debounces);
  - AI-07 loses `panel_rate_limit` and the screens' `rate_limit` entries.
- The server limits (#252) keep working either way.
