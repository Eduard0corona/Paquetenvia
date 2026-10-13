# SCL-007 PR 9: adaptive wait of the realtime outbox dispatchers

Phase 4 PR 9 (`docs/development/phase4-platform-plan.md`, "PR 9: espera adaptativa del outbox (SCL-007)"), backlog
item **SCL-007** in `AI-08_BACKLOG.yaml`. Decision row: `SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13`.

The owner's question and answer, literal (2026-10-13):

- Question: "PR 9: espera adaptativa de los avisos en tiempo real. Sin trabajo, el repartidor espera cada vez más entre
  consultas, hasta una espera máxima. ¿Cuál?"
- Answer: "1 segundo en ambos (Recommended)", whose option read "Negocio y ubicación esperan como máximo 1 s. Sin trabajo
  bajan de unas 6 a 2 consultas por segundo; el primer aviso después de un rato sin trabajo tarda hasta 1 s más, dentro
  de los 2 s de AI-15."

The other options, the context shown to the owner and the choices this note makes are in the decision row.

## Before and after

Only the API's realtime outbox dispatchers change (`BusinessOutboxDispatcher` and `LocationOutboxDispatcher` in
`src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxDispatchers.cs`).

| Lane | Before, after an empty claim | After, after each empty claim | Claims a second while idle |
| --- | --- | --- | --- |
| Business (`platform.outbox_events`) | 250 ms, always | 250 ms, 500 ms, then 1 s | 4 before, 1 after |
| Location (`platform.location_outbox_events`) | 500 ms, always | 500 ms, then 1 s | 2 before, 1 after |

A claim that returns work, a full batch or a partial one, is followed by the next claim at once, as before. The Worker's
pollers do not change: Notifications and the Dispatch assignment lifecycle lane (250 ms), proof validation (5 s) and the
scheduled jobs keep their fixed intervals. The owner's question covered only the realtime notices.

## Progression

Each lane loop owns one `OutboxIdleWait` (`src/Modules/Realtime/Realtime.Application/Dispatching/OutboxIdleWait.cs`).
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

## Bounds

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

## Idle query rate

Each claim is one transaction on the `paqueteria_realtime` pool: `BEGIN`, `SET LOCAL ROLE`, the claim function and
`COMMIT`.

| | Business | Location | Both lanes | A day while idle |
| --- | --- | --- | --- | --- |
| Before | 4 claims/s | 2 claims/s | about 6 claims/s | about 518,000 claims |
| After | 1 claim/s | 1 claim/s | about 2 claims/s | about 173,000 claims |

The stale requeue does not change: each lane calls its requeue function every 30 s (`StaleRequeueIntervalSeconds`).
The fake-clock test counts the claims of the first idle minute: 63 on business (3 during the ramp, then 1 a second) and
62 on location. Before, there would have been about 244 and 122.

## First notice after an idle period

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

## `MaxClaimAgeMs` includes the wait

`MaxClaimAgeMs` is computed in `OutboxLaneMonitor.Claimed` (`src/BuildingBlocks/Paqueteria.Infrastructure/Observability`)
as `now - min(available_at)` of each claimed batch. `now` is read after the claim returns, on the host's
`TimeProvider`. The dispatchers now wait on that same `TimeProvider`. A row that became ready while the lane was waiting
therefore reports the whole time it waited, idle wait included: up to 1000 ms on these lanes.

The computation did not need to change. The monitor's remarks now say that the age includes the wait. A fake-clock test
proves it: a row that became ready right after an empty claim and was taken by the claim after the 1 s wait reports
`MaxClaimAgeMs` 1000.

## Mechanism and invariants

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

## Tests

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

## Rollback

- **Configuration, no deploy of code.** Set each maximum equal to its poll interval:
  `Realtime__OutboxDispatcher__Business__MaximumIdleWaitMilliseconds=250` and
  `Realtime__OutboxDispatcher__Location__MaximumIdleWaitMilliseconds=500` (Container Apps environment variables of the
  API). The progression then never grows, which is exactly the old fixed poll.
- **Code.** Revert the pull request. There is no schema, migration, grant or data to undo.

## Residual risks

- In a quiet pilot, most first notices arrive up to 1 s later than before. The owner accepted this in the chosen option.
- The worst case is 1 s plus the claim and the publish, so a slow publish can bring one notice close to 2 s. The publish
  timeout (5 s) and the retry policy are unchanged.
- The Worker's Notifications and Dispatch lifecycle lanes still poll every 250 ms while idle (about 8 claims a second
  between them). Giving them an adaptive wait would need its own decision.
- At about 2 claims a second, the API replica is still active for Container Apps billing, so the pilot cost table does
  not change.
