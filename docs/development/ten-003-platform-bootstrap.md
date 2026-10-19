# TEN-003: platform bootstrap (first PLATFORM organization and PLATFORM_ADMIN)

Decision: `TEN-003-PLATFORM-BOOTSTRAP-2026-10-19` in `docs/normative/v0.6/decision-log.md` (owner decision E1,
"Comando único auditado (Recommended)"). The decision is the ADR for creating the pilot's root privileged identity
(AI-01 §7).

## Why it exists

Nothing else creates the first PLATFORM organization or its PLATFORM_ADMIN:

- REG-001 `security.create_self_service_organization` creates only BUSINESS or ALLY organizations.
- `security.add_pending_membership` lets only a PLATFORM_ADMIN grant DISPATCHER or FINANCE, and PLATFORM_ADMIN only
  inside a PLATFORM organization (REG-ROLE-CEILING, REG-PLATFORM-ADMIN-ADDS).
- `security.decide_ally_organization` requires an ACTIVE PLATFORM_ADMIN of an ACTIVE PLATFORM organization, and
  `master-data-gate` needs `--platform-organization-id`.
- The TEN-003 provisioner had no production caller and is denied by default in the API.

Without a PLATFORM_ADMIN, nobody in the pilot can create an order.

## The command

```bash
dotnet /app/migrator/Paqueteria.DatabaseMigrator.dll platform-bootstrap \
  --connection-env PAQUETERIA_MIGRATION_CONNECTION \
  --identity-subject <AuthCenter sub> --legal-name '<legal name>' --display-name '<display name>'
```

The owner runs it once, before the first sign-in, in an execution of `job-pv-pilot-migrate` that only changes the
arguments (`docs/operations/env-001-pilot/README.md` §6.1 step 10; Spanish walkthrough in
`guia-owner-seccion-a.md` §7.5).

Inputs:

- `--identity-subject`: the AuthCenter `sub`. AuthCenter sets `sub` to its user id (`ApplicationUser.Id`, a
  `Guid`), which the owner reads at the end of `https://authcenter.info/admin-v2/users/<id>`. The command accepts
  only the canonical lowercase UUID form, so an e-mail, a whole address or an uppercase copy is refused before it
  can bind the only PLATFORM_ADMIN to an account nobody signs in with.
- `--legal-name` (at most 200 characters) and `--display-name` (at most 80): the REG-001 onboarding rules
  (`PostgreSqlSelfServiceRegistrationService.IsValidName`): not empty, no leading or trailing spaces, no control
  characters.
- Each option may appear once. A value without its option is refused without echoing it.

Before the bootstrap, the command asserts the baseline (`DatabaseBaselineDeployer.AssertAsync`) and every module
lane (`ModuleMigrationCoordinator.AssertAsync`), like `assert`.

## The transaction

`PlatformBootstrap.RunAsync` calls the TEN-003 `PostgreSqlInitialOrganizationProvisioner` with three things of its
own:

- an authorizer bound to exactly one provisioning: that subject, organization type PLATFORM, role PLATFORM_ADMIN;
- `PlatformBootstrapTransaction`, its `ITenantTransactionRunner<OrganizationsDbContext>`;
- the shared `PostgreSqlAppendOnlyAuditWriter`.

The provisioner generates the four UUIDs (user, organization, membership, audit) and runs one transaction on the
migration connection:

1. `BEGIN`, `SET LOCAL lock_timeout = '30s'` and `statement_timeout = '60s'`.
2. `pg_advisory_xact_lock(2026101901)`, so two concurrent first runs cannot both see an empty database.
3. The login check. `current_user` must be the session login, must be a superuser or `BYPASSRLS`, and must have SET
   on `paqueteria_migrator`; otherwise `TEN003_PLATFORM_BOOTSTRAP_DEPLOYMENT_LOGIN_REQUIRED`. Under FORCE RLS any
   other login, the runtime ones included, would see no row and miss an earlier run.
4. The refusal, read as the deployment login, which sees every tenant:
   - any PLATFORM organization or any PLATFORM_ADMIN membership, in any status: `TEN003_PLATFORM_BOOTSTRAP_ALREADY_DONE`;
   - any `identity.users` row with that subject: `TEN003_PLATFORM_BOOTSTRAP_SUBJECT_ALREADY_SIGNED_IN`.
5. `set_config('app.current_user_id', <new user>, true)` and `set_config('app.current_org_ids', <new organization>
   as a uuid[] parameter, true)`, after `BEGIN` (AI-01 §4.1-§4.2, AI-02 `provisioning`).
6. `SET LOCAL ROLE paqueteria_migrator`. The command then asserts that the current role is NOSUPERUSER and
   NOBYPASSRLS.
7. The TEN-003 inserts:
   - `identity.users`: ACTIVE, `email_ciphertext` NULL;
   - `organizations.organizations`: PLATFORM, ACTIVE, `self_service_creator_user_id` NULL;
   - `organizations.organization_memberships`: PLATFORM_ADMIN, ACTIVE, default;
   - one `platform.audit_logs` row, `INITIAL_ORGANIZATION_PROVISIONED` / `ORGANIZATION` for the new organization,
     with the new user as actor, request id `ten-003-platform-bootstrap` and payload `{}`.

   The AI-06 policies `users_tenant`, `organizations_tenant`, `memberships_tenant` and `audit_logs_tenant` admit
   these rows only because the new UUIDs are in the transaction's context.
8. `COMMIT`. Any exception rolls the whole transaction back.

### Role and connection (AI-18)

The command runs on the migration connection and writes as `paqueteria_migrator`, like `master-data-gate`. It does
not use the API's `paqueteria_app` for three reasons:

- On Azure the deployment login has SET and USAGE only on `paqueteria_migrator` and the executors (E-002 capability
  gate). It has none on `paqueteria_app`, so `TenantTransactionContext`'s `SET LOCAL ROLE paqueteria_app` would be
  refused.
- `paqueteria_app` cannot see other tenants, so it could not refuse a second run without a new SECURITY DEFINER
  function (an AI-06/AI-18 change).
- Running as the API login would mean giving an operator job the API's connection secret.

`paqueteria_migrator` is NOBYPASSRLS and every table written is FORCE ROW LEVEL SECURITY, so RLS applies to it as to
`paqueteria_app`. The deployment login's own `BYPASSRLS` (required by the E-002 preflight) serves only the
read-only refusal checks in step 4. The command adds no role, grant or SECURITY DEFINER function.

### Code changes outside the command

- `PostgreSqlInitialOrganizationProvisioner` now takes `ITenantTransactionRunner<OrganizationsDbContext>` (the
  existing interface of `TenantTransactionContext` and `WorkerTenantTransactionContext`) instead of the concrete
  `TenantTransactionContext`. The API registration builds it explicitly with `TenantTransactionContext`
  (`paqueteria_app`) and the default `DenyInitialOrganizationProvisioningAuthorizer`, so the API is unchanged.
- `Paqueteria.Infrastructure` exposes its internals to `Paqueteria.DatabaseMigrator`. The bootstrap enters
  `TenantDatabaseExecutionState` for its transaction, so the shared audit writer checks the audited organization
  as it does in the API.
- In the migrator's parser, a token that is not an option is refused without echoing it, and the unknown-option
  message reads "Unknown, repeated or incomplete option".

## Output and exit codes

The output never includes the subject, a name or any id (OBS-003-TENANT-PSEUDONYM; the other migrator commands
print no organization id either). After the first sign-in, `GET /api/v1/me/organization-contexts` shows the PLATFORM
organization id that `master-data-gate` needs.

| Exit | Output |
|---|---|
| 0 | `TEN003_PLATFORM_BOOTSTRAP_OK organization_type=PLATFORM status=ACTIVE role=PLATFORM_ADMIN (audited)` |
| 2 | Argument errors, with the usage text and no value echoed |
| 5 | A baseline or lane assertion failed (database not fully migrated) |
| 11 | `..._ALREADY_DONE`, `..._SUBJECT_ALREADY_SIGNED_IN`, `..._DEPLOYMENT_LOGIN_REQUIRED` or `..._ROW_SECURITY_REQUIRED`; nothing was written |
| 12 | `TEN003_PLATFORM_BOOTSTRAP_DATABASE_ERROR SQLSTATE=<code>`; nothing was written |

## Tests

- `tests/Paqueteria.ContractTests/PlatformBootstrapCommandOptionsTests.cs` (no database) covers argument validation:
  - the subject format;
  - the name rules and length limits;
  - the required connection;
  - repeated options;
  - foreign options;
  - that rejected values are not echoed.
- `tests/Paqueteria.ContractTests/PostgreSql/PlatformBootstrapPostgreSqlContractTests.cs` (Testcontainers,
  PostgreSQL 18/PostGIS 3.6, each test on its own migrated database):
  - the whole command refuses a subject that already signed in;
  - a NOBYPASSRLS login and the API runtime login are refused;
  - a failure after each insert leaves nothing;
  - an Azure-like deployment login (NOSUPERUSER, BYPASSRLS, SET and USAGE on `paqueteria_migrator`, no
    `paqueteria_app`) creates exactly the four rows, and the output holds no UUID, subject or name;
  - every later run is refused (the whole command, exit 11) with the counts unchanged;
  - two concurrent first runs of the whole command, both held on the advisory lock, give exactly one root identity;
  - after that, the owner's sign-in (`register_identity_subject`) reuses the same user and
    `resolve_identity_context` shows the default PLATFORM_ADMIN membership;
  - the PLATFORM_ADMIN adds DISPATCHER, FINANCE and PLATFORM_ADMIN pending memberships (`ADDED`), lists the pending
    ALLY and approves it, while the ALLY's creator gets `REG001_PLATFORM_ADMIN_REQUIRED`.
- The existing TEN-003 tests (`ActiveTenantRuntimeContractTests`, `TenancyContractTests`) still run the provisioner
  through `TenantTransactionContext`.

## Residual risks

- **A wrong but well-formed UUID.** The command cannot tell a mistyped UUID from the right one. A typo binds the
  only PLATFORM_ADMIN to an account that does not exist, and every later run is refused. Copying the id from the
  AuthCenter address bar avoids typing it. Recovery is the rollback below.
- **Signing in first.** If the owner signs in before running the command, it refuses
  (`SUBJECT_ALREADY_SIGNED_IN`) and never takes over the existing user. No automatic path exists; that case needs
  a separate owner decision.
- **Execution history.** The job execution keeps its arguments (the subject and the two names) in its history,
  readable by whoever can read the resource group. None of them is a secret.
- **The `az` override is unverified.** `az containerapp job start --yaml` with the job's own template and new
  arguments was checked against the Azure CLI source, not against Azure (README §9).
- **The deployment login reads every tenant.** It does so only for three existence checks, which return booleans
  and print nothing.

## Rollback

- **Code:** revert the PR. The API registration returns to the type registration; the TEN-003 provisioner behaves
  the same either way.
- **Data:** the command is forward-only. Its audit row is append-only, and no runtime role can delete the
  organization, user or membership. To undo a run (for example, a wrong subject):
  - before anyone has used the PLATFORM organization, restore the pilot database to a point in time before the run
    (README §6.4, point-in-time restore), then run the command again with the right subject;
  - after other activity, removing or replacing the root identity needs its own owner decision. Never delete the
    audit row.
