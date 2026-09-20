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
