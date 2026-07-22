# TEN-001 active tenant context and transactional RLS

TEN-001 adds the minimum productive tenant boundary on top of the frozen v0.6
database baseline. It does not add commercial use cases, change AuthCenter,
start TEN-002/TEN-003, or modify any file under `docs/normative/v0.6`.

## Module and persistence ownership

`src/Modules/Organizations` has the canonical Domain, Application,
Infrastructure and Endpoints layers. `IdentityDbContext` owns the EF mapping for
`identity.users`; `OrganizationsDbContext` owns `organizations.organizations`
and `organizations.organization_memberships`. Both map the existing AI-06
objects explicitly. UUIDs, timestamps and statuses are supplied by application
code and primary keys use `ValueGeneratedNever`. There is no lazy loading,
generic repository, startup migration or Worker registration.

The first migrations are adoption migrations:

- `20260722_AdoptCanonicalIdentityBaseline`
- `20260722_AdoptCanonicalOrganizationsBaseline`

Their `Up` operations assert the existing canonical shape and never create,
alter or drop an AI-06 table, policy, role or grant. Their non-destructive
`Down` operations intentionally do nothing. EF records them independently in
`platform.__ef_migrations_history_identity` and
`platform.__ef_migrations_history_organizations`; both histories are owned by
`paqueteria_migrator` and are inaccessible to runtime roles.

The independent `Paqueteria.DatabaseMigrator` verifies AI-06, AI-18 and both
adoption sources before database access. `plan` reports pending/applied/drift
per history, `apply` executes baseline then Identity then Organizations, and
`assert` rejects missing, unexpected or incorrectly owned histories. API and
Worker never run migrations at startup.

## Request context and authorization

Tenant-aware endpoints carry explicit metadata and require exactly one
`X-Organization-Id` value in canonical UUID `D` format. Empty UUIDs, whitespace,
padding, malformed values and multiple header values are rejected. There is no
default organization fallback. The request-scoped `ITenantContext` is selected
once and is immutable afterward.

Selection happens after authentication and before authorization. The selected
organization must be one of the active memberships resolved by SEC-002.
Authorization evaluates role and MFA inside that selected organization; a role
held in a different organization never grants access. Activating a
`PLATFORM_ADMIN` membership writes the narrow `TENANT_CONTEXT_ACTIVATED` event
to the existing append-only `platform.audit_logs` table in a tenant transaction.
Normal logs do not contain full organization lists.

`GET /api/v1/me/organization-contexts` intentionally has no active-organization
header. It takes active memberships from the SEC-002 session, opens one
transaction containing all those organization IDs, and returns only
`organization_id`, `display_name`, `role` and `is_default`. Missing contexts
produce an empty array; inaccessible identity and infrastructure failure remain
generic 403 and 503 responses.

## Transaction-local database context

Every productive EF tenant query or write runs through
`TenantTransactionContext<TDbContext>`. Each retry begins a fresh transaction
and executes parameterized commands equivalent to:

```sql
SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
SET LOCAL ROLE paqueteria_app;
```

Organization IDs are deduplicated and sorted. An empty array is supported only
for explicitly expected paths. Npgsql retry execution repeats the complete
transaction, including both GUCs and the local role; no transaction or context
is reused after a transient failure.

Command and SaveChanges interceptors throw
`TenantTransactionRequiredException` when EF is used without both an explicit
transaction and applied context. Identity and Organizations also have EF query
filters for the current user and allowed organizations. These are defense in
depth: canonical FORCE RLS remains the final database boundary. Because every
value and the role change are transaction-local, commit, rollback, retry and
pool reuse cannot retain tenant or role state.

## Initial provisioning

Initial provisioning has no public endpoint. Its application port requires an
authorization decision before any insert. The PostgreSQL implementation
generates user, organization, membership and audit IDs, then inserts all four
rows with typed Npgsql parameters and no `RETURNING` inside one tenant
transaction. A failure after any insert rolls the whole transaction back;
concurrent duplicate identity subjects yield one success and a generic conflict.
Production composition registers a deny-by-default authorizer until an approved
internal workflow supplies the authorization policy.

## Validation and rollback

The Testcontainers lane uses the pinned PostgreSQL 18/PostGIS 3.6 image and
proves adoption histories, FORCE RLS visibility, multi-organization reads,
empty context, pooling, guards, full-transaction retry, header/authorization
behavior, context listing, provisioning rollback and duplicate conflict. It
never uses the persistent FND-002 database.

Adoption rollback never deletes canonical tables. Do not add a destructive
`Down`, delete migration histories manually, or remove memberships to simulate
rollback. Revert application commits for a code rollback. Destroy/recreate an
ephemeral Testcontainers database; recreate an authorized empty environment;
for future production use restore or a controlled forward-fix. A failed
provisioning transaction rolls itself back.

TEN-002 and TEN-003 remain separate backlog work and were not started. GATE-007
continues to block the production retention/partition decisions outside this
tenant-context slice.
