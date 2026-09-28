# OBS-002: pilot observability (logs, alerts, workbook)

Backlog item **OBS-002** (`AI-08_BACKLOG.yaml`, AI08-PILOT-ITEMS): centralized logs, metrics and alerts
for API, Worker and outbox lanes. Acceptance criteria:

1. alerts on outbox lag, DEAD growth, retention failures and readiness degradation;
2. no PII, tokens or payloads in telemetry dimensions;
3. dashboards cover request errors by status class and Worker job outcomes;
4. required test: telemetry dimension allowlist.

No NuGet or npm package was added. The design uses what the pilot already has: JSON console logs
(`AddJsonConsole`) that Container Apps sends to the Log Analytics workspace of `platform.bicep`.
The alerts are Azure Monitor log search alerts (`Microsoft.Insights/scheduledQueryRules`) over those
logs. The .NET `Meter` instruments stay as they are; nothing exports them in the pilot (that would need
an OpenTelemetry exporter package), so the alerts read structured log events instead.

## Structured events

All three events are written with a fixed `EventId`. Every property is a fixed lane, job or outcome
name, a status class or a count. The allowlist is `TelemetryDimensions.Allowed`
(`src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TelemetryDimensions.cs`).

| EventId | Name | Written by | Properties | Frequency |
| --- | --- | --- | --- | --- |
| 4601 | `OutboxLaneSummary` | `OutboxLaneMonitor`, driven by each dispatcher loop | `Lane`, `Claimed`, `Processed`, `Retry`, `Dead`, `LoopFailures`, `MaxClaimAgeMs`, `WindowSeconds` | one per lane per minute, also when idle (heartbeat); `Warning` when `Dead` or `LoopFailures` > 0 |
| 4602 | `ScheduledJobCycle` | `PeriodicJobScheduler` (ADR-034) | `Job`, `Outcome` (`success`/`failure`), `DurationMs` | one per job cycle; `Error` on failure |
| 4603 | `HttpStatusSummary` | `HttpStatusReporter` in the API | `Total`, `Status2xx`, `Status3xx`, `Status4xx`, `Status5xx`, `WindowSeconds` | one per minute with at least one response; `Warning` when 5xx > 0 |
| 4004 | `OutboxRetentionLaneCompleted` (existing, OPS-004) | `OutboxRetentionService` | `Lane`, `Outcome` (`success`/`failure`/`cancelled`), counts and cutoffs | one per retention lane run (every 15 min) |

Lanes (`OutboxLanes`, anything else throws): `notifications` and `dispatch` (Worker),
`realtime_business` and `realtime_location` (API, GATE-013 in-process SignalR dispatcher).

Settlement mapping, taken from the AI-06 functions each lane calls under `lease_token`:

| Lane | `Processed` | `Retry` | `Dead` |
| --- | --- | --- | --- |
| notifications | `SUCCESS`; source expanded or without recipients | `TRANSIENT`, `AMBIGUOUS` | `DEAD` settle, `PERMANENT`, `MAX_ATTEMPTS`, template errors on a source row |
| dispatch | reaction settled `PROCESSED` (not `LeaseLost`) | reaction failed, attempts left | invalid payload, attempts exhausted |
| realtime_* | settle `PROCESSED` | settle `RETRY` | settle `DEAD` |

A settle that lost its lease is not counted.

The JSON console line looks like this (checked with the .NET 10 `JsonConsoleFormatter`):

```json
{"EventId":4601,"LogLevel":"Information","Category":"Paqueteria.Infrastructure.Observability.OutboxLaneMonitor","Message":"Outbox lane notifications summary: ...","State":{"Lane":"notifications","Claimed":3,"MaxClaimAgeMs":90000,...}}
```

The KQL reads it as `parse_json(Log_s)`, then `e.EventId` and `e.State.<Property>`.

### Outbox lag: what is measured and what is not

Invariant 3 forbids a direct `SELECT` on the outbox from runtime. No existing SECURITY DEFINER or
maintenance function returns the backlog size or the oldest pending age:
- the claim, settle and requeue functions return only the rows they lease;
- the OPS-004 purge function (and its DEAD-only dry-run probe) counts only `PROCESSED`/`DEAD` rows
  past their retention cutoff.

No SQL was added for this. Lag is measured cheaply from what the claim functions already return:

- **`MaxClaimAgeMs`** is `now - min(available_at)` over each claimed batch. It is the time the oldest
  ready message waited before a dispatcher took it. A retry's backoff is not counted as lag, because
  `available_at` is the retry time. The Dispatch store now also reads `available_at` from the
  `claim_dispatch_outbox` result row, which was already `RETURNING o.*`.
- **A stalled lane** writes no `OutboxLaneSummary`. The summary is driven by the dispatcher loop, so a
  blocked loop stops its heartbeat instead of reporting zeros.
- **`LoopFailures`** counts loop iterations that failed, for example because the database is
  unreachable.

**Not measured:** the size of a backlog that no dispatcher is claiming, and the age of messages that
no dispatcher can claim. A lane that stops claiming is still caught as a stall (no summary) or through
readiness (probe failures). An exact backlog gauge needs a new maintenance function in AI-06/AI-18,
for example `maintenance.outbox_lane_stats()` returning counts and the oldest `available_at` per
status. That is a normative change and needs an owner decision; it is not part of this change.

### What the API counts

`app.UseHttpStatusTelemetry()` is the first middleware, so it sees the final status code, including
the 500 written by the exception handler. An exception that escapes it counts as 5xx. A request the
client abandoned is not counted. `/health/*` is platform probe traffic and is excluded. 1xx (a
WebSocket upgrade that ended) counts as 2xx. Only the status class is recorded: never the path,
query, route values, headers, user or tenant.

## Alerts (`deploy/azure/pilot/observability.bicep`, stage 5)

Every rule queries the pilot workspace. Each is stateful (`autoMitigate`), so it notifies once when it
fires and once when it resolves. All rules notify the action group `ag-pv-pilot-ops`, whose only
receiver is an e-mail: the parameter `alertEmailAddress`, which has no default. The owner sets it in
`deploy/azure/pilot/observability.parameters.json`, which holds `OWNER_DECISION_REQUIRED` until then.
Both the provenance job and stage 5 of the workflow stop with `STOP_FOR_OWNER_DECISION` while the
placeholder is still there (`env001_pilot_guards.py observability-check`).

| Rule | Condition (defaults are template parameters) | Frequency / window | Severity |
| --- | --- | --- | --- |
| `sqr-pv-pilot-outbox-lag` | per lane: no summary in the window, or `max(MaxClaimAgeMs)` > 300 s (`outboxLagThresholdSeconds`), or ≥ 10 loop failures | 15 min / 15 min | 2 |
| `sqr-pv-pilot-outbox-dead` | any lane with `sum(Dead)` > 0 | 15 min / 15 min | 2 |
| `sqr-pv-pilot-job-failure` | any 4004 or 4602 `failure`, or no successful 4004 retention lane result in the hour | 15 min / 1 h | 2 |
| `sqr-pv-pilot-readiness` | per app (API, Worker, Web) ≥ 3 (`readinessEventThreshold`) `ProbeFailed`, crash/back-off, `ContainerTerminated` or `ReplicaUnhealthy` system events, or probe warnings | 5 min / 10 min | 1 |
| `sqr-pv-pilot-api-5xx` | ≥ 5 (`http5xxMinimumCount`) 5xx responses that are also ≥ 5 % (`http5xxPercentThreshold`) of all responses | 15 min / 15 min | 2 |

The KQL of every rule and workbook tile was parsed and type-checked offline with
`Microsoft.Azure.Kusto.Language` against the `ContainerAppConsoleLogs_CL` / `ContainerAppSystemLogs_CL`
columns. `skipQueryValidation` is set because those tables appear only after the first container log.

## Workbook

`Paqueteria pilot operations (OBS-002)` (`Microsoft.Insights/workbooks`, free), on the pilot workspace:

- API responses by status class (2xx/3xx/4xx/5xx per 15 min, 24 h);
- Worker job outcomes (cycles per job and outcome, retention per lane, 24 h);
- outbox lanes (claimed, processed, retry, dead, loop failures, max claim age, last summary);
- probe failures and restarts (7 days).

## Cost (PILOT-BUDGET-100USD, PILOT-BUDGET-ACCEPT-98-108)

Prices come from the Azure Retail Prices API for `mexicocentral` on 2026-09-28. A log search alert
costs, per rule and month, 0.55 USD at a 15-minute frequency, 1.10 USD at 10 minutes and 1.65 USD at
5 minutes.

| Item | USD/month |
| --- | --- |
| 4 rules at 15 min + 1 rule (readiness) at 5 min | 3.85 |
| Action group e-mails (first 1,000 per month free) | 0.00 |
| Workbook | 0.00 |
| New log lines: about 10,000 short lines per day (4 lanes × 1,440 heartbeats, about 3,400 job cycles, at most 1,440 HTTP summaries), roughly 10–15 MB/day or 0.3–0.45 GB/month | 0.00 inside the 5 GB free tier; at most about 1.10 if the workspace is already past it |
| **Added total** | **≈ 3.85 (typical)** |

The pilot estimate moves from about 98 to about **102 USD typical**. At the log cap it moves from
about 108 to about **112 USD**, because the 0.3 GB/day cap bounds the extra logs. Guard P21 fails if
the rules would cost more than 5 USD per month. If the owner wants to stay under 100 USD, the cheapest
lever is readiness at 15 minutes (−1.10 USD).

## Tests

- `tests/Paqueteria.ArchitectureTests/ObservabilityArchitectureTests.cs`: the telemetry dimension
  allowlist.
  - Every log template on the Worker and outbox paths uses only allowlisted properties, with no
    interpolated messages.
  - Metric tags are allowlisted.
  - The OBS-002 events carry exactly `TelemetryDimensions.Allowed`, and no allowlisted name looks
    like an identifier or personal field.
  - The observability code never touches the database.
  - The alert queries read only allowlisted `State` properties and the four event ids.
  - The API wraps the exception handler.
- `tests/Paqueteria.UnitTests/Operations/Obs002TelemetryTests.cs` checks:
  - the lane summary window, counts, claim age and reset;
  - rejection of unknown lanes;
  - HTTP status classes, and that an empty window writes nothing;
  - one scheduler event per cycle, without the exception text;
  - the notification outcome to settlement mapping.
- `tools/azr-001/test_env001_pilot_guards.py`:
  - `observability-check` (the placeholder blocks deployment; the file allows one e-mail address and
    the documented thresholds only);
  - P00 now expects five templates, and P01 accepts only the three `Microsoft.Insights` types used;
  - P21 fails closed on a default e-mail, a non-e-mail receiver, a 1-minute frequency, rules over
    5 USD, a window shorter than its frequency, a dropped rule, a non-allowlisted property or table,
    a stateless rule, or a workflow that skips the parameter check.

## Verified here vs. pending

- **Verified without Azure:**
  - pinned Bicep v0.47.16 build and lint with no diagnostics;
  - guards P00–P21 pass;
  - offline KQL parse and type check;
  - the JSON console shape;
  - unit and architecture tests.
- **Unverified until the first deployment with stage 5:**
  - ARM acceptance of the three `Microsoft.Insights` resources;
  - that Container Apps writes the JSON line unchanged into `Log_s`;
  - the exact `Reason_s` values of `ContainerAppSystemLogs_CL` for probe failures and restarts;
  - that a deployment's own revision restart stays under the readiness threshold.

  After the first run, check each rule's query in Log Analytics and adjust the thresholds in
  `observability.parameters.json` if needed.

## Rollback

- **Alerts:** delete the stage 5 resources with
  `az monitor scheduled-query delete`, `az monitor action-group delete` and
  `az resource delete --resource-type Microsoft.Insights/workbooks`, or leave them disabled. Removing
  stage 5 from the workflow also requires reverting the P00/P21 guard changes.
- **Code:** revert the commit. The new log events are additive, no schema or contract changed, and
  the only behavioral change is the `PeriodicJobScheduler` failure line: event 4602 with
  `Outcome=failure` replaces the previous `CYCLE_FAILURE` message.
