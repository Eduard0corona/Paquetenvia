# OBS-003: dependency admission for OpenTelemetry and the Azure Monitor exporter

Phase 4, PR 4 (`docs/development/phase4-platform-plan.md`). This PR only registers the admission entry
`OBS-003-OPENTELEMETRY` in `dependency_admissions` of `tools/rel-000/security-remediation-policy.json`
(policy format v3, `status: ACTIVE`). It adds no `PackageReference`, no `PackageVersion` and no lock file. The
packages arrive later, in the dependency PR into `main` (PR 5).

Decisions (`decision-log.md`):

- `OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12`: Application Insights through the official Azure exporter. The row
  says the exact package list still needs its explicit per-package admission.
- `GOV-REL000-CHAINED-UPGRADES-2026-10-12`: REL-000 accepts exact upgrade chains across admissions (#239).
  Without it, the Azure.Core move could not be registered.
- `OBS-003-TEST-EXTENSIONS-ADMISSION-2026-10-12`: the five `Microsoft.Extensions.*` 10.0.0 packages of the
  ArchitectureTests lock join the list.
- Under `GOV-DEPENDENCY-ADMISSION-001` the owner admits the exact list below, package by package, when approving this
  PR. That admission is recorded in its own `decision-log.md` row before the merge.

## Packages

Two packages are referenced directly, only by the two hosts (`src/Paqueteria.Api/Paqueteria.Api.csproj` and
`src/Paqueteria.Worker/Paqueteria.Worker.csproj`):

- `OpenTelemetry.Extensions.Hosting` is the SDK host integration.
- `Azure.Monitor.OpenTelemetry.Exporter` is the official exporter.

Not used:

- the distro `Azure.Monitor.OpenTelemetry.AspNetCore`;
- instrumentation packages: the built-in .NET 10 `Microsoft.AspNetCore` and `System.Net.Http` ActivitySources and
  Meters are enough.

Versions are the latest stable on nuget.org. Exporter 1.10.0 (2026-10-07) is the first stable release that needs
Azure.Core 1.63.0; 1.9.0 needs 1.62.0. No version listed here has a NuGet advisory. Content hashes are copied
exactly from the restored `packages.lock.json` files.

| Package | Version | New or upgrade | Role | Lock files | NuGet content hash (SHA-512, base64) |
| --- | --- | --- | --- | --- | --- |
| `Azure.Monitor.OpenTelemetry.Exporter` | 1.10.0 | new | direct (Api, Worker) | all four | `39Vc71fy1KYqU70j3sbGOqUU6wT5/eKskEuoW8R+H+J+srIyCewN6ooMTj+6lU4yFpJ0x5da2jx1GK9h48g6iw==` |
| `OpenTelemetry.Extensions.Hosting` | 1.19.1 | new | direct (Api, Worker) | all four | `3qqZfVmW8mKA2/fRwI83o8z+yZd33KbEqRlXrlX0+3e/U7i+z0MQiCr0L3Axxa0jGdiylIrpWX4DIT9nAbJ00A==` |
| `Microsoft.Extensions.Configuration` | 10.0.0 | new | transitive | ArchitectureTests | `H4SWETCh/cC5L1WtWchHR6LntGk3rDTTznZMssr4cL8IbDmMWBxY+MOGDc/ASnqNolLKPIWHWeuC1ddiL/iNPw==` |
| `Microsoft.Extensions.Configuration.Binder` | 10.0.0 | new | transitive | ArchitectureTests | `tMF9wNh+hlyYDWB8mrFCQHQmWHlRosol1b/N2Jrefy1bFLnuTlgSYmPyHNmz8xVQgs7DpXytBRWxGhG+mSTp0g==` |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | 10.0.0 | new | transitive | ArchitectureTests | `TmFegsI/uCdwMBD4yKpmO+OkjVNHQL49Dh/ep83NI5rPUEoBK9OdsJo1zURc1A2FuS/R/Pos3wsTjlyLnguBLA==` |
| `Microsoft.Extensions.Logging.Configuration` | 10.0.0 | new | transitive | ArchitectureTests | `j8zcwhS6bYB6FEfaY3nYSgHdpiL2T+/V3xjpHtslVAegyI1JUbB9yAt/BFdvZdsNbY0Udm4xFtvfT/hUwcOOOg==` |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | 10.0.0 | new | transitive | ArchitectureTests | `tL9cSl3maS5FPzp/3MtlZI21ExWhni0nnUCF8HY4npTsINw45n9SNDbkKXBMtFyUFGSsQep25fHIDN4f/Vp3AQ==` |
| `OpenTelemetry` | 1.19.1 | new | transitive | all four | `6ywhc3upf+dq8EsOohj7I7zS3l4yX9vfbtFkvyEC8xIUY4Z9iDIOVuke356eCAzIZaWtmwmmyl2UXYCp5n7I3Q==` |
| `OpenTelemetry.Api` | 1.19.1 | new | transitive | all four | `9JKIH+xrV0HNTFjQC8zrx9gZpDOMEljN5Htg/U0VE2IvO3N+7bKALCCsGy5tp09marX5dd8UkrgQFoLaulIBRw==` |
| `OpenTelemetry.Api.ProviderBuilderExtensions` | 1.19.1 | new | transitive | all four | `HtGe72B/IhzKqdU0a3fNE5osax/Cegwxd+p4u6lOFy20nsgmeaFuP2NRyFs1WvrOv/bpkII3IvvBkKoZXG8BLg==` |
| `OpenTelemetry.PersistentStorage.Abstractions` | 1.1.1 | new | transitive | all four | `tsZtLJZZNYf7HIZZGm/DcYdQWKye0shFoekx59TkSkmXPB0u8bUXJZMYsdDIId+nyXcwTFdBlv5z0SQHOp+Xxg==` |
| `OpenTelemetry.PersistentStorage.FileSystem` | 1.1.1 | new | transitive | all four | `f66+dhmMQgs4uYN+IbssgGRiINyw639CPWIPHt0+92Jq/VewNm7uZ/sSxQpWVorY/rmvDxfNqL1vzLebfZcczw==` |
| `Azure.Core` | 1.61.0 → 1.63.0 | upgrade (ADP-001-AZURE) | transitive | all four | `5egODg5yWcRV0kkAv22jQL7YmsfupX7intDYbxZMu8giASSQVfG5+mwC+BdJOWvhFT1Y+9KzuvbUGqAVEBzsfw==` |
| `Microsoft.Bcl.AsyncInterfaces` | 10.0.9 → 10.0.10 | upgrade (ADP-001-AZURE) | transitive | all four | `TFI6OKYE1XZz4SGuTSH70c6SBdPpFktXsoa1gCxTr3mKrhmXirnvaS0tKz+J3ZWICEAmMpEGn59nO4ICtUpQXA==` |
| `Microsoft.Identity.Client` | 4.84.2 → 4.90.0 | upgrade (ADP-001-AZURE) | transitive | all four | `oc5A0jmu083T3OW1JVYMrM1rekIL8SkBRB/byMMQjFuYlRZsMh3wDeCwwrz9sGI6XlP5BIqO7XWgjPpF3reF5A==` |
| `Microsoft.Identity.Client.Extensions.Msal` | 4.84.2 → 4.90.0 | upgrade (ADP-001-AZURE) | transitive | all four | `/L7b+fNF+ITSw3SPlbgE7ax2MqVg/a60kHsDooKUbjYDdX88COTTwE20qRPT84fykehBTSmP7LSxh6b5hwIEOw==` |
| `System.ClientModel` | 1.15.0 → 1.16.0 | upgrade (ADP-001-AZURE) | transitive | all four | `vLljZON+8lXDUtsPP8uwsPvsBSQaUHhh8NvB+D9TQsXu5n622RLtuLyZAcyIFc98b8ixrwD0AhzUQhtg2NwJnQ==` |
| `System.Memory.Data` | 10.0.9 → 10.0.10 | upgrade (ADP-001-AZURE) | transitive | all four | `roR+5AeOpflUxJQlqSfsZs/Bvi1uw4KBuTkIgF1e8l0RdalG4AUn84+sjJLclEfOV85gAjc8+Pp540qjDE5UIg==` |

In the ArchitectureTests and IntegrationTests locks, the two direct packages appear as `CentralTransitive` nodes
(through the project references to the hosts). REL-000 accepts that only for admitted direct packages.

Why each group is here:

- **OpenTelemetry, OpenTelemetry.Api, OpenTelemetry.Api.ProviderBuilderExtensions:** the SDK under
  `OpenTelemetry.Extensions.Hosting`.
- **OpenTelemetry.PersistentStorage.FileSystem and .Abstractions:** the exporter's local retry storage.
- **The five `Microsoft.Extensions.*` 10.0.0:** OpenTelemetry 1.19.1 depends on them.
  - In Api and Worker, the ASP.NET Core shared framework prunes them.
  - IntegrationTests already has them at 10.0.10.
  - ArchitectureTests is a plain `Microsoft.NET.Sdk` project without that framework, so it gains them at the
    minimum version OpenTelemetry declares. Only that test lock changes; nothing from it is deployed.
- **The six upgrades:** Azure.Core 1.63.0 itself raises the other five. ADP-001-AZURE admitted all six at the
  "from" versions.

## Lock files

| Lock file | Change |
| --- | --- |
| `src/Paqueteria.Api/packages.lock.json` | 2 direct + 5 OpenTelemetry transitives; 6 upgrades |
| `src/Paqueteria.Worker/packages.lock.json` | 2 direct + 5 OpenTelemetry transitives; 6 upgrades |
| `tests/Paqueteria.ArchitectureTests/packages.lock.json` | 2 central-transitive + 5 OpenTelemetry + 5 `Microsoft.Extensions.*` 10.0.0; 6 upgrades |
| `tests/Paqueteria.IntegrationTests/packages.lock.json` | 2 central-transitive + 5 OpenTelemetry transitives; 6 upgrades |

- No node is removed, in any lock file.
- Every other lock file is unchanged. Their projects do not reference a host, so they keep Azure.Core 1.61.0 and
  the other ADP-001 versions.
- `tests/Paqueteria.RealtimeTestHost/packages.lock.json`: `dotnet restore --force-evaluate` also rewrites it, but
  only drops its trailing newline. The graph is identical. That file is not in the admission: the dependency PR
  must revert it (`git checkout -- tests/Paqueteria.RealtimeTestHost/packages.lock.json`), or REL-000 fails with
  `DEPENDENCY_ADMISSION_FILE_NOT_ALLOWED`.

The admission's `allowed_dependency_files` and `required_dependency_files` are `Directory.Packages.props` plus the
four lock files. Its `allowed_project_files` are the two host `.csproj` files.

## The Azure.Core move

ADP-001-AZURE admitted Azure.Core 1.61.0 and its dependencies. Azure.Core 1.63.0 depends on System.ClientModel
1.16.0, Microsoft.Identity.Client and Microsoft.Identity.Client.Extensions.Msal 4.90.0, and
Microsoft.Bcl.AsyncInterfaces and System.Memory.Data 10.0.10. A package can be admitted by only one admission, so
the entry declares each new version as follows:

- in `additional_package_versions`, scoped to the four lock files above;
- with one `baseline_package_upgrades` step per lock file, from the exact ADP-001 version and hash to the new one
  (24 steps).

Each step lands on the entry's own additional version, which delivers that `id@version` in that lock file (#239).

`Microsoft.Bcl.AsyncInterfaces` in `tests/Paqueteria.IntegrationTests` is a node of the MVP-0 baseline. ADP-001
already moved it once, so its upgrade chain is:

| Step | Admission | From | To |
| --- | --- | --- | --- |
| 1 | ADP-001-AZURE | 6.0.0 (`UcSjPsst…`) | 10.0.9 (`Aq7M8lG6…`) |
| 2 | OBS-003-OPENTELEMETRY | 10.0.9 (`Aq7M8lG6…`) | 10.0.10 (`TFI6OKYE…`) |

The checks against each base:

- **Against the tested base (`main` with ADP-001):** the dependency PR delivers only step 2.
- **Against the MVP-0 baseline (`test_k`):** the node goes through steps 1 and 2. Until PR 5 lands, the tree is
  still at 10.0.9, so `test_k` accepts step 1 alone.
- **For the other five packages,** the "from" node is not an MVP-0 baseline node. Against MVP-0 the node is new and
  matches the additional version; against `main` it is the single declared step.

## Evidence

I ran the real restore on a local throwaway branch, never pushed. I added the two `PackageVersion` lines and the two
`PackageReference` items, then ran `dotnet restore Paqueteria.sln --force-evaluate`. The restore reproduced the trial
lock files byte for byte. The admissions were read from this branch's commit, as REL-000 reads them from a tested
base:

- From `deps/obs-003-opentelemetry`, `validate_dependency_diff` (NORMAL_RELEASE_EVIDENCE) **accepts** the diff:
  `ADMITTED_DEPENDENCIES`, admission ids `["OBS-003-OPENTELEMETRY"]`, four lock files plus
  `Directory.Packages.props`.
- The permanent check against MVP-0 (`988926c7`) accepts all 36 changed lock files, and the `test_k` unit test
  passes on that tree.
- It is rejected in each of these cases:
  - without this entry (base = `development`): `DEPENDENCY_ADMISSION_VERSION_MISMATCH` (Azure.Core 1.63.0 against
    the admitted 1.61.0);
  - from another branch (`deps/adp-001-azure`, or this PR's branch): `DEPENDENCY_ADMISSION_BRANCH_NOT_AUTHORIZED`;
  - with one hash changed (`OpenTelemetry.Api`): `DEPENDENCY_ADMISSION_CONTENT_HASH_MISMATCH`;
  - with the RealtimeTestHost newline rewrite kept: `DEPENDENCY_ADMISSION_FILE_NOT_ALLOWED`.
- `main_source_guard.py`, given this policy as the tested base, opens `main` to `deps/obs-003-opentelemetry`
  (`MAIN_SOURCE_AUTHORIZED_DEPENDENCY_ADMISSION`). It still refuses any other branch.
- Regression test: `test_committed_policy_registers_obs_003_opentelemetry_with_exact_amendments`.

## PR 5: the dependency route

Three steps, each with its own PR (GOV-DEPENDENCY-ROUTE-001, `branching-and-ci.md`):

1. Promote `development` → `main` with this entry. Then open the dependency PR into `main` from
   `deps/obs-003-opentelemetry`, cut from `main`:
   - add `<PackageVersion Include="Azure.Monitor.OpenTelemetry.Exporter" Version="1.10.0" />` after `Azure.Identity`,
     and `<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.19.1" />` after `NetTopologySuite`,
     in `Directory.Packages.props`;
   - add `<PackageReference Include="Azure.Monitor.OpenTelemetry.Exporter" />` and
     `<PackageReference Include="OpenTelemetry.Extensions.Hosting" />` to the API and Worker host `.csproj` files;
   - run `dotnet restore Paqueteria.sln --force-evaluate`, revert the RealtimeTestHost lock, and commit only the
     seven allowed files.

   Foundation must pass 13/13 on the PR and again on the push to `main`. REL-000 runs in `NORMAL_RELEASE_EVIDENCE`
   with no remediation id.
2. Back-sync PR, head `main` → base `development` (`MAIN_BACKSYNC`, which needs the certified Foundation run of the
   `main` head).
3. A PR into `development` sets this entry to `status: MERGED`, adds `tracked_pull_request`, updates the regression
   test and adds the admission to `rel-000-internal-release.md`. The packages stay admitted, and the branch can no
   longer open PRs into `main`.

The OpenTelemetry code (export off by default, 10 % sampling, attribute allowlist) is PR 6. It is not part of this
route.

## Rollback

- **Before PR 5 merges:** revert this PR. The admission is read from the tested base, so removing it closes
  `deps/obs-003-opentelemetry` and admits nothing.
- **After PR 5 merges:** the packages do nothing until PR 6 uses them, and in PR 6 the export is off by default. The
  operational rollback is configuration.
- **Removing the packages again is not expressible today.**
  - A new admission cannot declare the reverse step (Azure.Core 1.63.0 → 1.61.0). Its target, 1.61.0, is the
    version ADP-001-AZURE already admits, so the registry rejects it with `DEPENDENCY_ADMISSION_POLICY_INVALID`.
    The chain check would also reject it, because 1.61.0 is a version the chain already visited
    (`DEPENDENCY_ADMISSION_AMENDMENT_CONFLICT`).
  - Removing the packages would need its own owner decision and REL-000 support.
