# ARC-002 validation report

## Result

| Field | Verified value |
|---|---|
| Date | 2026-07-21 |
| Branch | `fix/arc-002-purge-contract` |
| Status | Remediated and locally validated; `DONE` pending five green CI jobs |
| Image | `postgis/postgis:18-3.6` |
| OCI index digest | `sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d` |
| PostgreSQL | `18.4 (Debian 18.4-1.pgdg13+1)` |
| PostGIS | `3.6.4` |
| AI-06 SHA-256 | `c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96` |
| AI-18 SHA-256 | `7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd` |
| Static ContractTests | 11 passed |
| PostgreSQL ContractTests | 20 passed |
| Complete .NET test run | 52 passed, 0 failed, 0 skipped (Debug and Release) |
| Frontend | lint, typecheck, 1 test, and production build passed |
| Canonical inventory | 72 files verified |
| Local infrastructure | Clean and existing-volume smoke passed; final Down/Reset left 0 resources |
| Package audit | .NET clean; pnpm reports one tracked high `sharp` advisory |
| GitHub Actions | Pending draft PR |

## Gate evidence

| Contract area | Result | Evidence |
|---|---|---|
| Canonical baseline | Pass | `validate_contracts.py` returns `VALIDATION_OK` and verifies 72 manifest entries |
| AI-06 remediation | Pass | Both purge branches omit row locks and recheck ID, terminal state, and cutoff in the target `DELETE` |
| AI-18 identity | Pass | File unchanged and exact required hash retained |
| PostgreSQL baseline | Pass | Disposable tests execute PostgreSQL 18.4 and PostGIS 3.6.4 |
| Empty database bootstrap | Pass | AI-06 then AI-18 execute directly from the canonical files |
| Extensions | Pass | PostGIS is in `public`; pgcrypto is in `extensions` |
| Least privilege | Pass | maintenance has `SELECT,DELETE` without `UPDATE`; Worker has function-only purge access |
| Business outbox purge | Pass | Dry-run, batch deletion, exact counts, state/cutoff preservation, unsafe cutoff, and idempotence |
| Location outbox purge | Pass | Dry-run, batch deletion, exact counts, state/cutoff preservation, unsafe cutoff, and idempotence |
| Concurrent purge | Pass | Two timed connections remove the exact eligible set without double count, deadlock, active-row loss, or residual transaction |
| Other runtime contracts | Pass | Bootstrap, roles, RLS, provisioning, lifecycle, tracking, acceptance, snapshots, and money |
| FND-002 persistence | Pass | Named volume mounts at `/var/lib/postgresql`; smoke preserves data through restart and `Down` |
| CI completion gate | Pending | ARC-002 remains not-DONE until all five draft-PR jobs pass |

## Controlled normative change

The only behavioral SQL changes are inside the real deletion branches of
`security.purge_outbox` and `security.purge_location_outbox`. Candidate locking
was removed because it required an `UPDATE` privilege that ADR-030 intentionally
denies to maintenance. The target `DELETE` now guards against stale candidate
observations by rechecking the ID, terminal state, and relevant cutoff.

No `UPDATE` grant was added. ADR-030 and AI-18 were not modified. The bundle was
reissued as `v0.6-full-canonical-sync-3-arc002-purge-remediation` with a complete
manifest and checksum refresh.

## Local PostgreSQL 18 validation

The first PostgreSQL 18 start correctly rejected the existing PostgreSQL 17
local volume. The authorized `Reset -Force` removed only the four named volumes
for project `paquetenvia-local`. A clean `Up` then initialized PostgreSQL 18,
and the smoke check proved exactly one initialization, persistence through
individual restarts and a non-destructive `Down`/recovery cycle. A second smoke
over the retained volume reported zero reinitializations. Final `Down` followed
by `Reset -Force` left zero project containers and zero project volumes.

AI-06 and AI-18 were executed only in disposable Testcontainers. They were not
applied to the FND-002 persistent database.

## Dependency follow-up

`pnpm audit` remains enabled. The inherited `sharp` advisory
`GHSA-f88m-g3jw-g9cj` is not overridden in this SQL/infrastructure remediation;
it is assigned to a separate GitHub issue with exposure and closure criteria.

## Safety confirmation

- PR #4's implementation and tests are preserved.
- No migration or product database upgrade was created.
- No API or Worker was connected to the persistent database.
- SEC-001, SEC-002, TEN-001, DBA-001, product providers, and business logic were
  not started.
- No merge is performed by this remediation PR.
