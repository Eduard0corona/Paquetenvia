# REL-001: pilot readiness report, entry phase

AI-08 REL-001 "Puerta piloto operativo". Owner decision `REL-001-PILOT-READINESS-2026-11-02` (round Z, Z1 to Z12,
quoted in `docs/normative/v0.6/decision-log.md`). Definitions and thresholds: AI-24 `pilot_readiness`. Tool:
`tools/rel-001/rel001.py`. On-demand workflow: `.github/workflows/pilot-readiness.yml`.

## Two phases (Z1)

| Phase | What it proves | Status |
| --- | --- | --- |
| Entry | The pilot may start: delivery plan, synthetic rehearsal, current restore drill, full regression, gates and the recipient-notice line | This tool |
| Closing | `orders_with_complete_events_and_pod >= 0.98` on the 50-100 real deliveries | Pending. It needs a read-only count function in the pilot database (Z6, second half): an AI-06/AI-18 change, a migration with up and down and a job, reviewed as a security change. Nothing here reads the pilot's data. |

The entry phase lands in three PRs:

1. **PR-A (#335):** the AI-08 criterion, AI-24 `pilot_readiness`, the decision-log row, the tool, its closed manifests
   and the workflow.
2. **PR-B (#340):** the local/CI synthetic rehearsal with the cobro y fallas journey, which writes the
   rehearsal evidence the tool validates (Z4, Z5, Z6, Z7, Z8). See *Synthetic rehearsal* below.
3. **PR-C (this change):** `deploy/azure/pilot/restore-drill.sh` and the OPS-002 CI drill compare the row counts of
   organizations, orders, order events and proof photos, and the drill names the commit its `assert` ran (Z9, Z18).
   See *Restore drill row counts* below.

## Criteria

| Criterion | Source | Passes when | Today |
| --- | --- | --- | --- |
| `full_regression` | AI-08 required test | Foundation CI 13/13 push run on `main` whose head is `tested_git_sha`; REL-000 technical evidence `PASSED` in that run; no failed or skipped test in any execution artifact; every identity of the two closed manifests executed and passed in that run; no `GAP` | Fails: 1 screen and 3 AI-09 scenarios have no test |
| `data_completeness` | AI-02, Z6 | The rehearsal has at least 50 delivered orders and `complete * 100 >= delivered * 98`, and the four journey steps passed | Measured by the rehearsal of each dispatch; `MISSING` if it fails |
| `latency` | AI-02, Z7, Z8 | Each critical screen p95 < 2000 ms and order creation p95 < 3000 ms, at least 100 samples each, nearest-rank | Measured by the rehearsal of each dispatch; `MISSING` if it fails |
| `restore_vigente` | AI-08, Z9 | The pilot drill `Succeeded`, restore point inside the 14-day PITR window, verified 0 to 7 days before `period_start` (before the record exists: before the report time), row counts matched, `assert` ran `tested_git_sha`; the OPS-002 CI drill passed with the same counts | `MISSING` until the owner runs the drill on the pilot; a certified run from before PR-C has no CI counts (`MISSING`) |
| `recipient_notices_line` | AI-08 NTF line | Every switch off, or GATE-004 and GATE-007 both resolved | Passes: all six switches off |
| `release_gates` | AI-10 `release_gates.MVP-1_PILOT` | Each gate has a `Gate resolution` or `Gate scoping` row | Fails: GATE-003, 004, 007, 008, 009, 010 open |
| `delivery_plan` | AI-08, Z12 | The decision record holds 50 to 100 planned deliveries, a period, senders and zones | Pending the owner |
| `owner_approval` | Z10 | The decision record is valid and anchors this run's commit, Foundation run and drill | Pending the owner |

`entry_status` is `NOT_READY` while any technical criterion does not pass or the record is invalid,
`READY_FOR_OWNER_DECISION` when the technical criteria pass and there is no record yet, and `APPROVED_FOR_PILOT_START`
only when everything passes. Approval never authorizes production.

The AI-15 technical levels (`http_p95_ms`, `event_delivery_p95_seconds`, `signed_url_generation_p95_ms`) are reported as
`OK`, `WARNING`, `CRITICAL` or `NOT_MEASURED` and never block. The report says that CI runners do not represent the
pilot's burstable database server.

### Recipient notices (NTF line)

The six switches are `Notifications:RecipientNotices:Enabled`, `Messaging:WhatsApp:Provider`,
`Messaging:Email:Provider`, `Messaging:WhatsApp:Webhook:Enabled`, `Messaging:Email:Events:Enabled` and
`Locations:NoticeKeyProtector`. Each is off when:

- every API and Worker `appsettings*.json` except Development and Testing leaves it absent or off;
- its option default is off (the property has no initializer and the enum's first member is `Disabled`);
- no `deploy/azure/pilot` template sets its environment name (Bicep comments are ignored).

A gate is resolved only by a decision-log row `GATE-NNN-...` of Type `Gate resolution`, the parser ENV-001 uses.

## Closed manifests

- `tests/fixtures/rel-001/browser-suites.json`: every AI-07 route of MVP-0 and MVP-1, plus the MVP-1 screen without a
  route (`external_driver_offers`). Each is `COVERED` by named Playwright classes or a `GAP` with the task that owns it
  (Z2: UI-001, AUTH-001, EXT-001, NTF-006 or REG-001). Today: 17 covered, 1 gap.
- `tests/fixtures/rel-001/ai09-scenario-map.json`: the 81 AI-09 scenarios. Each is `COVERED` by test identities, a
  `GAP` with a reason, or `NOT_APPLICABLE` with a contract reason (V1_SCALE_READINESS, GATE-013, GATE-014, GATE-017,
  MVP-3 allies). Today: 73 covered, 3 gaps, 5 not applicable. `Restaurar un backup` stays a `GAP` that lists its tests:
  since #344 the OPS-002 CI drill runs each of its steps, with the row-count comparison, on a backup it takes itself,
  and no CI test restores a daily backup. The pilot's daily backups (Azure, 14-day PITR) are restored only by
  `restore-drill.sh` on the deployed environment, which this report checks as `restore_vigente`.

An identity is a full test name or a class name (every test of the class). Each one names the job whose execution
artifact must show it passed. The tool's tests check that every .NET identity is still declared under `tests/`, so a
renamed or removed test must update the map in the same PR. When a missing suite lands, its task turns the `GAP` into
`COVERED`.

## Synthetic rehearsal (Z4, Z5, Z6, Z7, Z8)

`tests/Paqueteria.IntegrationTests/PilotRehearsal`, category `PilotRehearsal`, one test
(`PilotRehearsalTests.Rehearsal_runs_the_cobro_y_fallas_journey_and_measures_completeness_and_latency`). It reuses the
OPS-005 harness as it is: PostgreSQL 18/PostGIS with the baseline, the module lanes and one `NOBYPASSRLS` login per
runtime role; the S3-compatible storage container behind its loopback TLS tunnel; the real API on Kestrel and the real
Worker (proof validation with the `Synthetic` scanner, the assignment lifecycle, notifications); the production Next.js
build; Chromium. No Azure (Z4). Synthetic data only: every address, contact and description says SINTETICO and every
phone starts with 0000.

**Only on demand (Z5).** Only the `rehearsal` job of `pilot-readiness.yml` selects the category, on `tested_git_sha`,
after the 13/13 gate. Every `dotnet` job of Foundation CI, PR Validation and the development push smoke excludes it
(`&Category!=PilotRehearsal`), and `tools/rel-001/test_rel001.py` fails if another workflow selects it. Its four pure
tests (`RehearsalEvidenceTests`: percentiles, the evidence shape, the AI-24 screen list) have no category and run in the
`dotnet` job, so the code cannot rot between dispatches.

What it does, all through the API's own endpoints (the screens have their own suites, Z2):

| Step | How |
| --- | --- |
| Regular deliveries (52) | Dispatcher: quote, order, CONFIRMED, READY_FOR_PICKUP, assignment of the own driver (45.00 MXN). Driver: AT_PICKUP, pickup photo, PICKED_UP, IN_TRANSIT, DELIVERING, delivery photo, DELIVERED. Four at a time |
| Cobro (3) | The order declares 250.00 MXN of COD; the driver records exactly that amount before DELIVERED; the dispatcher reconciles it and closes the order |
| Fallas (2) | At the destination the driver reports `RECIPIENT_ABSENT` with a delivery photo as evidence and moves the order to FAILED_ATTEMPT; dispatch decides to retry today (DELIVERING); the driver delivers with a new photo; dispatch resolves the incident (`resolveIncident`, RESOLVED), which the close and the settlement approval require |
| CSV (3) | One file of three rows (each a fresh quote), previewed and committed with the prohibited-goods confirmation; each created order is then delivered |
| Liquidación | Finance (MFA) calculates the driver's settlement for the rehearsal day, approves it, marks it paid and exports the CSV |
| Photos | Each one: `createProofUploadSession` (timed), the signed PUT to storage, the Worker's validation, `finalizeProof` |

The settlement runs on a second API whose clock is two days ahead (an `IClock` replacement through the new optional
`configureTestServices` of the harness), because D7 settles only closed periods and the deliveries happened today.

**Completeness** is read after the run with the administration connection, over the 60 orders that reached DELIVERED:
`order_events` versions exactly 1 to the order version, one `ORDER_STATUS_CHANGED` audit row per status change, and a
`PICKUP_PHOTO` and a `DELIVERY_PHOTO` proof. Each missing count is a number of orders.

**Latency** is measured in Chromium against the production build, 100 samples each, nearest-rank, each sample a full
navigation timed to the first authoritative render: the inbox row of an order waiting for assignment, the order
heading on `/ops/orders/:id`, the driver's stop in `/driver/stops`, the order on `/driver/stops/:id`, and the status
on the public tracking page (the link issued for an assigned order). Order creation is timed from "Crear y confirmar
orden" to "Orden creada y confirmada" in the wizard, so the quote (and its geocoding, an external provider) is left out.
The AI-15 metrics are reported only: the API's request p95 over the whole run (its hosting log lines), the p95 of the
upload-session requests and the p95 from each DELIVERED request to its `OrderStatusChanged` at the operations hub.

**Pool use** is reported only, in the test log and never in the evidence. The test listens to Npgsql's own meter in
the test process, which hosts both the API and the Worker. Every 100 ms it records the peak connections in use, the
peak requests waiting for a connection and the timeouts of the `paqueteria_app`, `paqueteria_realtime` and
`paqueteria_worker` pools (SCL-007), and prints each pool only under its role, never its name. Since the POD-001
per-object scope (#337), proof validation can hold up to `ProofStorage:MaximumConcurrency` Worker connections at once.

**Test-host settings**, as OPS-005 sets its own: Finance on PostgreSQL, the synthetic incident PII protector, and every
SEC-004 permit at its maximum (10,000 per window; public tracking 1,000), so the 100-sample loops measure the screens,
not the rate limiter's waits. Nothing else changes.

## Restore drill row counts (Z9, Z18)

Z18 (project owner, 2026-11-02): "Exacto, en el job (Recommended)". In full, as recorded in the session record: "Solo
para el simulacro, el job de verificación también lee el servidor de origen y compara los 4 conteos en ambos, en solo
lectura; falla ante cualquier diferencia. Antes del arranque no hay tráfico, así que coincide; si el origen cambió, se
repite con un punto de restauración más nuevo."

- **Pilot drill.** Only a drill execution, never `--real`, runs `assert --compare-row-counts-env
  PAQUETERIA_ROW_COUNT_SOURCE_CONNECTION`:
  - It gets one more Key Vault mapping: the verify job's own everyday `pg-migrate-connection`, which points at the
    server in service. That is the same login and privilege the job already holds, so nothing reads with more.
  - After the baseline and lane assertions on the restored server, the migrator counts organizations, orders, order
    events, and `PICKUP_PHOTO` and `DELIVERY_PHOTO` proofs on both servers. Each side is one READ ONLY REPEATABLE READ
    transaction.
  - Any difference fails `assert` with exit 13, and so do a login that is neither superuser nor BYPASSRLS and the same
    database named twice.
  - The job log gets one `ROW_COUNTS_MATCHED` or `ROW_COUNTS_DIFFER` line with the counts only.
  - The job is never given that mapping or argument. The drill starts that one execution with `az containerapp job
    start --yaml` and the job's own template. The only setting it changes on the job, mapping 0 (the drill secret),
    is reset by the cleanup on every exit.
  - Before restoring anything, the drill checks that the job is exactly as deployed; otherwise it stops.
- **The EVIDENCE line** gains `row_counts=MATCHED` (the comparing `assert` succeeded), `UNVERIFIED` (it did not) or
  `NOT_COMPARED` (`--real`). It also gains `asserted_git_sha`: the locked `<tested_git_sha>` tag of the verify job's
  db-ops image digest, or `UNVERIFIED`.
- **The tool:**
  - A line without those tokens is `MISSING`.
  - Any value other than `MATCHED`, or a commit other than `tested_git_sha`, is `FAILED`.
- **OPS-002 CI drill:**
  - `backup-environment.ps1` records the same four counts in its report while the source is quiesced, and the
    fingerprint check shows they did not move.
  - `restore-environment.ps1` counts again in the restored database and fails on any difference.
  - The final report holds `row_counts_source`, `row_counts_restored` and `row_counts_matched`, and
    `test-backup-restore.ps1` requires them equal to the seed (2, 2, 9, 2).
  - The tool reads that report from the certified run's `ops002-backup-restore-results` artifact.

## Inputs and outputs

| Input | Where it comes from |
| --- | --- |
| `tested_git_sha`, `foundation_run_id` | Dispatch inputs; the AZR-001 `deploy-gate` checks them first (13/13 push run on `main`, ancestry) |
| REL-000 report and execution artifacts | Downloaded from that Foundation run. They expire after 7 days (14 for OPS-001/OPS-002), so dispatch over a recent run |
| Rehearsal evidence | `rel001-rehearsal-evidence.json` (`paquetenvia-rel001-rehearsal-v1`), the artifact `rel001-rehearsal-evidence` of the same readiness run's `rehearsal` job |
| Restore drill | The optional `restore_drill_evidence` input: the `EVIDENCE ...` line of `restore-drill.sh`. The execution name is never published |
| Decision record | `docs/releases/mvp-1-pilot-decision.json` on `main` (`paquetenvia-mvp1-pilot-decision-v1`), absent until the owner writes it |

The artifact `rel001-pilot-readiness-report` (14 days) holds exactly `rel001-pilot-readiness-report.json`,
`rel001-regression-evidence.json`, `rel001-data-completeness.json`, `rel001-latency-evidence.json`,
`rel001-restore-evidence.json`, `rel001-delivery-plan.json` and the Spanish summary
`rel001-pilot-readiness-report.md`. Before writing, the tool refuses any output that contains something shaped like an
e-mail address, a phone number, a UUID, a URL, a secret or the drill's execution name. Exit code: 0 ready or approved,
1 report written and not ready, 2 invalid input (nothing written).

### Decision record (Z10, Z12)

```json
{
  "format_version": "paquetenvia-mvp1-pilot-decision-v1",
  "decision_id": "REL-001-ENTRY-001",
  "release": "MVP-1_PILOT",
  "phase": "ENTRY",
  "decision": "APPROVE",
  "decision_statement": "Apruebo el arranque del piloto (REL-001, fase de arranque)",
  "decided_by": "project_owner",
  "decided_on": "YYYY-MM-DD",
  "approvals": {
    "operations": {"approved_by": "project_owner"},
    "finance": {"approved_by": "project_owner"},
    "legal": {"approved_by": "project_owner", "legal_review_by_role": "OWNER_LAWYER"}
  },
  "delivery_plan": {"planned_real_deliveries": 80, "period_start": "YYYY-MM-DD", "period_end": "YYYY-MM-DD",
                    "sender_count": 6, "zone_count": 3},
  "approved_evidence": {"tested_git_sha": "<40 hex>", "foundation_run_id": "<run id>",
                        "restore_drill_verified_at": "YYYY-MM-DDTHH:MM:SSZ"},
  "scope": {"pilot_start_authorized": true, "closing_phase_approved": false, "production_authorized": false,
            "recipient_notices_enabled": false}
}
```

Every key is required and no other key is accepted, so the record cannot hold a name, an address, a phone or an e-mail.
`legal_review_by_role` is `OWNER_LAWYER` or `PROJECT_OWNER`. The record approves a commit, its Foundation run and a drill,
not an artifact digest: the approving run repeats the rehearsal. Once a run reports `APPROVED_FOR_PILOT_START`, its
artifact is frozen under `docs/releases/evidence/` with a manifest of digests, as REL-000 did.

## Owner steps

1. Dispatch *Pilot readiness (REL-001)* from `main` with the certified SHA and its Foundation run id. Read the summary.
2. After ENV-001's first deployment and the last migration, run the restore drill in Cloud Shell and dispatch again with
   its `EVIDENCE` line. It must say `verify_status=Succeeded row_counts=MATCHED asserted_git_sha=<tested_git_sha>`.
3. Write the decision record with the plan counts and the three approvals, merge it to `main`, and dispatch once more
   with the same SHA, run id and drill line, at most 7 days before `period_start`.

## Local run

```bash
python3 -m unittest discover -s tools/rel-001 -p "test_*.py"
```

The tests are offline and synthetic. Foundation's `normative` job and PR Validation's `azr-static` job run them;
the classifier maps `tools/rel-001/**`, `tests/fixtures/rel-001/**` and the workflow to `azr-static`
(`REL001_READINESS`).

The rehearsal needs Docker and the Chromium of the browser suites (OPS-005), like the platform end-to-end tests:

```bash
dotnet build tests/Paqueteria.IntegrationTests/Paqueteria.IntegrationTests.csproj -c Release
pnpm --dir apps/web install --frozen-lockfile
REL001_REHEARSAL_OUTPUT="$PWD/rel001-rehearsal" dotnet test \
  tests/Paqueteria.IntegrationTests/Paqueteria.IntegrationTests.csproj -c Release --no-build \
  --filter "Category=PilotRehearsal"
```

It takes about seven minutes on a workstation and writes `rel001-rehearsal-evidence.json` to that directory (to a new
temporary directory when the variable is empty). Outside the workflow the commit and run fields are placeholders
(forty zeros and run `0`), so a local file never matches a dispatched report.

## Choices not fixed by Z1 to Z12

These are recorded in the decision-log row:

- AI-24 holds the definitions, because no contract defined pilot readiness.
- AI-10 MVP-1_PILOT gates block the approval while open.
- `Locations:NoticeKeyProtector` counts as a recipient-notice switch.
- Blocking latencies need at least 100 samples each, with nearest-rank percentiles.
- The rehearsal needs at least 50 delivered orders.
- The AI-09 map uses the statuses `COVERED`, `GAP` and `NOT_APPLICABLE`.
- An identity may be a full test name or a class name.
- The decision record anchors a commit, a Foundation run and a drill instead of an artifact digest.
- The drill freshness counts from `period_start`, or from the report time before the record exists.
- The legal reviewer roles, the decision statement and the scope flags of the record.
- The artifact is kept 14 days.

The rehearsal (PR-B) adds these, recorded here and in its PR:

- 60 delivered orders: 52 regular, 3 with cash on delivery, 2 with a failed attempt and 3 from a CSV import, four
  regular deliveries at a time.
- The journey goes through the API's endpoints, as OPS-001 does. Only the latency samples use the browser, because the
  screens have their own suites (Z2).
- 250.00 MXN of cash on delivery, the own driver at 45.00 MXN per assignment, and the failed attempt retried the same
  day with its incident resolved after the redelivery.
- The settlement runs on a second API whose clock is two days ahead, through a new optional `configureTestServices` of
  the harness.
- On the test host every SEC-004 permit is at its maximum, and the public tracking lookup at 1,000.
- A screen sample runs from the navigation start to the first authoritative render. Order creation runs from the click
  to the confirmation, without the quote.
- The AI-15 metrics come from the API's hosting log lines, from the upload-session requests and from the operations hub
  client.
- Completeness means contiguous event versions, one audit row per status change and both photos. It is read with the
  administration connection over the delivered orders.
- The evidence is written even in a local run, with placeholder commit and run fields. The workflow keeps it 14 days.
- The rehearsal job checks out `tested_git_sha` with a 75-minute limit. The report still runs when the rehearsal fails
  and then marks completeness and latency `MISSING`.
- The four pure tests have no category, so they run in the `dotnet` job of every PR.
- The rehearsal step logs at `detailed` verbosity, so the passing test's lines (counts, percentiles, pool use) appear in
  the job log. The pool sampler reads Npgsql's meter every 100 ms, so a wait shorter than one sample can be missed.
