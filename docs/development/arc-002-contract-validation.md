# ARC-002 runtime contract validation

## Purpose and completion gate

ARC-002 executes the v0.6 contracts without adding migrations, application use
cases, API endpoints, a functional Worker, or production persistence. The purge
remediation is locally validated. ARC-002 becomes `DONE` only after the five
pull-request jobs also pass.

## Isolated database

The PostgreSQL tests use this immutable image:

```text
postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d
```

Each run creates and later destroys a fresh `paqueteria_contracts` database with
synthetic credentials and a dynamic host port. It never connects to the
persistent FND-002 database. The fixture verifies the SQL hashes and executes:

1. `docs/normative/v0.6/database/AI-06_SCHEMA.sql`
2. `docs/normative/v0.6/database/AI-18_DATABASE_ROLE_MODEL.sql`

Verified hashes:

```text
AI-06  c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96
AI-18  7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd
```

The fixture requires PostgreSQL major version 18 and PostGIS 3.6. AI-18 is
unchanged. Runtime connections are non-superuser logins with only their
intended `paqueteria_app` or `paqueteria_worker` membership.

## Purge contract

The real branches of `security.purge_outbox` and
`security.purge_location_outbox` select a bounded, deterministic terminal set
without row locks, then delete only rows whose target-table ID, terminal state,
and corresponding cutoff still match. Dry-run remains read-only.

This preserves ADR-030's least-privilege decision:

- maintenance remains `NOLOGIN BYPASSRLS` with `SELECT, DELETE` only on both
  outbox tables;
- maintenance has no `UPDATE` and no access outside the two lanes;
- Worker has no direct table access and invokes purge only through `EXECUTE`;
- maintenance cannot claim, settle, requeue, or modify lifecycle states.

Runtime coverage requires both lanes to delete old `PROCESSED` and `DEAD` rows,
honor `p_batch_size`, return exact counts, preserve recent terminal and active
`PENDING`, `RETRY`, and `PROCESSING` rows, reject unsafe cutoffs, and return zero
after the eligible set has been exhausted.

The concurrent test opens two real connections and starts both purge calls over
the same eligible set. Calls have command and cancellation timeouts plus row and
container diagnostics. The combined return count must equal the rows actually
removed, active rows must remain, and no idle transaction or lock may survive.
A brief wait is acceptable; deadlock, double counting, and indefinite waiting
are failures.

## Other executable coverage

The suite also checks the exact 72-file canonical inventory, OpenAPI references,
bootstrap catalogs, extensions, ownership and grants, forced RLS and tenant
isolation, provisioning rollback, outbox claim/settle/requeue behavior, tracking
vectors and fail-closed projections, acceptance evidence, money constraints,
snapshots, and the public status mapping.

## Commands

```powershell
dotnet restore --locked-mode
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj
```

CI partitions the project into:

```powershell
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category!=PostgreSqlContract"
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category=PostgreSqlContract"
```

An explicit local diagnostic escape hatch exists for a workstation without
Docker:

```powershell
$env:PAQUETERIA_SKIP_POSTGRES_CONTRACT_TESTS = "true"
```

A skipped database test is not successful ARC-002 validation, and CI never sets
that variable.

## FND-002 boundary and upgrade note

Compose uses the same immutable PostgreSQL 18/PostGIS 3.6 image and mounts its
named volume at `/var/lib/postgresql`, the PostgreSQL 18 layout. `Down` preserves
the volume and `Reset -Force` removes it. A volume initialized by PostgreSQL 17
must be reset or upgraded deliberately before the PostgreSQL 18 container can
start. No data migration is performed by this task.

AI-06 and AI-18 are never applied to that persistent FND-002 database. The
Compose init script only creates PostGIS in `public` and pgcrypto in
`extensions`.

## Scope and dependency risk

SEC-001, SEC-002, TEN-001, DBA-001, business behavior, providers, migrations,
and persistent API/Worker integration remain out of scope. The inherited sharp
advisory `GHSA-f88m-g3jw-g9cj` is tracked separately; this remediation does not
force an incompatible override or disable `pnpm audit`.
