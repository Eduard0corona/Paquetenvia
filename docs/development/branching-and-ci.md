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

## Deploy image build smoke (ENV-001 F-05)

The last step of `azr-static`, *Build and smoke-test the deploy images (no push)*, is also the last step of Foundation's `normative` job, with the same body (`SEMANTIC_STEP_OWNER` in `test_workflow_parity.py`). It catches a broken Dockerfile in CI instead of during a pilot deploy. Before it, nothing built these images; for example, both db-ops images could not run the migrator until #299.

- **What it builds:** every Dockerfile under `deploy/azure`. These are the four that `deploy-azure-pilot.yml` builds and pushes (`Dockerfile.api`, `Dockerfile.worker`, `pilot/Dockerfile.db-ops`, `pilot/Dockerfile.web`) and the two DEV-SYNTHETIC ones (`Dockerfile.db-ops`, `Dockerfile.web`).
- **How:** with the deploy's build line, `docker build --pull --file <Dockerfile> --build-arg SOURCE_SHA=<tested sha> ... .`, and the deploy's build arguments. The pilot web gets an empty `NEXT_PUBLIC_TRACKING_SUPPORT_URL`, because there is no support link while GATE-001 is open. The DEV-SYNTHETIC web gets `NEXT_PUBLIC_API_BASE_URL=https://api.example.invalid`: its Dockerfile always defines that variable, and `next.config.ts` refuses an empty or non-HTTPS value.
- **Checks:** each image then runs with `--network none`. `id -u` must print a number other than 0. Both db-ops images must pass the migrator's `verify`, which needs no database and exits 0 only when every migration source the migrator reads is in the image.
- **What it never does:** push, log in or contact Azure. It also removes its images when it ends.
- **No cross-run cache:** the repository pins no cache action. Inside the step, Docker's own layer cache lets the second web build reuse the first one's `pnpm install`.
- **When it runs:**
  - on every Foundation run;
  - in PR Validation, whenever `azr-static` runs. Besides the Dockerfiles (`AZURE`) and every web path (`WEB_*`), the `IMAGE_BUILD_INPUT` domain selects `azr-static` for `src/**`, `database/**`, `Paqueteria.sln`, `Directory.Build.props`, `.dockerignore`, `tools/Paqueteria.DatabaseMigrator/**` and `tools/Paqueteria.DevSeed/**`. `DEPS` and unmatched paths are `FULL`, so they run it too;
  - not for backend paths that no image reads, such as integration tests, compose files or `tools/*.ps1`.
- **Tests:**
  - `ImageBuildInputTests` in `tools/ci/test_classify_changes.py` proves that every tracked file the images are built from selects `azr-static`, and that the .NET project closure and every `COPY` source stay inside those inputs;
  - `tools/ci/test_image_build_smoke.py` keeps the list equal to the Dockerfiles in the repository and to the pilot workflow's builds, and runs the real step with a stand-in `docker`.
- **Cost (measured on PR #303):**
  - the step took 221 s and 162 s on the two runs, so about 3 to 4 minutes. On the first run the images took: API 48 s, Worker 28 s, pilot db-ops 36 s, DEV db-ops 36 s, pilot web 43 s, DEV web 30 s. The two DEV-SYNTHETIC images add about a minute;
  - `azr-static` goes from about 1.6 minutes to 3.9–5.8 minutes. That is still shorter than the slowest PR Validation job (about 7 minutes), so the wall time of a run does not grow;
  - Foundation's `normative` job (about 1 minute on the last `push`/`main` run) grows by the same 3 to 4 minutes, well inside its 15-minute timeout;
  - the runner had 80 GB of disk free after the six builds.
- **Pending owner decisions (not done here):** pinning the base images by digest, with a refresh process; image scanning; ACR tag locks; reusing earlier digests for a rollback. Until then CI and the deploy each `--pull` the floating base tags, so the deploy can build on a newer base than CI tested.

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
- an optional expiry;
- optional **exact baseline amendments**, when restoring the admitted packages unavoidably
  changes baseline lock nodes (each entry names one lock file, framework, package, version and
  content hash; no wildcards):
  - `additional_package_versions`: one more exact version of a package another admission
    already admits, as a transitive node, only in the named lock files (for example MSAL's
    `Microsoft.IdentityModel.Abstractions` 8.14.0 where AUTH-001-OIDC admits 8.19.2);
  - `baseline_package_upgrades`: one baseline node moved from an exact version and hash to an
    exact version and hash that this admission admits, keeping its node type. A node that an
    earlier admission already upgraded may only be moved again as the next exact step of that
    chain: same lock file, framework and id, starting at the earlier step's exact version and
    hash, never revisiting a version or content hash (GOV-REL000-CHAINED-UPGRADES-2026-10-12). An upgrade that
    lands on this admission's declared additional version delivers that version in that lock
    file;
  - `baseline_package_removals`: one exact baseline node removed from one lock file (a node the
    SDK prunes). Other baseline nodes of that lock file may then lose dependency edges to the
    removed nodes, and nothing else.

The admission is bound to the branch and the package set, not to a base SHA, because `main` keeps moving.

Order of operations:

1. Register the admission in a PR into `development` (`status: ACTIVE`). Promote `development` → `main`. Both `main_source_guard.py` and REL-000 read the policy **from the tested base**, so a dependency PR can never admit itself.
2. The dependency PR from the authorized branch → `main`. REL-000 runs in `NORMAL_RELEASE_EVIDENCE`, with no remediation ID and no workflow change. It accepts the diff only if it is exactly the admitted set:
   - `Directory.Packages.props` = baseline + the admitted `PackageVersion` lines;
   - every lock file = baseline nodes, unchanged, + the admitted nodes, except the declared
     amendments above;
   - every declared amendment is actually delivered (an unused declaration fails as incomplete);
   - only allowed files are changed, and every required file is present.
3. Back-sync (`MAIN_BACKSYNC`) as above.
4. A follow-up PR into `development` flips the admission to `status: MERGED`. From then on the branch no longer opens PRs into `main`, but the packages stay admitted permanently: every later REL-000 run still compares against the MVP-0 baseline plus the admitted set.

Anything unregistered still fails closed: an extra transitive package, another version or content hash, an undeclared change or removal of an existing package, a declared amendment in another lock file or framework, two admissions amending the same node other than as an exact upgrade chain (`DEPENDENCY_ADMISSION_AMENDMENT_CONFLICT` when the policy loads), or `Directory.Packages.props` changed without an admission. `PR Gate` still rejects `DEPS` into `development`. `MAIN_BACKSYNC` remains the only way dependencies reach `development`. Details and reason codes: `docs/development/rel-000-internal-release.md`.

## Re-running

- **PR Validation / Foundation on a PR:** Actions → run → *Re-run failed jobs* (or *Re-run all jobs*). `rel000` accepts a partial re-run: it correlates artifacts from the current attempt. To force `FULL` on a task PR, add the `full-ci` label. The `labeled` event re-runs the workflow.
- **Development Push Validation:** *Run workflow* (`workflow_dispatch`) on `development`, or *Re-run* on the run.
- **Foundation push/main:** re-run from Actions. For a deploy, use the `id` of the green run whose `head_sha` is the commit to deploy.
- **Deploy (`deploy-azure-dev.yml`):** `workflow_dispatch` from `main` only. `tested_git_sha` must match `^[0-9a-f]{40}$` and `foundation_run_id` must match `^[0-9]+$`. The inputs are validated before use and reach the shells only through `env:`.
- **Pilot deploy (`deploy-azure-pilot.yml`, ENV-001):**
  - Runs by `workflow_dispatch` from `main` only, with the same `tested_git_sha` / `foundation_run_id`
    inputs and the same 13/13 gate.
  - Needs the `azure-pilot` GitHub Environment, owner-recorded GATE-007/GATE-012 decision ids, and
    `deploy/azure/pilot/apps.settings.json`, `observability.parameters.json` and `web.parameters.json`
    without `OWNER_DECISION_REQUIRED`.
  - Runbook: `docs/operations/env-001-pilot/README.md`.
  - Its templates and workflow belong to the `AZURE` / `DEPLOY_WORKFLOW` domains, so `azr-static` checks
    them. The Bicep build/lint step covers both `deploy/azure/*.bicep` and `deploy/azure/pilot/*.bicep`.
    Pilot templates are built into their own directory, and any pilot lint diagnostic, warning
    included, fails the step. `azr-static` also runs the pilot guard tests
    (`tools/azr-001/test_env001_pilot_guards.py`). The same step is in Foundation's `normative` job.
  - `azr-static` also builds and smoke-tests the four images the pilot deploys (see *Deploy image build smoke*). After
    stage 4 the deploy checks that the new API, Worker and Web revisions are ready, active, `Healthy` and on the digests
    it pushed (guard P26). The run summary lists those digests.
- **Locally:** `python3 -m unittest discover -s tools/ci -p "test_*.py"`, `python3 -m unittest discover -s tools/azr-001 -p "test_*.py"`, `python3 docs/normative/v0.6/tools/validate_contracts.py`.

## Workflow conventions

- Every job declares `timeout-minutes`.
- Every job pins `runs-on: ubuntu-24.04`, never `ubuntu-latest`. The pin is on purpose: GitHub moves `ubuntu-latest` to Ubuntu 26 from 2026-10-19, and the runner image must not change underneath a gate. `tools/ci/test_foundation_invariants.py` (`GITHUB_HOSTED_LINUX_RUNNER`) checks every job of every workflow. Moving to 26.04 is a later, deliberate PR that changes all jobs and that constant together, with a full run as evidence.
- Third-party actions outside `actions/*` are pinned by commit SHA with the tag in a comment (`uses: owner/action@<sha> # vX`). To update one, resolve the tag with `git ls-remote https://github.com/<owner>/<action> 'refs/tags/<tag>*'` and use the `^{}` value if the tag is annotated.
- Dispatch inputs and event text are never interpolated with `${{ }}` inside `run:`. They go through `env:` and are validated first.
