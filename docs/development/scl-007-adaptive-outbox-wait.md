# SCL-007: adaptive wait of the outbox dispatchers

Backlog item **SCL-007** in `AI-08_BACKLOG.yaml`. Two owner decisions apply the same wait to the four outbox lanes:

| Lanes | Decision row | Section |
| --- | --- | --- |
| API realtime business and location | `SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13` (phase 4 PR 9) | Realtime lanes (PR 9) |
| Worker Notifications and Dispatch assignment lifecycle | `SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14` (phase 4 follow-up) | Worker lanes |

The four lanes use one implementation, `OutboxIdleWait`
(`src/BuildingBlocks/Paqueteria.Application/Scaling/OutboxIdleWait.cs`). While claims return work, a lane claims again
at once; while they come back empty, it waits longer each time, from its poll interval up to 1 s. Proof validation
(5 s) and the scheduled jobs keep their fixed intervals.

## Realtime lanes (PR 9)

Phase 4 PR 9 (`docs/development/phase4-platform-plan.md`, "PR 9: espera adaptativa del outbox (SCL-007)"). Decision
row: `SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13`.

The owner's question and answer, literal (2026-10-13):

- Question: "PR 9: espera adaptativa de los avisos en tiempo real. Sin trabajo, el repartidor espera cada vez más entre
  consultas, hasta una espera máxima. ¿Cuál?"
- Answer: "1 segundo en ambos (Recommended)", whose option read "Negocio y ubicación esperan como máximo 1 s. Sin trabajo
  bajan de unas 6 a 2 consultas por segundo; el primer aviso después de un rato sin trabajo tarda hasta 1 s más, dentro
  de los 2 s de AI-15."

The other options, the context shown to the owner and the choices this note makes are in the decision row.

### Before and after

Only the API's realtime outbox dispatchers change (`BusinessOutboxDispatcher` and `LocationOutboxDispatcher` in
`src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxDispatchers.cs`).

| Lane | Before, after an empty claim | After, after each empty claim | Claims a second while idle |
| --- | --- | --- | --- |
| Business (`platform.outbox_events`) | 250 ms, always | 250 ms, 500 ms, then 1 s | 4 before, 1 after |
| Location (`platform.location_outbox_events`) | 500 ms, always | 500 ms, then 1 s | 2 before, 1 after |

A claim that returns work, a full batch or a partial one, is followed by the next claim at once, as before. The owner's
question covered only the realtime notices; the Worker's Notifications and Dispatch assignment lifecycle lanes got the
same wait later, with their own decision (Worker lanes, below).

### Progression

Each lane loop owns one `OutboxIdleWait` (`src/BuildingBlocks/Paqueteria.Application/Scaling/OutboxIdleWait.cs`; it
lived in `Realtime.Application` until the Worker lanes adopted it).
After each claim:

| The claim returned | Next claim | Progression |
| --- | --- | --- |
| a full batch (`min(BatchSize, MaximumConcurrency)` rows: 4 on business, 8 on location) | at once | starts over |
| a partial batch (1 row up to one less than full) | at once | starts over |
| nothing | after the current step | the next step doubles, up to the maximum |
| an error (for example, the database is unreachable) | after the poll interval, as before | unchanged |

Why doubling from the poll interval:

- The first wait after work is the lane's existing poll interval. A lane with steady traffic therefore behaves as before:
  the first empty claim after a batch waits exactly as long as it used to.
- It reaches the maximum quickly. Business needs two empty claims (250 ms + 500 ms of waiting), location needs one
  (500 ms). A notice that arrives during the ramp waits less than the maximum.
- With the defaults, every step lands exactly on 1 s. Other poll intervals are capped at the maximum
  (300 ms gives 300, 600, 1000).
- No jitter. There is one API replica while GATE-013 is open, and claims are safe across replicas anyway (`FOR UPDATE
  SKIP LOCKED` and the lease token).

A full and a partial batch behave the same. Before this change, any claim with rows was already followed by an immediate
claim, and keeping that keeps the behaviour under load unchanged. The cost is one extra claim after each partial batch,
which usually comes back empty and starts the wait at the poll interval.

Claim instants after the last work, with the defaults (claims at t = 0 return nothing):

| Lane | Claims at |
| --- | --- |
| Business | 0, 0.25 s, 0.75 s, 1.75 s, 2.75 s, 3.75 s, ... |
| Location | 0, 0.5 s, 1.5 s, 2.5 s, 3.5 s, ... |

### Bounds

The maximum is validated configuration, per lane:

| Setting | Default (code and `src/Paqueteria.Api/appsettings.json`) | Accepted |
| --- | --- | --- |
| `Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds` | 1000 | from `Business:PollIntervalMilliseconds` (250) to 1000 |
| `Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds` | 1000 | from `Location:PollIntervalMilliseconds` (500) to 1000 |

A value outside that range refuses the API start, like every other bounded option of the section ("Realtime:
OutboxDispatcher contains an invalid bounded option."). So does a value that is not a number. The Bicep sets neither
value, so the pilot runs with the defaults (`PilotApiStartupConfigurationTests` checks the composed pilot settings).

The upper bound is `OutboxDispatcherOptionsValidator.MaximumIdleWaitCeilingMilliseconds` = 1000 ms, half of the AI-15
`event_delivery_p95_seconds` warning (2 s, `EventDeliveryBudgetMilliseconds`):

- On an idle lane the maximum wait adds directly to the delivery of a notice. Half of the budget may be spent waiting.
  The other half is kept for the claim that takes the row, the evidence reads, the SignalR publish and the settle.
- It is also the owner's value. The owner turned down "2 s negocio, 5 s ubicación" because business notices "quedarían
  cerca del límite de 2 s". A longer wait needs a new owner decision, not a configuration change.
- The lower bound is the lane's poll interval: the progression starts there, and a maximum below it would make the wait
  shrink.
- As a side effect, a realtime lane's poll interval can no longer exceed 1000 ms (it could be up to 60000 before). The
  defaults are 250 and 500.

The other two constraints of the plan:

- **AI-15 `oldest_outbox_age_seconds` warning (60 s).** On an idle lane, a ready row waits at most 1 s for its claim,
  1/60 of the warning. The OBS-002 lag alert (`max(MaxClaimAgeMs)` > 300 s) is far away too.
- **OBS-002 per-minute heartbeat.** `ReportIfDue` runs at the top of every loop iteration, and an idle lane iterates at
  least once a second. Each lane's `OutboxLaneSummary` is still written once a minute, at most about 1 s after its minute
  ends, with `WindowSeconds` 60. A stalled loop still stops writing it.

### Idle query rate

Each claim is one transaction on the `paqueteria_realtime` pool: `BEGIN`, `SET LOCAL ROLE`, the claim function and
`COMMIT`.

| | Business | Location | Both lanes | A day while idle |
| --- | --- | --- | --- | --- |
| Before | 4 claims/s | 2 claims/s | about 6 claims/s | about 518,000 claims |
| After | 1 claim/s | 1 claim/s | about 2 claims/s | about 173,000 claims |

The stale requeue does not change: each lane calls its requeue function every 30 s (`StaleRequeueIntervalSeconds`).
The fake-clock test counts the claims of the first idle minute: 63 on business (3 during the ramp, then 1 a second) and
62 on location. Before, there would have been about 244 and 122.

### First notice after an idle period

- **Worst case.** The notice commits just after an empty claim, while the lane waits its 1 s maximum. It waits up to 1 s,
  then takes the time of the claim, the evidence reads, the publish and the settle. Before, it waited up to 250 ms
  (business) or 500 ms (location). The worst case grows by 750 ms on business and 500 ms on location.
- **On average.** A notice arriving at a random moment of a long idle period waits about 500 ms. Before, it waited about
  125 ms (business) or 250 ms (location).
- **During the ramp.** Within the first 1.75 s (business) or 1.5 s (location) after the last work, the wait is shorter.
- **Under load.** No change: a lane whose claims return work claims again at once.

Measured by `Scl007AdaptiveOutboxWaitDeliveryTests`, with real PostgreSQL, the API on Kestrel and a real SignalR client,
using the production waits, after 3 s idle, from just before the producer's insert to the client's receipt (local run):
business 181 ms and 1012 ms, location 773 ms and 531 ms. The test asserts every sample at or below 2 s.

### `MaxClaimAgeMs` includes the wait

`MaxClaimAgeMs` is computed in `OutboxLaneMonitor.Claimed` (`src/BuildingBlocks/Paqueteria.Infrastructure/Observability`)
as `now - min(available_at)` of each claimed batch. `now` is read after the claim returns, on the host's
`TimeProvider`. The dispatchers now wait on that same `TimeProvider`. A row that became ready while the lane was waiting
therefore reports the whole time it waited, idle wait included: up to 1000 ms on these lanes.

The computation did not need to change. The monitor's remarks now say that the age includes the wait. A fake-clock test
proves it: a row that became ready right after an empty claim and was taken by the claim after the 1 s wait reports
`MaxClaimAgeMs` 1000.

### Mechanism and invariants

- The wait only spaces calls to the existing `security.claim_realtime_outbox` and `security.claim_location_outbox`. Settle
  and requeue are unchanged: `security.settle_outbox`, `security.settle_location_outbox` and the two stale requeue
  functions, with the lease token. There is no new SQL, no new grant and no outbox read.
- The lanes are independent. Each loop owns its `OutboxIdleWait`; nothing is shared between them.
- The wait is `Task.Delay(wait, timeProvider, stoppingToken)`. Stopping the host cancels it at once, and the loop leaves
  through its existing cancellation branch.
- The dispatchers read the time from the host's `TimeProvider` (`TimeProvider.System`, registered by
  `AddOutboxLaneMonitor`), both for the wait and for the stale requeue schedule. Tests use a manual clock.
- `realtime.outbox.batch_duration` measures only the work of an iteration: the stale requeue when it is due, the claim
  and the batch. The dispatcher ends the measurement before it waits, after an empty claim and after a failed
  iteration, so an idle iteration records only its claim time. Before this change it also counted the fixed 250 or
  500 ms poll. This matters because OBS-003 PR 6 is to export the `Paquetenvia.*` meters with `lane` as a tag. The
  telemetry reads durations and the claimed-row age on the same `TimeProvider` as the wait.
- The wait still runs inside the cycle's trace (OBS-003), as the fixed poll did. The outbox loops' traces are not
  exported.

### Tests

- Unit, `tests/Paqueteria.UnitTests/Scaling/Scl007AdaptiveOutboxWaitTests.cs`. The real dispatcher loops run on a manual
  `TimeProvider` with a scripted claim store. Rows with an unknown topic are settled `DEAD` by the real processor, and no
  time passes unless the test moves the clock. The tests cover:
  - the wait grows on each lane (250, 500, 1000 ms; 500, 1000 ms) and is never longer than 1 s over three idle minutes;
  - full batches are claimed back to back, and full or partial batches start the wait over;
  - the lanes are independent (work on one does not restart the other's wait);
  - a failed iteration keeps the poll interval;
  - stopping during a 1 s wait returns without the clock moving;
  - the batch duration records each claim (5 ms on the clock) and never the 250, 500 or 1000 ms wait after it;
  - the idle heartbeat of both lanes, and the idle claim count;
  - `MaxClaimAgeMs` includes the wait;
  - the defaults and the AI-15 ceiling;
  - the validator and the configuration binding refuse values below the poll interval, above 1000 ms or not a number.
- Integration (PostgreSQL), `tests/Paqueteria.IntegrationTests/Realtime/Scl007AdaptiveOutboxWaitDeliveryTests.cs`
  (category `OutboxSignalRDelivery`): the delivery after an idle period, above.
  - The harness polls every 50 ms, so the test reads the waits from the API's own `appsettings.json` and puts them back.
  - It measures only the end-to-end result. Only the fake-clock unit tests prove the backoff itself: each wait, its
    reset, its cap and the independence of the lanes.
  - Each of the four samples must be at or below 2 s. The worst case is the 1 s wait plus a few milliseconds of claim
    and publish on loopback, so a runner has about 0.9 s of margin.
- `PilotApiStartupConfigurationTests` asserts the pilot API composes 250 ms / 1 s and 500 ms / 1 s.
- Regression: the realtime outbox, SignalR delivery, OBS-002 and OBS-003 tests keep passing unchanged. The harness keeps
  its 50 ms poll interval and now also backs off, up to 1 s.

### Rollback

- **Configuration, no deploy of code.** Set each maximum equal to its poll interval:
  `Realtime__OutboxDispatcher__Business__MaximumIdleWaitMilliseconds=250` and
  `Realtime__OutboxDispatcher__Location__MaximumIdleWaitMilliseconds=500` (Container Apps environment variables of the
  API). The progression then never grows, which is exactly the old fixed poll.
- **Code.** Revert the pull request. There is no schema, migration, grant or data to undo.

### Residual risks

- In a quiet pilot, most first notices arrive up to 1 s later than before. The owner accepted this in the chosen option.
- The worst case is 1 s plus the claim and the publish, so a slow publish can bring one notice close to 2 s. The publish
  timeout (5 s) and the retry policy are unchanged.
- The Worker's Notifications and Dispatch lifecycle lanes polled every 250 ms while idle (about 8 polls, 12 claim
  transactions, a second between them). They now have the same wait, with their own decision (Worker lanes, below).
- At about 2 claims a second, the API replica is still active for Container Apps billing, so the pilot cost table does
  not change.

## Worker lanes

Phase 4 follow-up, backlog item **SCL-007**. Decision row: `SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14`.

The owner's question and answer, literal (2026-10-14):

- Question: "Las colas del Worker (notificaciones por WhatsApp o correo y ciclo de vida de asignaciones) consultan la base
  cada 250 ms aunque no haya trabajo: unas 8 consultas por segundo. ¿Les aplicamos la espera adaptativa, como a los
  avisos en tiempo real?"
- Answer: "1 segundo en ambas (Recommended)", whose option read "Sin trabajo bajan a unas 2 consultas por segundo. La
  primera notificación o evento después de un rato sin trabajo tarda hasta 1 s más. Igual que lo que elegiste para los
  avisos en tiempo real."

The options not chosen ("5 segundos en ambas", "Dejarlo como está"), the context shown to the owner and the choices
this section makes are in the decision row.

### Before and after

The Worker's two outbox dispatchers change:

- `NotificationsOutboxDispatcher`
  (`src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxDispatcher.cs`);
- `AssignmentLifecycleDispatcher`
  (`src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs`).

They get exactly the realtime lanes' behaviour, with the same `OutboxIdleWait`:

| Lane | Before, after an empty poll | After, after each empty poll | Polls a second while idle |
| --- | --- | --- | --- |
| Notifications | 250 ms, always | 250 ms, 500 ms, then 1 s | 4 before, 1 after |
| Dispatch assignment lifecycle | 250 ms, always | 250 ms, 500 ms, then 1 s | 4 before, 1 after |

What one poll is:

- **Notifications.** The stale recovery when it is due (every 30 s), then two claims: the owned rows
  (`security.claim_notifications_outbox`) and the unowned rows (`security.claim_unowned_outbox`), then the processing.
  Work on either claim, a full batch (10 rows) or a partial one, makes the lane poll again at once and starts the wait
  over. The rows the stale recovery returns are processed in the same poll but do not count as work, as before: only
  the two claims decided whether the lane waited.
- **Dispatch assignment lifecycle.** The stale requeue when it is due (every 30 s), then one claim
  (`security.claim_dispatch_outbox`, up to 10 rows), then the reactions. Any row claimed counts as work.

As on the realtime lanes, a failed iteration (for example, the database is unreachable) waits the poll interval and
does not move the progression. Each lane loop owns its own `OutboxIdleWait`, so the two Worker lanes wait
independently of each other and of the API's realtime lanes.

Poll instants after the last work, with the defaults (the poll at t = 0 returns nothing): 0, 0.25 s, 0.75 s, 1.75 s,
2.75 s, 3.75 s, and so on, on both lanes.

Proof validation (`ProofStorage:ProcessingIntervalSeconds`, 5 s) and the scheduled jobs keep their fixed intervals.

### Bounds

| Setting | Default (code and `src/Paqueteria.Worker/appsettings.json`) | Accepted |
| --- | --- | --- |
| `Notifications:MaximumIdleWaitMilliseconds` | 1000 | from `Notifications:PollIntervalMilliseconds` (250) to 1000 |
| `Dispatch:AssignmentLifecycle:MaximumIdleWaitMilliseconds` | 1000 | from `Dispatch:AssignmentLifecycle:PollIntervalMilliseconds` (250) to 1000 |

A value outside that range refuses the Worker start, like every other bounded option of the section ("Notifications
contains an invalid bounded option.", "Dispatch:AssignmentLifecycle contains an invalid bounded option."). So does a
value that is not a number. The pilot Bicep sets only `Provider` and `WorkerId` in both sections, so the pilot runs with
the defaults.

- The bounds are the realtime lanes' bounds, from one helper: `OutboxIdleWait.IsValidMaximum`.
- The upper bound is `OutboxIdleWait.MaximumIdleWaitCeilingMilliseconds` = 1000 ms, the owner's value. The owner turned
  down "5 segundos en ambas", so a longer wait needs a new owner decision, not a configuration change. It equals the
  realtime lanes' ceiling (`OutboxDispatcherOptionsValidator.MaximumIdleWaitCeilingMilliseconds`, half the AI-15 2 s
  delivery warning), and a unit test keeps them equal.
- The lower bound is the lane's poll interval: the progression starts there.
- As a side effect, a Worker lane's poll interval can no longer exceed 1000 ms (it could be up to 60000 before). The
  default is 250, and the test harnesses use 25 or 100.

The two constraints:

- **AI-15 `oldest_outbox_age_seconds` warning (60 s)**, the Worker metric. On an idle lane, a ready row waits at most
  1 s for its claim, 1/60 of the warning. The OBS-002 lag alert (`max(MaxClaimAgeMs)` > 300 s) is far away too.
- **OBS-002 per-minute heartbeat.** `ReportIfDue` runs at the top of every loop iteration, and an idle Worker lane
  iterates at least once a second. Each lane's `OutboxLaneSummary` is still written once a minute, at most about 1 s
  after its minute ends, with `WindowSeconds` 60. A stalled loop still stops writing it.

### Idle query rate

Each claim is one transaction on the `paqueteria_worker` pool: `BEGIN`, `SET LOCAL ROLE paqueteria_worker`, the claim
function and `COMMIT`. A Notifications poll is two claims.

| | Notifications | Dispatch | Both lanes | A day while idle |
| --- | --- | --- | --- | --- |
| Polls, before | 4 a second | 4 a second | about 8 a second | about 691,000 |
| Polls, after | 1 a second | 1 a second | about 2 a second | about 173,000 |
| Claim transactions, before | 8 a second | 4 a second | about 12 a second | about 1,037,000 |
| Claim transactions, after | 2 a second | 1 a second | about 3 a second | about 259,000 |

The owner's question counted polls: "unas 8 consultas por segundo", down to "unas 2". In claim transactions it is about
12 down to about 3, the same fourfold reduction. The stale recovery does not change: each lane runs it every 30 s
(`StaleRecoveryIntervalSeconds`). The fake-clock test counts the polls of the first idle minute: 63 on each lane (3
during the ramp, then 1 a second; before, about 244), and 63 unowned claims on Notifications.

Across the four lanes of both processes, an idle pilot now makes about 5 claim transactions a second (2 realtime, 3
Worker) instead of about 18 before SCL-007 (6 realtime, 12 Worker).

### First notification or reaction after an idle period

- **Worst case.** The row commits just after an empty poll, while the lane waits its 1 s maximum. It waits up to 1 s
  for the claim that takes it, then the processing (the provider call for a notification, the reaction transaction for
  the Dispatch lane). Before, it waited up to 250 ms. The worst case grows by 750 ms; the owner's option rounds it to
  "hasta 1 s más".
- **On average.** A row arriving at a random moment of a long idle period waits about 500 ms. Before, about 125 ms.
- **During the ramp.** Within the first 1.75 s after the last work, the wait is shorter.
- **Under load.** No change: a lane whose claims return work polls again at once.
- **Realtime notices written by a Worker lane.** The Dispatch reaction writes `dispatch.assignment-changed` and a
  notification delivery writes `notifications.status-changed`, both to the API's realtime business lane. On a fully
  idle system the two idle waits can add up: up to 1 s on the Worker lane, then up to 1 s on the realtime lane, about
  2 s in the worst case before the screen updates (before: 250 ms and 250 ms). The AI-15 `event_delivery_p95_seconds`
  warning (2 s) applies to the realtime lane from its own row, whose wait does not change. The order notices the API
  writes itself (for example `orders.status-changed`) do not go through a Worker lane.

### `MaxClaimAgeMs` includes the wait

As on the realtime lanes, `OutboxLaneMonitor.Claimed` computes `now - min(available_at)` when the claim returns, on the
host's `TimeProvider`, and both Worker dispatchers now wait on that same `TimeProvider`. A row that became ready while
the lane was waiting reports the whole wait: up to 1000 ms. The computation did not change; the monitor's remarks now
name the Worker lanes. A fake-clock test proves it on each lane: a row that became ready right after an empty poll and
was taken by the poll after the 1 s wait reports `MaxClaimAgeMs` 1000.

### Mechanism and invariants

- `OutboxIdleWait` moved from `Realtime.Application` to the shared building block
  `src/BuildingBlocks/Paqueteria.Application/Scaling/OutboxIdleWait.cs`, next to `InstanceIdentity`, which every lane's
  options already use. Its constructor now takes the poll interval and the maximum, and it carries the bounds helper and
  the 1000 ms ceiling. The realtime lanes behave exactly as before; their tests are unchanged apart from the constructor
  call. No module references another (ArchitectureTests).
- The wait only spaces calls to the existing claim functions. Settle, recovery and requeue are unchanged:
  `security.settle_outbox`, `security.recover_stale_notifications_outbox`, `security.requeue_stale_unowned_outbox`,
  `security.requeue_stale_dispatch_outbox` and the Notifications delivery functions, with the lease token. There is no
  new SQL, no new grant and no outbox read.
- The wait is `Task.Delay(wait, timeProvider, stoppingToken)`. Stopping the Worker cancels it at once, and the loop
  leaves through its existing cancellation branch.
- The dispatchers read the time from the host's `TimeProvider` (`TimeProvider.System`, registered by
  `AddOutboxLaneMonitor`), both for the wait and for the stale recovery schedule; before, they read
  `DateTimeOffset.UtcNow` for the schedule. Tests use a manual clock.
- The Worker lanes have no batch-duration meter, so nothing else measures the wait. The wait still runs inside the
  cycle's trace (OBS-003), as the fixed poll did.

### Tests

- Unit, `tests/Paqueteria.UnitTests/Scaling/Scl007WorkerAdaptiveWaitTests.cs`. The real dispatcher loops run on the
  manual `TimeProvider` of the realtime tests, with scripted stores. Rows with an unknown topic are settled `DEAD` by the
  real processors, and no time passes unless the test moves the clock. On each Worker lane, the tests cover:
  - the wait grows (250, 500, 1000 ms) and is never longer than 1 s over three idle minutes;
  - full batches are polled back to back, and full or partial batches (on Notifications, owned or unowned) start the
    wait over;
  - the lanes are independent (work on one does not restart the other's wait);
  - a failed iteration keeps the poll interval;
  - stopping during a 1 s wait returns without the clock moving;
  - the idle heartbeat of both lanes, the idle poll and claim counts and the 30 s stale recovery schedule;
  - `MaxClaimAgeMs` includes the wait;
  - the defaults, the Worker's own `appsettings.json` (250 ms and 1 s on both lanes) and the shared ceiling;
  - the validators and the configuration binding refuse values below the poll interval, above 1000 ms or not a number.
- The realtime tests (`Scl007AdaptiveOutboxWaitTests`) keep passing, with the constructor taking the two values.
- Regression: the Notifications, Dispatch lifecycle, outbox, OBS-002 and OBS-003 tests keep passing. Their harnesses
  keep their 25 or 100 ms poll intervals and now also back off, up to 1 s.
- No end-to-end delivery measurement was added for the Worker lanes: the owner's question was about the query rate,
  and the fake-clock tests prove each wait, its reset, its cap and the independence of the lanes.

### Rollback

- **Configuration, no deploy of code.** Set each maximum equal to its poll interval:
  `Notifications__MaximumIdleWaitMilliseconds=250` and `Dispatch__AssignmentLifecycle__MaximumIdleWaitMilliseconds=250`
  (Container Apps environment variables of the Worker). The progression then never grows, which is exactly the old
  fixed poll.
- **Code.** Revert the pull request. There is no schema, migration, grant or data to undo. Reverting it also moves
  `OutboxIdleWait` back to `Realtime.Application`; the realtime lanes keep their behaviour either way.

### Residual risks

- In a quiet pilot, most first notifications and Dispatch reactions start up to 1 s later than before (up to 750 ms
  more). The owner accepted this in the chosen option.
- An assignment or notification status notice on the screens can wait for both idle lanes, about 2 s in the worst case
  on a fully idle system (before about 0.5 s). The order notices the API writes itself are not affected.
- At about 1 poll a second per lane, the Worker replica is still active for Container Apps billing, so the pilot cost
  table does not change.
