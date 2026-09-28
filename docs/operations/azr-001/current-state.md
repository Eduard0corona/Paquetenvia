# AZR-001 current operational state

This is the current-state overlay recorded on 2026-09-19. It is intentionally separate from the recovered historical artifacts in this directory.

```yaml
contract_family:
  azr_001: v0.8
  e_001: v0.3
  azr_001_dec_001:
    status: APPROVED
    scope: DEV_SYNTHETIC_ONLY
  e_002: v0.13.1
  e_002_owner_approved: true

governance:
  implementation_authorized: true
  merge_authorized: false
  auto_merge_authorized: false
  production_authorized: false
  staging_authorized: false
  real_data_authorized: false
  go_live_authorized: false

validated_source_head: 4b27725e6bbe7e5ac80f8a2f17af9593f82414d2
validated_foundation_run: 35433745649
validated_foundation_result: 13 / 13 SUCCESS

operational_evidence:
  signalr_foundation_blocker: RESOLVED
  e_002_retained_database_acceptance: PASS
  notifications_migrations: 9 / 9 APPLIED
  azure_clean_path: NOT_YET_EXERCISED_ON_AZURE
```

The recovered `e-002-v0.13.1.md` remains the historical pre-approval artifact and therefore still says `owner_approved = false` and `implementation_authorized = false`. The later Owner approval and current implementation authorization are recorded here instead of altering that source.

The documentation-only commit that adds this directory is a subsequent commit. Its SHA is established by Git history and is deliberately not embedded in the same commit. The normal Foundation run triggered after push is the validation record for the final PR HEAD.

No merge, auto-merge, staging or production action, real-data use, deployment, Azure mutation, database mutation, or migration execution is authorized by this record.

## Update recorded on 2026-09-26

The 2026-09-19 overlay above is kept as written. Two statements in it are now superseded by facts:

- `merge_authorized: false` described the state before merge. PR #59 was merged into `main` on 2026-09-20 and PR #60 on 2026-09-20, both by the project owner, although their bodies stated that merge was not authorized. PR #61 was merged on 2026-09-21 under a controlled owner authorization tied to head `e1ebce8` and Foundation run 35568160027 (13/13). The formal merge authorization for #59 and #60 is recorded as the open question `GOV-2026-09-MERGE-AUTHORIZATION` in `docs/normative/v0.6/decision-log.md`.
- `notifications_migrations: 9 / 9 APPLIED` mislabels the evidence. PR #59 reports **9/9 module migration lanes APPLIED**, of which the Notifications lane is one (`Notifications: APPLIED`); it is not a count of Notifications migrations.

```yaml
update_2026_09_26:
  merged:
    - {pr: 59, target: main, merged_on: 2026-09-20, merge_authorization_in_pr_body: false}
    - {pr: 60, target: main, merged_on: 2026-09-20, merge_authorization_in_pr_body: false}
    - {pr: 61, target: main, merged_on: 2026-09-21, merge_authorization_in_pr_body: conditional_owner_authorization}
  operational_evidence_correction:
    module_migration_lanes_at_pr59: 9 / 9 APPLIED (Notifications lane included)
  still_not_authorized:
    - deployment
    - staging
    - production
    - real_data
    - go_live
  azure_clean_path: NOT_YET_EXERCISED_ON_AZURE
  later_lanes_not_covered_by_bridge:
    - LIF-001 paqueteria_lifecycle_executor ownership transfer (PR #83 follow-up)
```

The later module lanes (INC-001, SCL-001 Data Protection, SET-001 Finance, LIF-001 Orders) were added after the retained-database run; the 9/9 figure is not current for `development`.

## Update recorded on 2026-09-27 (ENV-001)

The 2026-09-26 entry `later_lanes_not_covered_by_bridge` is superseded by the code. Commit `5218068`
("fix(lif-001): align lifecycle executor with Azure bridge") added `paqueteria_lifecycle_executor` to
the E-002 bridge. The bridge now also covers the cleanup, registration and session executors
(`E002Guards.CanonicalRoles` and `SpecializedOwners`). ENV-001 adds a contract test that fails if
any privileged `NOLOGIN` role declared by AI-18, or re-declared by a module adoption lane, is left
out of the bridge (`PilotAzureOwnershipBridgeContractTests`).

The bridge is still exercised only by the canonical migrator. It still has no Azure evidence for the
Clean path.

```yaml
update_2026_09_27:
  later_lanes_not_covered_by_bridge: []
  bridge_role_coverage_test: tests/Paqueteria.ContractTests/PostgreSql/PilotRuntimeLoginContractTests.cs
  azure_clean_path: NOT_YET_EXERCISED_ON_AZURE
  note: ENV-001 (PILOT-REAL-PEOPLE) is a separate environment documented in docs/operations/env-001-pilot/; nothing here authorizes it.
```
