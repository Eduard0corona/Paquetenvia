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

1. **PR-A (this change):** the AI-08 criterion, AI-24 `pilot_readiness`, the decision-log row, the tool, its closed
   manifests and the workflow. The rehearsal and the drill row counts do not exist yet, so the report marks them
   `MISSING` and can never pass.
2. **PR-B:** the local/CI synthetic rehearsal with the cobro y fallas journey, which writes the rehearsal evidence the
   tool already validates (Z4, Z5, Z6, Z7, Z8).
3. **PR-C:** `deploy/azure/pilot/restore-drill.sh` and the OPS-002 CI drill compare the row counts of organizations,
   orders, order events and proof photos, and the drill names the commit its `assert` ran (Z9).

## Criteria

| Criterion | Source | Passes when | Today |
| --- | --- | --- | --- |
| `full_regression` | AI-08 required test | Foundation CI 13/13 push run on `main` whose head is `tested_git_sha`; REL-000 technical evidence `PASSED` in that run; no failed or skipped test in any execution artifact; every identity of the two closed manifests executed and passed in that run; no `GAP` | Fails: 7 screens and 3 AI-09 scenarios have no test |
| `data_completeness` | AI-02, Z6 | The rehearsal has at least 50 delivered orders and `complete * 100 >= delivered * 98`, and the four journey steps passed | `MISSING` until PR-B |
| `latency` | AI-02, Z7, Z8 | Each critical screen p95 < 2000 ms and order creation p95 < 3000 ms, at least 100 samples each, nearest-rank | `MISSING` until PR-B |
| `restore_vigente` | AI-08, Z9 | The pilot drill `Succeeded`, restore point inside the 14-day PITR window, verified 0 to 7 days before `period_start` (before the record exists: before the report time), row counts matched, `assert` ran `tested_git_sha`; the OPS-002 CI drill passed with the same counts | `MISSING` until PR-C |
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
  (Z2: UI-001, AUTH-001, EXT-001, NTF-006 or REG-001). Today: 11 covered, 7 gaps.
- `tests/fixtures/rel-001/ai09-scenario-map.json`: the 81 AI-09 scenarios. Each is `COVERED` by test identities, a
  `GAP` with a reason, or `NOT_APPLICABLE` with a contract reason (V1_SCALE_READINESS, GATE-013, GATE-014, GATE-017,
  MVP-3 allies). Today: 73 covered, 3 gaps, 5 not applicable.

An identity is a full test name or a class name (every test of the class). Each one names the job whose execution
artifact must show it passed. The tool's tests check that every .NET identity is still declared under `tests/`, so a
renamed or removed test must update the map in the same PR. When a missing suite lands, its task turns the `GAP` into
`COVERED`.

## Inputs and outputs

| Input | Where it comes from |
| --- | --- |
| `tested_git_sha`, `foundation_run_id` | Dispatch inputs; the AZR-001 `deploy-gate` checks them first (13/13 push run on `main`, ancestry) |
| REL-000 report and execution artifacts | Downloaded from that Foundation run. They expire after 7 days (14 for OPS-001/OPS-002), so dispatch over a recent run |
| Rehearsal evidence | `rel001-rehearsal-evidence.json` (`paquetenvia-rel001-rehearsal-v1`) from the same readiness run (PR-B) |
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
   its `EVIDENCE` line (PR-C adds the row counts).
3. Write the decision record with the plan counts and the three approvals, merge it to `main`, and dispatch once more
   with the same SHA, run id and drill line, at most 7 days before `period_start`.

## Local run

```bash
python3 -m unittest discover -s tools/rel-001 -p "test_*.py"
```

The tests are offline and synthetic. Foundation's `normative` job and PR Validation's `azr-static` job run them;
the classifier maps `tools/rel-001/**`, `tests/fixtures/rel-001/**` and the workflow to `azr-static`
(`REL001_READINESS`).

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
