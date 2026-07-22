# SEC-002 secure identity bootstrap and public tracking adapters

## Scope

SEC-002 connects the SEC-001 request pipeline to the two read-only bootstrap
functions already frozen in AI-06/AI-18. It does not modify the 73 canonical
files, provision users, choose an active tenant, integrate AuthCenter, expose a
productive tracking endpoint, implement order transitions, or use real PII.
TEN-001 and TRK-001 remain future work.

## Identity bootstrap

`IIdentityContextResolver` lives in Identity.Application and is independent of
ASP.NET Core, Npgsql, claims and SQL. It returns either a resolved immutable
context or `NoAuthorizedContext`; infrastructure failures use a distinct
technical exception.

`PostgreSqlIdentityContextResolver` executes exactly:

```sql
SELECT security.resolve_identity_context(@identity_subject);
```

The subject is a typed text parameter. The adapter uses an explicit command
timeout, cancellation and one JSONB scalar. It never queries `identity.users`
or `organization_memberships` directly. Unknown, suspended and disabled users
all produce the same unresolved result because the function returns `NULL`.

## Public tracking

`IPublicTrackingProjectionReader` lives in Orders.Application. Its public model
contains only `PublicId`, mapped public status, nullable estimated window and
timeline entries (`Code`, `OccurredAt`). It has distinct Found, NotFound and
technical outcomes and exposes no JSON, internal order/organization ID, actor,
driver, coordinates, cost or PII.

`PostgreSqlPublicTrackingProjectionReader` executes exactly:

```sql
SELECT security.get_public_tracking_projection(@token);
```

The exact token is sent as a typed text parameter without trim, normalization,
case change, padding change or pre-hash. SQL `NULL` is uniformly NotFound; the
adapter never distinguishes missing, expired, revoked, mutated or unmapped
status. The strict parser permits only the normative public fields and public
timeline codes.

The only HTTP surface added by SEC-002 is
`GET /__tests/tracking/{token}` in `Testing`. It is excluded from OpenAPI,
anonymous, responds with `Cache-Control: no-store`, uniform 404 Problem Details
or generic 503. `/api/v1/tracking/{token}` remains unimplemented.

## Cryptographic and status contracts

`TrackingTokenHasher` is the single productive implementation. It creates 32
CSPRNG bytes, encodes Base64URL without padding, and hashes the exact UTF-8 bytes
with SHA-256. It performs no trim or Unicode normalization. Permanent vector:

```text
token  AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA
sha256 eb9f16800c9029ffca85695763d23c3ace71011cf40e9354acd810205e250f87
```

`PublicOrderStatusPolicy` is the single productive C# mapping for all 17
internal statuses. Unknown input throws `PublicStatusMappingException`; SQL
fails closed with no projection. ContractTests compare AI-04, OpenAPI, SQL and
the C# policy.

## Runtime role and deployment assertions

Both adapters open a transaction and use `SET LOCAL ROLE paqueteria_app` for a
NOINHERIT API login. They never persist `SET ROLE`, assume
`paqueteria_bootstrap`, set tenant GUCs or leak role state to pooled connections.

`DatabaseBaselineAssertions` verifies that `paqueteria_bootstrap` is NOLOGIN and
BYPASSRLS, owns no schema/table/sequence and only owns the two approved access
functions. It validates `SECURITY DEFINER`, exact fixed `search_path`, no dynamic
`EXECUTE`, PUBLIC revocation, app/worker EXECUTE matrix and exact column-level
SELECT grants on the five approved tables.

## Tests and local execution

The SEC-002 `WebApplicationFactory` starts only the pinned ephemeral image:

```text
postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d
```

It reuses `DatabaseBaselineDeployer`, creates a synthetic non-superuser API
login with only `paqueteria_app`, injects its connection, seeds synthetic users,
memberships, tokens, order and public/private events, verifies PostgreSQL 18 and
PostGIS 3.6, then destroys the container. It never touches the persistent
FND-002 database.

```powershell
dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj
dotnet test .\tests\Paqueteria.ArchitectureTests\Paqueteria.ArchitectureTests.csproj
```

Rollback is code/config rollback only. No migration or persistent data was
introduced. Keep both providers `Disabled` to fail closed when PostgreSQL is
not intentionally configured.
