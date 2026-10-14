# End-to-end tests

OPS-005 (phase 4 PR 10): the platform end-to-end suite. It runs the assembled API, Worker and Web against real
PostgreSQL/PostGIS and the S3-compatible storage double, drives the owner's main journey through the real screens in
Chromium, and measures the platform checks of AI-08 OPS-005 in the same run. Details, how each check is measured and
how to run it locally: [`docs/development/ops-005-end-to-end-suite.md`](../../docs/development/ops-005-end-to-end-suite.md).

- Category `PlatformEndToEnd`: the suite. It runs in the `driver-stops-pwa` CI job and is excluded from the `dotnet`
  jobs.
- Uncategorized: the instrument tests, which prove against the real runtime what two checks rely on. They run in the
  `dotnet` job.
- The harness is the one of `tests/Paqueteria.IntegrationTests`, referenced rather than copied. Packages: only the ones
  that project already references, so this project's lockfile stays a subset of its lockfile.
- Synthetic data only (SEC-003).

This project contains no placeholder or always-passing tests: every test asserts an observable behaviour of the
assembled platform or of an instrument a check depends on.
