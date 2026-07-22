# TEN-002 transactional RLS and pooling validation

TEN-002 validates the transaction-local tenant infrastructure introduced by
TEN-001. No production implementation or database migration was required: the
existing `TenantTransactionContext`, execution state, command guard,
SaveChanges guard, retry strategy and EF filters already satisfy the runtime
contract. TEN-002 adds independent executable evidence and this traceability
record without changing the frozen normative baseline.

## Traceability matrix

| TEN-002 requirement | Existing implementation | Executable evidence | Result | Gap disposition |
| --- | --- | --- | --- | --- |
| Explicit transaction start | `TenantTransactionContext.ExecuteAsync` | Npgsql transaction log capture | SATISFIED | Independent order evidence added |
| Initialization order after `BEGIN` | `ApplyContextAsync` | `Productive_context_initialization_is_the_first_application_work_after_begin_and_is_ordered` | SATISFIED | Exact order asserted |
| `SET LOCAL ROLE` | `SetRoleSql` | Runtime log and architecture source contract | SATISFIED | Persistent role explicitly rejected |
| User GUC | typed `user_id` command | Runtime value and command-order assertions | SATISFIED | Independent evidence added |
| Organization GUC | typed `organization_ids` command | Runtime value, command and parameter log assertions | SATISFIED | Independent evidence added |
| PostgreSQL `uuid[]` parameter | `NpgsqlParameter<Guid[]>` with `Array | Uuid` | Architecture source contract plus PostgreSQL command log | SATISFIED | Type and server-side cast asserted |
| Empty `{}` serialization | empty `Guid[]` | `Empty_organization_context_is_exactly_braces_and_never_null` | SATISFIED | Exact value asserted |
| `NULL` rejection | constructor null guard | unit test plus non-null runtime result | SATISFIED | Explicit test added |
| LINQ query outside transaction | command guard | fail-closed contract test | SATISFIED | No useful SQL reaches Npgsql |
| SQL query outside transaction | command guard | tagged raw-query contract test | SATISFIED | No useful SQL reaches Npgsql |
| Command outside transaction | command guard | tagged raw-command contract test | SATISFIED | No useful SQL reaches Npgsql |
| `SaveChanges` outside transaction | SaveChanges guard | unit and PostgreSQL contract tests | SATISFIED | No insert reaches Npgsql |
| Retry creates a new transaction | EF Npgsql execution strategy | two distinct PostgreSQL transaction IDs | SATISFIED | Independent evidence added |
| Context reapplied per retry | transaction body surrounds initialization | two captured user GUC, org GUC and local-role sequences | SATISFIED | Per-attempt ordering asserted |
| Commit | transaction coordinator | Npgsql commit capture and persisted TEN-001 cases | SATISFIED | Captured once after successful retry |
| Rollback | catch/rollback path | Npgsql rollback capture and clean next lease | SATISFIED | Residual state rejected |
| Exception cleanup | catch/finally plus transaction disposal | synthetic exception followed by clean pooled lease | SATISFIED | Reusable harness added |
| Cancellation cleanup | cancellation propagated to Npgsql | cancelled `pg_sleep` followed by clean pooled lease | SATISFIED | Cancellation regression added |
| Return connection to pool | scoped DbContext/data source | sequential leases through a pool of size one | SATISFIED | Physical reuse demonstrated |
| Same physical connection reuse | Npgsql pooling | identical `pg_backend_pid()` over repeated leases | SATISFIED | Four A/B iterations asserted |
| User changes between leases | transaction-local user GUC | user A then user B on the same backend | SATISFIED | Exact GUC values asserted |
| Organization changes between leases | transaction-local organization GUC | organization A then B on the same backend | SATISFIED | Tenant rows remain isolated |
| Empty context after valid use | empty typed array | `{}` on the same backend after populated contexts | SATISFIED | Zero visible tenant rows asserted |
| Privileged activation pool return | platform-admin activation audit uses the same transaction coordinator | audit followed by a clean non-privileged lease on the same backend | SATISFIED | Local role and GUC state do not survive activation |
| Cross-tenant isolation | EF filters plus canonical FORCE RLS | sequential and concurrent client-account reads/writes | SATISFIED | No cross-contamination observed |
| Runtime `NOBYPASSRLS` | AI-18 roles and runtime logins | existing role/catalog and privilege contract tests | SATISFIED | Reused verified TEN-001/DBA-001 evidence |
| No persistent `SET ROLE` | local role constant only | source contract and clean `current_user` after pool return | SATISFIED | Session returns to login role |

No row remains `PARTIAL`, `MISSING` or `CONTRADICTED` after the TEN-002 test
additions.

## Exact transaction sequence

Npgsql starts an explicit transaction. The first application SQL is the user
GUC, followed by the organization GUC and the local application role:

```text
BEGIN
SELECT set_config('app.current_user_id', @user_id::uuid::text, true)
SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true)
SET LOCAL ROLE paqueteria_app
tenant SQL
COMMIT or ROLLBACK
```

The synthetic login is `NOINHERIT`. `SET LOCAL ROLE` is therefore required
before tenant business SQL, but it is not tenant business work and it does not
need to precede `set_config`. The GUC statements are the first application
initialization after `BEGIN`; the role is applied before EF executes any tenant
query or command. All three operations are repeated inside every execution
strategy attempt.

## Typed organization context

The application binds `organization_ids` as
`NpgsqlParameter<Guid[]>` with `NpgsqlDbType.Array | NpgsqlDbType.Uuid`.
PostgreSQL performs `@organization_ids::uuid[]::text` inside `set_config`; no
UUID is concatenated into SQL and no client-built array literal is used. One or
many UUIDs retain the authorized, sorted set. `Array.Empty<Guid>()` produces
exactly `{}`. The execution-context constructor rejects a null collection, so
`NULL` cannot reach Npgsql.

## Retry and pool lifecycle

The retry regression throws a controlled transient `NpgsqlException` after a
real PostgreSQL query in the first attempt. Npgsql's EF execution strategy
creates a second transaction with a different transaction ID. Captured logs
show a complete GUC/local-role sequence before tenant SQL in both attempts, one
rollback and one final commit.

The pool harness uses dedicated Npgsql data sources. A pool with maximum size
one proves physical reuse by observing the same `pg_backend_pid()` while
switching user A/organization A to user B/organization B over repeated leases.
It then verifies an empty organization context, an exception and a cancelled
command. Every following lease starts with the login role and empty user/org
settings, and cannot read rows from the previous tenant. A second pool runs
concurrent A/B transactions and verifies that results never cross-contaminate.

This is Npgsql connection pooling, not PgBouncer transaction pooling. Real
PgBouncer adoption remains gated by GATE-014 and requires rerunning this harness
against the selected pooler topology before production use. TEN-002 does not
claim that validation.

## Running the harness

```powershell
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj `
  --filter "FullyQualifiedName~TransactionalRlsPoolingContractTests"

dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj `
  --filter "Category=PostgreSqlContract"
```

The tests use the repository-pinned PostgreSQL 18/PostGIS 3.6 Testcontainers
image. They never target the persistent FND-002 database. Parameter logging is
enabled only on a dedicated test data source and contains synthetic UUIDs, not
PII or credentials.

## Residual risk and rollback

The residual database-pooling risk is GATE-014: PgBouncer transaction pooling
has not been selected or executed. Npgsql pooling is covered, including same
backend reuse, concurrency, exception and cancellation. No product schema,
role, grant or migration changed.

Rollback is a normal revert of the TEN-002 test and documentation commits. No
database rollback exists or is needed. Testcontainers databases are disposable;
do not delete adoption histories or alter the canonical baseline. TEN-003 and
general AUD-001 remain outside this work.
