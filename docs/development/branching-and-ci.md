# Branching and CI

How changes move from a task branch to `main`, which workflow validates each step, and how to re-run it.

## Branches

| Branch | Role | Who writes |
|---|---|---|
| `feature/*`, `fix/*` | One backlog task per branch/PR | Agents and developers (PRs in **draft**) |
| `development` | Integration branch | Only merges of PRs validated by PR Validation |
| `main` | Certified baseline; the only source for `deploy-azure-dev.yml` and `deploy-azure-pilot.yml` | Only promotion PRs from `development`, the dependency route and authorized security remediations |

Nobody pushes directly to `main` or `development`, and nobody rebases or force-pushes shared branches. Only the owner merges.

## Normal flow: task → development → main

1. Task PR (`feature/*` → `development`). **PR Validation** (`pr-validation.yml`) runs:
   - `classify` runs the classifier (`tools/ci/classify_changes.py` + `tools/ci/change-domains.json`) from the trusted first parent `github.sha^1`. A PR cannot rewrite the rules that judge it.
   - The plan is `SELECTIVE`, `FULL` or `MAIN_BACKSYNC`. Only the jobs in `plan.required_jobs` run. Unmatched paths, CI self-changes (`CI_SELF`), dependencies (`DEPS`) and the `full-ci` label force `FULL`.
   - `PR Gate` is the single terminal check. It rejects `DEPS` changes into `development` (`PR_GATE_DEPENDENCY_CHANGE_NOT_ALLOWED`).
   - PR Validation is **not authoritative**. It produces no release evidence.
2. After merge, **Development Push Validation** (`development-push.yml`) re-validates the new `development` head. It covers normative contracts, CI/AZR tooling tests, a secret scan of the pushed range, .NET build and unit/architecture/non-PostgreSQL contract tests. It is a smoke check that catches bad merge sequences, not a gate.
3. Promotion PR (`development` → `main`). **Foundation CI** (`ci.yml`, exactly 13 jobs, `rel000` last) runs on `pull_request` → `main`. It is the authoritative gate. The PR body carries the audit: included PRs, files per PR, binaries, dependencies, normative checksums and branch divergence.
4. After the merge, Foundation CI runs again on `push` → `main`. That run (13/13 green) is the **certification** of the `main` head. `deploy-azure-dev.yml` requires it (`foundation_run_id` + `tested_git_sha`).

`main` is normally one merge commit ahead of `development` after each promotion. That commit has the same tree as the promoted `development` head, so the divergence is topological only. It is not synchronized back unless the dependency route below requires it.

## PR Validation vs Foundation CI

| | PR Validation | Foundation CI |
|---|---|---|
| Trigger | `pull_request` → `development` | `pull_request` → `main`, `push` → `main` |
| Topology | 17 jobs (normative split into `secret-scan`, `normative-contracts`, `azr-static`, plus `classify` and `pr-gate`) | 13 fixed jobs |
| Selection | Per plan (change-aware) | Always all |
| Authority | Informational for merging into `development` | Promotion gate and certification of `main` |

The 11 shared jobs must be identical in both files except for orchestration keys (`needs`, `if`). This includes `timeout-minutes`, steps, `uses` and `env`. `tools/ci/test_workflow_parity.py` enforces it. Any change to a shared job goes into **both** files in the same PR.

## Dependency route through main (MAIN_BACKSYNC)

Dependency changes (`DEPS`: `Directory.Packages.props`, `packages.lock.json` with package changes, `package.json`, `pnpm-lock.yaml`, `global.json`, `.nvmrc`, …) **never go through `development`**:

1. Dependency PR → `main`, from an authorized security remediation branch or from the `authorized_source_branch` of an `ACTIVE` dependency admission (see `tools/rel-000/security-remediation-policy.json` and `tools/ci/main_source_guard.py`). Foundation CI must pass 13/13 on the PR, and then on `push` → `main`.
2. Back-sync PR with head `main` → base `development`. The `classify` job looks up the exact Foundation `push`/`main` run whose `head_sha` equals the `main` head, with all 13 jobs `success`. If it exists, it passes `--certified-main-sha` and the plan becomes `MAIN_BACKSYNC`: every job runs except `rel000`, and `PR Gate` accepts the `DEPS` domain. Without that certification, the plan is `FULL` and `PR Gate` rejects the dependencies.

A lock file that only changes the internal graph (`type: Project`) is proven by content (`NUGET_PROJECT_GRAPH`) and does not count as `DEPS`.

### Explicit dependency admission (GOV-DEPENDENCY-ADMISSION-001)

A **new** package (MVP-1 onward) needs an owner-approved entry in `dependency_admissions` (policy format v3, `mode: DEPENDENCY_ADMISSION`) **before** its dependency PR can pass. The entry registers:

- each package id with its exact version and NuGet content hash (`admitted_direct_packages`, `admitted_transitive_packages`);
- the authorized source branch;
- the allowed and required dependency files, plus the `.csproj` files that may gain the `PackageReference`;
- the owner decision ID;
- an optional expiry.

The admission is bound to the branch and the package set, not to a base SHA, because `main` keeps moving.

Order of operations:

1. Register the admission in a PR into `development` (`status: ACTIVE`). Promote `development` → `main`. Both `main_source_guard.py` and REL-000 read the policy **from the tested base**, so a dependency PR can never admit itself.
2. The dependency PR from the authorized branch → `main`. REL-000 runs in `NORMAL_RELEASE_EVIDENCE`, with no remediation ID and no workflow change. It accepts the diff only if it is exactly the admitted set:
   - `Directory.Packages.props` = baseline + the admitted `PackageVersion` lines;
   - every lock file = baseline nodes, unchanged, + the admitted nodes;
   - only allowed files are changed, and every required file is present.
3. Back-sync (`MAIN_BACKSYNC`) as above.
4. A follow-up PR into `development` flips the admission to `status: MERGED`. From then on the branch no longer opens PRs into `main`, but the packages stay admitted permanently: every later REL-000 run still compares against the MVP-0 baseline plus the admitted set.

Anything unregistered still fails closed: an extra transitive package, another version or content hash, a change or removal of an existing package, or `Directory.Packages.props` changed without an admission. `PR Gate` still rejects `DEPS` into `development`. `MAIN_BACKSYNC` remains the only way dependencies reach `development`. Details and reason codes: `docs/development/rel-000-internal-release.md`.

## Re-running

- **PR Validation / Foundation on a PR:** Actions → run → *Re-run failed jobs* (or *Re-run all jobs*). `rel000` accepts a partial re-run: it correlates artifacts from the current attempt. To force `FULL` on a task PR, add the `full-ci` label. The `labeled` event re-runs the workflow.
- **Development Push Validation:** *Run workflow* (`workflow_dispatch`) on `development`, or *Re-run* on the run.
- **Foundation push/main:** re-run from Actions. For a deploy, use the `id` of the green run whose `head_sha` is the commit to deploy.
- **Deploy (`deploy-azure-dev.yml`):** `workflow_dispatch` from `main` only. `tested_git_sha` must match `^[0-9a-f]{40}$` and `foundation_run_id` must match `^[0-9]+$`. The inputs are validated before use and reach the shells only through `env:`.
- **Pilot deploy (`deploy-azure-pilot.yml`, ENV-001):**
  - Runs by `workflow_dispatch` from `main` only, with the same `tested_git_sha` / `foundation_run_id`
    inputs and the same 13/13 gate.
  - Needs the `azure-pilot` GitHub Environment, owner-recorded GATE-007/GATE-012 decision ids, and
    `deploy/azure/pilot/apps.settings.json` without `OWNER_DECISION_REQUIRED`.
  - Runbook: `docs/operations/env-001-pilot/README.md`.
  - Its templates and workflow belong to the `AZURE` / `DEPLOY_WORKFLOW` domains, so `azr-static` checks
    them. The Bicep build/lint step covers both `deploy/azure/*.bicep` and `deploy/azure/pilot/*.bicep`.
    Pilot templates are built into their own directory, and any pilot lint diagnostic, warning
    included, fails the step. `azr-static` also runs the pilot guard tests
    (`tools/azr-001/test_env001_pilot_guards.py`). The same step is in Foundation's `normative` job.
- **Locally:** `python3 -m unittest discover -s tools/ci -p "test_*.py"`, `python3 -m unittest discover -s tools/azr-001 -p "test_*.py"`, `python3 docs/normative/v0.6/tools/validate_contracts.py`.

## Workflow conventions

- Every job declares `timeout-minutes`.
- Third-party actions outside `actions/*` are pinned by commit SHA with the tag in a comment (`uses: owner/action@<sha> # vX`). To update one, resolve the tag with `git ls-remote https://github.com/<owner>/<action> 'refs/tags/<tag>*'` and use the `^{}` value if the tag is annotated.
- Dispatch inputs and event text are never interpolated with `${{ }}` inside `run:`. They go through `env:` and are validated first.
