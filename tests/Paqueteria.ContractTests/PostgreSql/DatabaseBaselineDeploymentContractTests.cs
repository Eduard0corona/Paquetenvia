using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DatabaseBaselineDeploymentContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Clean_database_applies_once_and_then_reports_already_applied()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("idempotent");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var deployer = new DatabaseBaselineDeployer();

            var cleanPlan = await deployer.PlanAsync(baseline, connectionString);
            Assert.Equal(DatabaseBaselineStatus.Clean, cleanPlan.State.Status);
            Assert.Equal(["0001-canonical-schema", "0002-role-model"], cleanPlan.Steps.Select(step => step.Id));

            var first = await deployer.ApplyAsync(baseline, connectionString);
            var second = await deployer.ApplyAsync(baseline, connectionString);
            var finalPlan = await deployer.PlanAsync(baseline, connectionString);

            Assert.Equal(DatabaseBaselineApplyStatus.Applied, first.Status);
            Assert.Equal(DatabaseBaselineApplyStatus.AlreadyApplied, second.Status);
            Assert.Equal(DatabaseBaselineStatus.Applied, finalPlan.State.Status);
            Assert.True(first.Assertions.Checks >= 10);
            Assert.StartsWith("18.", first.Assertions.PostgreSqlVersion, StringComparison.Ordinal);
            Assert.StartsWith("3.6", first.Assertions.PostGisVersion, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    private static readonly AzureOwnershipBridgeSelection Bridge = new(
        "DevSynthetic", "DEV_SYNTHETIC", "AZURE_POSTGRESQL_FLEXIBLE_SERVER");

    /// <summary>
    /// Emulates the Azure Flexible Server deployment principal on plain PostgreSQL: a non-superuser login with
    /// CREATEROLE/BYPASSRLS, azure_pg_admin membership, effective ADMIN/SET authority over the (cluster-wide,
    /// pre-existing) canonical roles, the azure.extensions allowlist and PostGIS 3.6 already in public.
    /// The extensions schema is intentionally NOT pre-created (E-002 v0.9 Amendment 2).
    /// </summary>
    private sealed class AzureLikeEnvironment(PostgreSqlContractFixture fixture) : IAsyncDisposable
    {
        public string AdminConnectionString { get; private set; } = string.Empty;

        public string DeploymentConnectionString { get; private set; } = string.Empty;

        private string _login = string.Empty;

        public async Task<AzureLikeEnvironment> InitializeAsync(string purpose, bool bypassRls = true,
            bool azureAdmin = true, string? allowlist = "POSTGIS,PGCRYPTO")
        {
            AdminConnectionString = await fixture.CreateIsolatedDatabaseAsync(purpose);
            var database = new NpgsqlConnectionStringBuilder(AdminConnectionString).Database;
            _login = $"azr001_bridge_test_{Guid.NewGuid():N}";
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using (var admin = new NpgsqlConnection(fixture.DeploymentConnectionString))
            {
                await admin.OpenAsync();
                await using var setup = new NpgsqlCommand($$"""
                    CREATE ROLE {{_login}} LOGIN CREATEROLE {{(bypassRls ? "BYPASSRLS" : "NOBYPASSRLS")}} NOSUPERUSER PASSWORD '{{password}}';
                    ALTER DATABASE "{{database}}" OWNER TO {{_login}};
                    GRANT paqueteria_migrator, paqueteria_app, paqueteria_worker,
                          paqueteria_bootstrap, paqueteria_outbox_executor, paqueteria_maintenance,
                          paqueteria_lifecycle_executor, paqueteria_cleanup_executor,
                          paqueteria_registration_executor, paqueteria_session_executor,
                          paqueteria_master_data_executor, paqueteria_master_data_loader
                    TO {{_login}} WITH ADMIN TRUE, SET TRUE;
                    DO $$ BEGIN CREATE ROLE azure_pg_admin NOLOGIN; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
                    {{(azureAdmin ? $"GRANT azure_pg_admin TO {_login};" : string.Empty)}}
                    {{(allowlist is null ? string.Empty : $"ALTER DATABASE \"{database}\" SET azure.extensions = '{allowlist}';")}}
                    """, admin);
                await setup.ExecuteNonQueryAsync();
            }

            await using (var admin = new NpgsqlConnection(AdminConnectionString))
            {
                await admin.OpenAsync();
                await using var postgis = new NpgsqlCommand("CREATE EXTENSION postgis", admin);
                await postgis.ExecuteNonQueryAsync();
            }

            DeploymentConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Username = _login,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            return this;
        }

        public string Login => _login;

        public async Task<string?> TextAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(DeploymentConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync() as string;
        }

        public async Task AdminExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<bool> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(DeploymentConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async Task<E002AclSnapshot> DatabaseAclAsync()
        {
            await using var connection = new NpgsqlConnection(DeploymentConnectionString);
            await connection.OpenAsync();
            return await E002AclSnapshot.ReadDatabaseAsync(connection, null, "test", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await fixture.DropIsolatedDatabaseAsync(AdminConnectionString);
            await using var admin = new NpgsqlConnection(fixture.DeploymentConnectionString);
            await admin.OpenAsync();
            await using var cleanup = new NpgsqlCommand($"DROP ROLE IF EXISTS {_login}", admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_applies_canonical_baseline_as_non_superuser_and_cleans_grants()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("ownershipbridge");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        Assert.Equal(CanonicalBaselineContract.SchemaSha256, baseline.Steps[0].Sha256);
        Assert.Equal(CanonicalBaselineContract.RolesSha256, baseline.Steps[1].Sha256);

        var databaseAclBefore = await environment.DatabaseAclAsync();
        Assert.True(await environment.ScalarAsync(
            "SELECT datacl IS NULL FROM pg_database WHERE datname = current_database()"));

        var stages = new List<string>();
        var deployer = new DatabaseBaselineDeployer(stageObserver: stages.Add);
        var first = await deployer.ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        var firstStages = stages.ToArray();
        var second = await deployer.ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);

        Assert.Equal(DatabaseBaselineApplyStatus.Applied, first.Status);
        Assert.Equal(DatabaseBaselineApplyStatus.AlreadyApplied, second.Status);
        Assert.True(first.Assertions.Checks >= 10);
        Assert.Equal(DatabaseBaselineStatus.Applied,
            (await deployer.PlanAsync(baseline, environment.DeploymentConnectionString)).State.Status);

        // E-002 v0.8 §14/§18: platform preflight, then AI-06 first, then the bridge prelude
        // (role pre-creation, capability gate, temporary CREATE), then AI-18, then cleanup in §19 order
        // with the database CREATE revoked before SET ROLE and the ACL restoration asserted after RESET ROLE.
        Assert.Equal(
        [
            "e002-platform-preflight", "ai06", "e002-prelude", "e002-role-precreation", "e002-capability-gate",
            "e002-grant-database-create", "e002-grant-security-create", "ai18", "e002-cleanup",
            "e002-revoke-database-create", "e002-set-role-migrator", "e002-revoke-security-create",
            "e002-reset-role", "e002-database-acl-restoration", "canonical-assertions",
        ], firstStages);
        // §35: the AlreadyApplied second execution activates no bridge stage (read-only assertions only).
        Assert.Empty(stages.Skip(firstStages.Length));

        // E-002 v0.8 §20: NULL-aware normalized database ACL restored exactly (raw NULL before, explicit after).
        Assert.True(databaseAclBefore.SetEquals(await environment.DatabaseAclAsync()));
        Assert.True(await environment.ScalarAsync("""
            SELECT NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user)
               AND NOT has_schema_privilege('paqueteria_bootstrap', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_outbox_executor', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_maintenance', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_lifecycle_executor', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_cleanup_executor', 'security', 'CREATE')
               AND NOT has_schema_privilege('paqueteria_session_executor', 'security', 'CREATE')
               AND (SELECT NOT rolcanlogin AND rolbypassrls FROM pg_roles WHERE rolname = 'paqueteria_session_executor')
               AND has_column_privilege('paqueteria_session_executor', 'identity.bff_sessions', 'revoked_at', 'UPDATE')
               AND NOT has_table_privilege('paqueteria_app', 'identity.bff_sessions', 'SELECT')
               AND (SELECT NOT rolcanlogin AND rolbypassrls FROM pg_roles WHERE rolname = 'paqueteria_cleanup_executor')
               AND has_column_privilege('paqueteria_cleanup_executor', 'custody.proof_upload_sessions', 'status', 'UPDATE')
               AND NOT has_column_privilege('paqueteria_cleanup_executor', 'custody.proof_upload_sessions', 'order_id', 'UPDATE')
               AND (SELECT NOT rolcanlogin AND rolbypassrls FROM pg_roles WHERE rolname = 'paqueteria_lifecycle_executor')
               AND has_column_privilege('paqueteria_lifecycle_executor', 'orders.orders', 'finalized_at', 'UPDATE')
               AND NOT has_column_privilege('paqueteria_lifecycle_executor', 'orders.orders', 'status', 'UPDATE')
               AND NOT has_database_privilege('paqueteria_migrator', current_database(), 'CREATE')
               AND (SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'security') = 'paqueteria_migrator'
               AND (SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = 'extensions') = 'paqueteria_migrator'
               AND (SELECT pg_get_userbyid(proowner) FROM pg_proc
                    WHERE oid = 'security.resolve_identity_context(text)'::regprocedure) = 'paqueteria_bootstrap'
               AND (SELECT pg_get_userbyid(proowner) FROM pg_proc
                    WHERE oid = 'security.claim_outbox(text,integer,interval)'::regprocedure) = 'paqueteria_outbox_executor'
               AND (SELECT pg_get_userbyid(proowner) FROM pg_proc
                    WHERE oid = 'security.purge_outbox(timestamptz,timestamptz,integer,boolean)'::regprocedure) = 'paqueteria_maintenance'
            """));
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_restores_explicit_and_non_default_database_acls()
    {
        // Explicit-default equivalent (GRANT of an already-default privilege materializes datacl) plus a
        // representative non-default entry that E-002 must leave untouched.
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgeacl");
        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var grant = new NpgsqlCommand($"""
                GRANT CONNECT ON DATABASE "{admin.Database}" TO PUBLIC;
                GRANT CONNECT ON DATABASE "{admin.Database}" TO paqueteria_app;
                """, admin);
            await grant.ExecuteNonQueryAsync();
        }

        var before = await environment.DatabaseAclAsync();
        Assert.False(await environment.ScalarAsync(
            "SELECT datacl IS NULL FROM pg_database WHERE datname = current_database()"));
        Assert.Contains(before.Entries, entry => entry is { Grantee: "paqueteria_app", Privilege: "CONNECT" });

        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        var result = await new DatabaseBaselineDeployer().ApplyAsync(
            baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        Assert.Equal(DatabaseBaselineApplyStatus.Applied, result.Status);
        Assert.True(before.SetEquals(await environment.DatabaseAclAsync()));
    }

    [Fact]
    public void Database_acl_restoration_mismatch_emits_normative_guard()
    {
        var before = new E002AclSnapshot("database", [new E002AclEntry("PUBLIC", "owner", "CONNECT", false)]);
        var after = new E002AclSnapshot("database",
            [new E002AclEntry("PUBLIC", "owner", "CONNECT", false), new E002AclEntry("paqueteria_migrator", "owner", "CREATE", false)]);
        var reordered = new E002AclSnapshot("database",
            [new E002AclEntry("paqueteria_migrator", "owner", "CREATE", false), new E002AclEntry("PUBLIC", "owner", "CONNECT", false)]);

        E002AclSnapshot.AssertDatabaseRestored(after, reordered, "test");
        var exception = Assert.Throws<E002GuardException>(() => E002AclSnapshot.AssertDatabaseRestored(before, after, "test"));
        Assert.Equal("E002_DATABASE_ACL_RESTORE_MISMATCH", exception.GuardCode);
        Assert.StartsWith("E002_DATABASE_ACL_RESTORE_MISMATCH; STOP_FOR_CONTRACT_REVIEW", exception.Message, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Preexisting_temporary_create_privilege_fails_closed_before_mutation_and_is_not_removed()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgeprestate");
        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var grant = new NpgsqlCommand(
                $"GRANT CREATE ON DATABASE \"{admin.Database}\" TO paqueteria_migrator", admin);
            await grant.ExecuteNonQueryAsync();
        }

        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        var exception = await Assert.ThrowsAsync<E002GuardException>(() => new DatabaseBaselineDeployer()
            .ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge));

        Assert.Equal("E002_CREATE_PRESTATE_PRESENT", exception.GuardCode);
        // Rolled back: no AI-06 object survives and the pre-existing privilege was not silently revoked.
        Assert.True(await environment.ScalarAsync("""
            SELECT to_regnamespace('security') IS NULL AND to_regnamespace('extensions') IS NULL
               AND has_database_privilege('paqueteria_migrator', current_database(), 'CREATE')
            """));
    }

    [PostgreSqlContractFact]
    public async Task Preexisting_lifecycle_executor_security_create_fails_closed_before_the_bridge_grant()
    {
        // AI-06 creates schema security inside the bridge transaction, so the unintended prestate is seeded
        // as a default privilege of the deployment principal: the executor holds CREATE the moment it exists.
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgelifecycleprestate");
        await environment.AdminExecuteAsync(
            $"ALTER DEFAULT PRIVILEGES FOR ROLE {environment.Login} GRANT CREATE ON SCHEMAS TO paqueteria_lifecycle_executor");
        var stages = new List<string>();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();

        var exception = await Assert.ThrowsAsync<E002GuardException>(() => new DatabaseBaselineDeployer(stageObserver: stages.Add)
            .ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge));

        Assert.Equal("E002_CREATE_PRESTATE_PRESENT", exception.GuardCode);
        Assert.Contains("e002-capability-gate", stages);
        Assert.DoesNotContain("e002-grant-security-create", stages);
        // Rolled back, and the operator-owned default privilege is left in place rather than silently removed.
        Assert.True(await environment.ScalarAsync($"""
            SELECT to_regnamespace('security') IS NULL AND to_regnamespace('extensions') IS NULL
               AND EXISTS (SELECT 1 FROM pg_default_acl
                           WHERE defaclrole = '{environment.Login}'::regrole AND defaclobjtype = 'n')
            """));
    }

    [Fact]
    public void E002_models_the_lifecycle_cleanup_registration_and_session_executors_as_canonical_specialized_roles()
    {
        Assert.Equal(
            [
                ("paqueteria_migrator", false), ("paqueteria_app", false), ("paqueteria_worker", false),
                ("paqueteria_bootstrap", true), ("paqueteria_outbox_executor", true), ("paqueteria_maintenance", true),
                ("paqueteria_lifecycle_executor", true), ("paqueteria_cleanup_executor", true),
                ("paqueteria_registration_executor", true),
                ("paqueteria_session_executor", true),
                ("paqueteria_master_data_executor", true),
                ("paqueteria_master_data_loader", false),
            ],
            E002Guards.CanonicalRoles);
        Assert.Equal(
            [
                "paqueteria_bootstrap", "paqueteria_outbox_executor", "paqueteria_maintenance", "paqueteria_lifecycle_executor",
                "paqueteria_cleanup_executor", "paqueteria_registration_executor", "paqueteria_session_executor",
                "paqueteria_master_data_executor",
            ],
            E002Guards.SpecializedOwners);
        Assert.Equal(
            ["paqueteria_master_data_executor", "paqueteria_master_data_loader"],
            E002Guards.LaneIntroducedRoles);
        Assert.Equal(
            Identity.Infrastructure.Persistence.Migrations.AddBffSessionStore.MigrationId,
            E002BffSessionStateReader.SessionStoreMigrationId);
        Assert.Equal(
            Custody.Infrastructure.Persistence.Migrations.AddBffSessionPurge.MigrationId,
            E002BffSessionStateReader.PurgeMigrationId);
        Assert.Equal(
            Organizations.Infrastructure.Persistence.Migrations.AddSelfServiceRegistration.MigrationId,
            E002RegistrationStateReader.Reg001MigrationId);
        var registration = E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true, ops003Applied: true,
                reg001Applied: true)
            .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true, ops003Applied: true))
            .ToArray();
        Assert.Equal(
            Organizations.Infrastructure.Persistence.Migrations.AddSelfServiceRegistration.OwnedFunctions,
            registration.Select(entry => entry.Signature));
        Assert.All(registration, entry =>
        {
            Assert.Equal("paqueteria_registration_executor", entry.Owner);
            Assert.Equal(["paqueteria_app"], entry.Grantees);
        });
        Assert.Equal("ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, dispatchLaneApplied: true, ops003Applied: true,
                reg001Applied: true));
        Assert.Equal(36, E002RoutineMap.Select(
            E002RoutineMapState.Applied, lif001Applied: true, dispatchLaneApplied: true, ops003Applied: true,
            reg001Applied: true).Count);
        Assert.Equal(
            Orders.Infrastructure.Persistence.Migrations.AddOrderLifecycleFinalizationExecutor.MigrationId,
            E002LifecycleStateReader.Lif001MigrationId);
        Assert.Equal(
            Custody.Infrastructure.Persistence.Migrations.AddOperationalCleanupExecutor.MigrationId,
            E002CleanupStateReader.Ops003MigrationId);

        var lifecycle = Assert.Single(
            E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true, ops003Applied: false)
                .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: false, ops003Applied: false)));
        Assert.Equal(
            new E002RoutineEntry("security.finalize_expired_orders(integer)", "paqueteria_lifecycle_executor", lifecycle.Grantees),
            lifecycle);
        Assert.Equal(["paqueteria_worker"], lifecycle.Grantees);

        var cleanup = E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true, ops003Applied: true)
            .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true, ops003Applied: false))
            .ToArray();
        Assert.Equal(
            [
                "security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)",
                "security.expire_proof_upload_sessions(integer)",
            ],
            cleanup.Select(entry => entry.Signature));
        Assert.All(cleanup, entry =>
        {
            Assert.Equal("paqueteria_cleanup_executor", entry.Owner);
            Assert.Equal(["paqueteria_worker"], entry.Grantees);
        });
        Assert.Equal(
            Custody.Infrastructure.Persistence.Migrations.AddOperationalCleanupExecutor.OwnedFunctions,
            cleanup.Select(entry => entry.Signature));

        Assert.Equal("ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_V1", E002RoutineMap.Name(E002RoutineMapState.Applied, false));
        Assert.Equal("ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_V1", E002RoutineMap.Name(E002RoutineMapState.Applied, true));
        Assert.Equal("ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_OPS003_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, ops003Applied: true));
        Assert.Equal("ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_OPS003_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, false, ops003Applied: true));
        Assert.Equal("ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, dispatchLaneApplied: true, ops003Applied: true));
        Assert.Equal(31, E002RoutineMap.Select(
            E002RoutineMapState.Applied, lif001Applied: true, dispatchLaneApplied: true, ops003Applied: true).Count);
        Assert.Equal("ROUTINE_MAP_AI18_PENDING_V1", E002RoutineMap.Name(E002RoutineMapState.Pending, false));
        Assert.Equal("ROUTINE_MAP_AI18_PENDING_PLUS_LIF001_V1", E002RoutineMap.Name(E002RoutineMapState.Pending, true));
        Assert.Equal(11, E002RoutineMap.Select(E002RoutineMapState.Pending, lif001Applied: false, ops003Applied: false).Count);
        Assert.Equal(12, E002RoutineMap.Select(E002RoutineMapState.Pending, lif001Applied: true, ops003Applied: false).Count);
        Assert.Equal(13, E002RoutineMap.Select(E002RoutineMapState.Pending, lif001Applied: false, ops003Applied: true).Count);
        Assert.Equal(14, E002RoutineMap.Select(E002RoutineMapState.Pending, lif001Applied: true, ops003Applied: true).Count);

        // BFF-SESSION-TABLE-SHAPE: six session routines for paqueteria_app, one purge for the Worker.
        var sessions = E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, bffSessionApplied: true)
            .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true))
            .ToArray();
        Assert.Equal(
            Identity.Infrastructure.Persistence.Migrations.AddBffSessionStore.OwnedFunctions,
            sessions.Select(entry => entry.Signature));
        Assert.All(sessions, entry =>
        {
            Assert.Equal("paqueteria_session_executor", entry.Owner);
            Assert.Equal(["paqueteria_app"], entry.Grantees);
        });
        var purge = Assert.Single(
            E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, bffSessionApplied: true, bffPurgeApplied: true)
                .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, bffSessionApplied: true)));
        Assert.Equal(
            new E002RoutineEntry("security.purge_bff_sessions(integer)", "paqueteria_cleanup_executor", purge.Grantees),
            purge);
        Assert.Equal(["paqueteria_worker"], purge.Grantees);
        Assert.Equal(38, E002RoutineMap.Select(
            E002RoutineMapState.Applied, true, true, true, bffSessionApplied: true, bffPurgeApplied: true).Count);
        Assert.Equal(
            "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_BFFSESSION_PLUS_BFFPURGE_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true, true, bffSessionApplied: true, bffPurgeApplied: true));
        // REG-001 and the BFF session store together: every lane of the pilot installation.
        Assert.Equal(43, E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true).Count);
        Assert.Equal(
            "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true, true, true, true, true));

        // REG-002: four more registration-executor routines for paqueteria_app, after REG-001.
        Assert.Equal(
            Organizations.Infrastructure.Persistence.Migrations.AddPendingMemberships.MigrationId,
            E002RegistrationStateReader.Reg002MigrationId);
        var pending = E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, reg002Applied: true)
            .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true))
            .ToArray();
        Assert.Equal(
            Organizations.Infrastructure.Persistence.Migrations.AddPendingMemberships.OwnedFunctions,
            pending.Select(entry => entry.Signature));
        Assert.All(pending, entry =>
        {
            Assert.Equal("paqueteria_registration_executor", entry.Owner);
            Assert.Equal(["paqueteria_app"], entry.Grantees);
        });
        Assert.Equal(47, E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true).Count);
        Assert.Equal(
            "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true, true, true, true, true, true));

        // MDM-001-OPERATOR-LOADER: one master-data-executor routine for the operator grantee only.
        Assert.Equal(
            Pricing.Infrastructure.Persistence.Migrations.AddMasterDataLoader.MigrationId,
            E002MasterDataStateReader.MigrationId);
        var masterData = Assert.Single(
            E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true, mdm001Applied: true)
                .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true)));
        Assert.Equal(Pricing.Infrastructure.Persistence.Migrations.AddMasterDataLoader.FunctionSignature, masterData.Signature);
        Assert.Equal("paqueteria_master_data_executor", masterData.Owner);
        Assert.Equal(["paqueteria_master_data_loader"], masterData.Grantees);
        Assert.Equal(48, E002RoutineMap.Select(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true).Count);
        Assert.Equal(
            "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true, true, true, true, true, true, true));
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_requires_set_authority_over_the_cleanup_executor()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgecleanupset");
        await environment.AdminExecuteAsync(
            $"REVOKE SET OPTION FOR paqueteria_cleanup_executor FROM {environment.Login}");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();

        var exception = await Assert.ThrowsAsync<E002GuardException>(() => new DatabaseBaselineDeployer()
            .ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge));

        Assert.Equal("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING", exception.GuardCode);
        Assert.Contains("roles=paqueteria_cleanup_executor", exception.Message, StringComparison.Ordinal);
        Assert.True(await environment.ScalarAsync(
            "SELECT to_regnamespace('security') IS NULL AND to_regnamespace('extensions') IS NULL"));
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_applies_the_ops003_custody_lane_as_non_superuser()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgeops003");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        var coordinator = new ModuleMigrationCoordinator();

        // Every other lane is applied by the privileged fixture principal; this contract is the OPS-003 step.
        // Rewinding it leaves a populated installation whose cleanup executor was pre-provisioned per E-002.
        await coordinator.ApplyAsync(environment.AdminConnectionString, CancellationToken.None, azureOwnershipBridge: true);
        await environment.AdminExecuteAsync($"""
            DROP FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean);
            DROP FUNCTION security.expire_proof_upload_sessions(integer);
            DROP FUNCTION security.purge_bff_sessions(integer);
            REVOKE ALL ON identity.bff_sessions FROM paqueteria_cleanup_executor;
            REVOKE ALL ON identity.bff_logout_jtis FROM paqueteria_cleanup_executor;
            REVOKE USAGE ON SCHEMA identity FROM paqueteria_cleanup_executor;
            DELETE FROM platform."__ef_migrations_history_custody"
              WHERE "MigrationId" IN ('{E002CleanupStateReader.Ops003MigrationId}','{E002BffSessionStateReader.PurgeMigrationId}');
            """);
        Assert.Equal("PENDING", await CustodyLaneAsync());
        const string SecurityAclSql = "SELECT nspacl::text FROM pg_namespace WHERE nspname='security'";
        var securityAclBefore = await environment.TextAsync(SecurityAclSql);

        // Without the E-002 bridge the managed-service principal cannot hand the functions to the executor.
        var denied = FindPostgresException(await Assert.ThrowsAnyAsync<Exception>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal("PENDING", await CustodyLaneAsync());

        // Fail closed on a pre-existing temporary CREATE, and leave it for the operator.
        await environment.AdminExecuteAsync("GRANT CREATE ON SCHEMA security TO paqueteria_cleanup_executor");
        var prestate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_CREATE_PRESTATE_PRESENT role=paqueteria_cleanup_executor", prestate.Message, StringComparison.Ordinal);
        await environment.AdminExecuteAsync("REVOKE CREATE ON SCHEMA security FROM paqueteria_cleanup_executor");

        // Fail closed without effective SET authority over the executor.
        await environment.AdminExecuteAsync($"REVOKE SET OPTION FOR paqueteria_cleanup_executor FROM {environment.Login}");
        var capability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_cleanup_executor", capability.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync($"GRANT paqueteria_cleanup_executor TO {environment.Login} WITH SET TRUE");
        Assert.Equal("PENDING", await CustodyLaneAsync());
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true);

        Assert.All(await coordinator.AssertAsync(environment.DeploymentConnectionString, CancellationToken.None),
            state => Assert.Equal("APPLIED", state.Status));
        Assert.Equal(
            "paqueteria_cleanup_executor|{paqueteria_cleanup_executor=X/paqueteria_cleanup_executor,paqueteria_worker=X/paqueteria_cleanup_executor}" +
            ";paqueteria_cleanup_executor|{paqueteria_cleanup_executor=X/paqueteria_cleanup_executor,paqueteria_worker=X/paqueteria_cleanup_executor}" +
            ";paqueteria_cleanup_executor|{paqueteria_cleanup_executor=X/paqueteria_cleanup_executor,paqueteria_worker=X/paqueteria_cleanup_executor}",
            await environment.TextAsync("""
                SELECT string_agg(pg_get_userbyid(proowner) || '|' || proacl::text, ';' ORDER BY proname)
                FROM pg_proc
                WHERE oid IN ('security.purge_expired_idempotency_keys(timestamptz,integer,boolean)'::regprocedure,
                              'security.expire_proof_upload_sessions(integer)'::regprocedure,
                              'security.purge_bff_sessions(integer)'::regprocedure)
                """));
        Assert.False(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_cleanup_executor','security','CREATE')"));
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await new DatabaseBaselineAssertions().AssertAsync(admin);
        }

        await using (var deployment = new NpgsqlConnection(environment.DeploymentConnectionString))
        {
            await deployment.OpenAsync();
            var semantic = await new E002SemanticAssertions().AssertAsync(deployment, E002NotificationState.Applied);
            Assert.Equal(
                "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_V1",
                semantic.RoutineMap);
            Assert.Equal(48, semantic.ControlledIdentities);
            Assert.Equal(94, semantic.NormalizedExecuteRows);
        }

        async Task<string> CustodyLaneAsync() =>
            (await coordinator.PlanAsync(environment.DeploymentConnectionString, CancellationToken.None))
                .Single(state => state.Module == "Custody").Status;
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_applies_the_bff_session_identity_lane_as_non_superuser()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgebffsession");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        var coordinator = new ModuleMigrationCoordinator();

        // Every other lane is applied by the privileged fixture principal; this contract is the Identity
        // BFF step. The canonical table and grants stay (AI-06/AI-18); only the lane's functions are rewound.
        await coordinator.ApplyAsync(environment.AdminConnectionString, CancellationToken.None, azureOwnershipBridge: true);
        await environment.AdminExecuteAsync($"""
            DROP FUNCTION security.create_bff_session(bytea,text,text,bytea,timestamptz);
            DROP FUNCTION security.resolve_bff_session(bytea);
            DROP FUNCTION security.revoke_bff_session(bytea);
            DROP FUNCTION security.revoke_bff_session(text);
            DROP FUNCTION security.revoke_bff_session(text,timestamptz);
            DROP FUNCTION security.register_bff_logout_jti(bytea,timestamptz);
            DELETE FROM platform."__ef_migrations_history_identity"
              WHERE "MigrationId"='{E002BffSessionStateReader.SessionStoreMigrationId}';
            """);
        Assert.Equal("PENDING", await IdentityLaneAsync());
        const string SecurityAclSql = "SELECT nspacl::text FROM pg_namespace WHERE nspname='security'";
        var securityAclBefore = await environment.TextAsync(SecurityAclSql);

        // Without the E-002 bridge the managed-service principal cannot hand the functions to the executor.
        var denied = FindPostgresException(await Assert.ThrowsAnyAsync<Exception>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal("PENDING", await IdentityLaneAsync());

        await environment.AdminExecuteAsync("GRANT CREATE ON SCHEMA security TO paqueteria_session_executor");
        var prestate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_CREATE_PRESTATE_PRESENT role=paqueteria_session_executor", prestate.Message, StringComparison.Ordinal);
        await environment.AdminExecuteAsync("REVOKE CREATE ON SCHEMA security FROM paqueteria_session_executor");

        await environment.AdminExecuteAsync($"REVOKE SET OPTION FOR paqueteria_session_executor FROM {environment.Login}");
        var capability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_session_executor", capability.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync($"GRANT paqueteria_session_executor TO {environment.Login} WITH SET TRUE");
        Assert.Equal("PENDING", await IdentityLaneAsync());
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true);

        Assert.All(await coordinator.AssertAsync(environment.DeploymentConnectionString, CancellationToken.None),
            state => Assert.Equal("APPLIED", state.Status));
        Assert.Equal(
            string.Join(';', Enumerable.Repeat(
                "paqueteria_session_executor|{paqueteria_session_executor=X/paqueteria_session_executor,paqueteria_app=X/paqueteria_session_executor}",
                6)),
            await environment.TextAsync("""
                SELECT string_agg(pg_get_userbyid(proowner) || '|' || proacl::text, ';' ORDER BY oid::regprocedure::text)
                FROM pg_proc WHERE proname IN ('create_bff_session','resolve_bff_session','revoke_bff_session','register_bff_logout_jti')
                """));
        Assert.False(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_session_executor','security','CREATE')"));
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await new DatabaseBaselineAssertions().AssertAsync(admin);
        }

        await using (var deployment = new NpgsqlConnection(environment.DeploymentConnectionString))
        {
            await deployment.OpenAsync();
            var semantic = await new E002SemanticAssertions().AssertAsync(deployment, E002NotificationState.Applied);
            Assert.Equal(
                "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_V1",
                semantic.RoutineMap);
            Assert.Equal(48, semantic.ControlledIdentities);
            Assert.Equal(94, semantic.NormalizedExecuteRows);
        }

        async Task<string> IdentityLaneAsync() =>
            (await coordinator.PlanAsync(environment.DeploymentConnectionString, CancellationToken.None))
                .Single(state => state.Module == "Identity").Status;
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_applies_the_mdm001_pricing_lane_as_non_superuser()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgemdm001");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        var coordinator = new ModuleMigrationCoordinator();

        // Every other lane is applied by the privileged fixture principal; this contract is the MDM-001 step.
        // Rewinding it leaves a populated installation whose master data roles were pre-provisioned per E-002.
        await coordinator.ApplyAsync(environment.AdminConnectionString, CancellationToken.None, azureOwnershipBridge: true);
        await environment.AdminExecuteAsync($"""
            DROP FUNCTION security.load_master_data(uuid,uuid,jsonb,bytea,boolean);
            DELETE FROM platform."__ef_migrations_history_pricing"
              WHERE "MigrationId"='{E002MasterDataStateReader.MigrationId}';
            """);
        Assert.Equal("PENDING", await PricingLaneAsync());
        const string SecurityAclSql = "SELECT nspacl::text FROM pg_namespace WHERE nspname='security'";
        var securityAclBefore = await environment.TextAsync(SecurityAclSql);

        // Without the E-002 bridge the managed-service principal cannot hand the function to the executor.
        var denied = FindPostgresException(await Assert.ThrowsAnyAsync<Exception>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal("PENDING", await PricingLaneAsync());

        // Fail closed on a pre-existing temporary CREATE, and leave it for the operator.
        await environment.AdminExecuteAsync("GRANT CREATE ON SCHEMA security TO paqueteria_master_data_executor");
        var prestate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_CREATE_PRESTATE_PRESENT role=paqueteria_master_data_executor", prestate.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync("REVOKE CREATE ON SCHEMA security FROM paqueteria_master_data_executor");

        // Fail closed without effective SET authority over the executor.
        await environment.AdminExecuteAsync($"REVOKE SET OPTION FOR paqueteria_master_data_executor FROM {environment.Login}");
        var capability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_master_data_executor", capability.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync($"GRANT paqueteria_master_data_executor TO {environment.Login} WITH SET TRUE");
        Assert.Equal("PENDING", await PricingLaneAsync());
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true);

        Assert.All(await coordinator.AssertAsync(environment.DeploymentConnectionString, CancellationToken.None),
            state => Assert.Equal("APPLIED", state.Status));
        Assert.Equal(
            "paqueteria_master_data_executor|{paqueteria_master_data_executor=X/paqueteria_master_data_executor,paqueteria_master_data_loader=X/paqueteria_master_data_executor}",
            await environment.TextAsync("""
                SELECT string_agg(pg_get_userbyid(proowner) || '|' || proacl::text, ';' ORDER BY proname)
                FROM pg_proc WHERE proowner='paqueteria_master_data_executor'::regrole
                """));
        Assert.False(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_master_data_executor','security','CREATE')"));
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await new DatabaseBaselineAssertions().AssertAsync(admin);
        }

        await using (var deployment = new NpgsqlConnection(environment.DeploymentConnectionString))
        {
            await deployment.OpenAsync();
            var semantic = await new E002SemanticAssertions().AssertAsync(deployment, E002NotificationState.Applied);
            Assert.Equal(
                "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_V1",
                semantic.RoutineMap);
            Assert.Equal(48, semantic.ControlledIdentities);
            Assert.Equal(94, semantic.NormalizedExecuteRows);
        }

        async Task<string> PricingLaneAsync() =>
            (await coordinator.PlanAsync(environment.DeploymentConnectionString, CancellationToken.None))
                .Single(state => state.Module == "Pricing").Status;
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_applies_the_reg001_organizations_lane_as_non_superuser()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgereg001");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        var coordinator = new ModuleMigrationCoordinator();

        // Every other lane is applied by the privileged fixture principal; this contract is the REG-001 and REG-002
        // steps. Rewinding both leaves a populated installation whose registration executor was pre-provisioned
        // per E-002 and whose pending_memberships table the REG-002 lane adopts.
        await coordinator.ApplyAsync(environment.AdminConnectionString, CancellationToken.None, azureOwnershipBridge: true);
        await environment.AdminExecuteAsync($"""
            DROP FUNCTION security.register_identity_subject(text,uuid);
            DROP FUNCTION security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text);
            DROP FUNCTION security.list_own_organization_applications(uuid);
            DROP FUNCTION security.list_pending_ally_organizations(uuid,uuid,integer);
            DROP FUNCTION security.decide_ally_organization(uuid,uuid,uuid,boolean,text);
            DROP FUNCTION security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text);
            DROP FUNCTION security.renew_pending_membership(uuid,uuid,uuid,text,text);
            DROP FUNCTION security.revoke_pending_membership(uuid,uuid,uuid,text,text);
            DROP FUNCTION security.apply_pending_memberships(text,bytea[],integer[]);
            DELETE FROM platform."__ef_migrations_history_organizations"
              WHERE "MigrationId" IN ('{E002RegistrationStateReader.Reg001MigrationId}','{E002RegistrationStateReader.Reg002MigrationId}');
            """);
        Assert.Equal("PENDING", await OrganizationsLaneAsync());
        const string SecurityAclSql = "SELECT nspacl::text FROM pg_namespace WHERE nspname='security'";
        var securityAclBefore = await environment.TextAsync(SecurityAclSql);

        // Without the E-002 bridge the managed-service principal cannot hand the functions to the executor.
        var denied = FindPostgresException(await Assert.ThrowsAnyAsync<Exception>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal("PENDING", await OrganizationsLaneAsync());

        // Fail closed on a pre-existing temporary CREATE, and leave it for the operator.
        await environment.AdminExecuteAsync("GRANT CREATE ON SCHEMA security TO paqueteria_registration_executor");
        var prestate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_CREATE_PRESTATE_PRESENT role=paqueteria_registration_executor", prestate.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync("REVOKE CREATE ON SCHEMA security FROM paqueteria_registration_executor");

        // Fail closed without effective SET authority over the executor.
        await environment.AdminExecuteAsync($"REVOKE SET OPTION FOR paqueteria_registration_executor FROM {environment.Login}");
        var capability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_registration_executor", capability.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync($"GRANT paqueteria_registration_executor TO {environment.Login} WITH SET TRUE");
        Assert.Equal("PENDING", await OrganizationsLaneAsync());
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true);

        Assert.All(await coordinator.AssertAsync(environment.DeploymentConnectionString, CancellationToken.None),
            state => Assert.Equal("APPLIED", state.Status));
        Assert.Equal(
            string.Join(';', Enumerable.Repeat(
                "paqueteria_registration_executor|{paqueteria_registration_executor=X/paqueteria_registration_executor,paqueteria_app=X/paqueteria_registration_executor}",
                9)),
            await environment.TextAsync("""
                SELECT string_agg(pg_get_userbyid(proowner) || '|' || proacl::text, ';' ORDER BY proname)
                FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole
                """));
        Assert.False(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_registration_executor','security','CREATE')"));
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));

        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await new DatabaseBaselineAssertions().AssertAsync(admin);
        }

        await using (var deployment = new NpgsqlConnection(environment.DeploymentConnectionString))
        {
            await deployment.OpenAsync();
            var semantic = await new E002SemanticAssertions().AssertAsync(deployment, E002NotificationState.Applied);
            Assert.Equal(
                "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_V1",
                semantic.RoutineMap);
            Assert.Equal(48, semantic.ControlledIdentities);
            Assert.Equal(94, semantic.NormalizedExecuteRows);
        }

        async Task<string> OrganizationsLaneAsync() =>
            (await coordinator.PlanAsync(environment.DeploymentConnectionString, CancellationToken.None))
                .Single(state => state.Module == "Organizations").Status;
    }

    [Fact]
    public void E002_models_the_d8_dispatch_lane_as_worker_only_outbox_executor_routines()
    {
        // D8-OUTBOX-LANE-DISPATCH: two routines owned by paqueteria_outbox_executor, EXECUTE for the Worker only.
        Assert.Equal(
            Notifications.Infrastructure.Persistence.Migrations.AddDispatchOutboxLane.MigrationId,
            E002NotificationStateReader.DispatchLaneMigrationId);
        var lane = E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true, dispatchLaneApplied: true)
            .Except(E002RoutineMap.Select(E002RoutineMapState.Applied, lif001Applied: true))
            .ToArray();
        Assert.Equal(
            ["security.claim_dispatch_outbox(text,integer,interval)",
             "security.requeue_stale_dispatch_outbox(interval,integer,integer)"],
            lane.Select(entry => entry.Signature));
        Assert.All(lane, entry =>
        {
            Assert.Equal("paqueteria_outbox_executor", entry.Owner);
            Assert.Equal(["paqueteria_worker"], entry.Grantees);
        });
        Assert.Equal(29, E002RoutineMap.Select(E002RoutineMapState.Applied, true, true).Count);
        Assert.Equal(
            "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, true, true));
        Assert.Equal(
            "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_D8DISPATCH_V1",
            E002RoutineMap.Name(E002RoutineMapState.Applied, false, true));
        Assert.Throws<InvalidOperationException>(() =>
            E002RoutineMap.Select(E002RoutineMapState.Pending, lif001Applied: false, dispatchLaneApplied: true));
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_requires_set_authority_over_the_lifecycle_executor()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgelifecycleset");
        await environment.AdminExecuteAsync(
            $"REVOKE SET OPTION FOR paqueteria_lifecycle_executor FROM {environment.Login}");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();

        var exception = await Assert.ThrowsAsync<E002GuardException>(() => new DatabaseBaselineDeployer()
            .ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge));

        Assert.Equal("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING", exception.GuardCode);
        Assert.Contains("roles=paqueteria_lifecycle_executor", exception.Message, StringComparison.Ordinal);
        Assert.True(await environment.ScalarAsync(
            "SELECT to_regnamespace('security') IS NULL AND to_regnamespace('extensions') IS NULL"));
    }

    [PostgreSqlContractFact]
    public async Task Azure_ownership_bridge_applies_the_lif001_orders_lane_as_non_superuser()
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync("bridgelif001");
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge);
        var coordinator = new ModuleMigrationCoordinator();

        // Every other lane is applied by the privileged fixture principal; this contract is the LIF-001 step.
        // Rewinding it leaves a populated installation whose lifecycle executor was pre-provisioned per E-002.
        await coordinator.ApplyAsync(environment.AdminConnectionString, CancellationToken.None, azureOwnershipBridge: true);
        await environment.AdminExecuteAsync($"""
            DROP FUNCTION security.finalize_expired_orders(integer);
            REVOKE USAGE ON SCHEMA orders FROM paqueteria_lifecycle_executor;
            REVOKE SELECT (id,status,claim_window_ends_at,finalized_at), UPDATE (finalized_at)
              ON orders.orders FROM paqueteria_lifecycle_executor;
            DELETE FROM platform."__ef_migrations_history_orders"
              WHERE "MigrationId"='{E002LifecycleStateReader.Lif001MigrationId}';
            """);
        Assert.Equal("PENDING", await OrdersLaneAsync());
        const string SecurityAclSql = "SELECT nspacl::text FROM pg_namespace WHERE nspname='security'";
        const string MembershipSql = """
            SELECT COALESCE(string_agg(pg_get_userbyid(member) || '>' || pg_get_userbyid(roleid) || ':' ||
                admin_option || inherit_option || set_option, ',' ORDER BY member, roleid), '')
            FROM pg_auth_members
            WHERE roleid='paqueteria_lifecycle_executor'::regrole OR member='paqueteria_lifecycle_executor'::regrole
            """;
        var securityAclBefore = await environment.TextAsync(SecurityAclSql);
        var membershipsBefore = await environment.TextAsync(MembershipSql);
        Assert.False(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_lifecycle_executor','security','CREATE')"));

        // Without the E-002 bridge the managed-service principal cannot hand the function to the executor.
        var denied = FindPostgresException(await Assert.ThrowsAnyAsync<Exception>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Contains("schema security", denied.MessageText, StringComparison.Ordinal);
        Assert.Equal("PENDING", await OrdersLaneAsync());

        // Fail closed on a pre-existing temporary CREATE, and leave it for the operator.
        await environment.AdminExecuteAsync("GRANT CREATE ON SCHEMA security TO paqueteria_lifecycle_executor");
        var prestate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_CREATE_PRESTATE_PRESENT", prestate.Message, StringComparison.Ordinal);
        Assert.True(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_lifecycle_executor','security','CREATE')"));
        await environment.AdminExecuteAsync("REVOKE CREATE ON SCHEMA security FROM paqueteria_lifecycle_executor");

        // Fail closed without effective SET authority over the executor.
        await environment.AdminExecuteAsync($"REVOKE SET OPTION FOR paqueteria_lifecycle_executor FROM {environment.Login}");
        var capability = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true));
        Assert.StartsWith("E002_EFFECTIVE_ROLE_CAPABILITY_MISSING roles=paqueteria_lifecycle_executor", capability.Message,
            StringComparison.Ordinal);
        await environment.AdminExecuteAsync($"GRANT paqueteria_lifecycle_executor TO {environment.Login} WITH SET TRUE");
        Assert.Equal("PENDING", await OrdersLaneAsync());
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));
        Assert.Equal(membershipsBefore, await environment.TextAsync(MembershipSql));

        await coordinator.ApplyAsync(environment.DeploymentConnectionString, CancellationToken.None, azureOwnershipBridge: true);

        Assert.All(await coordinator.AssertAsync(environment.DeploymentConnectionString, CancellationToken.None),
            state => Assert.Equal("APPLIED", state.Status));
        Assert.Equal(
            "paqueteria_lifecycle_executor|{paqueteria_lifecycle_executor=X/paqueteria_lifecycle_executor,paqueteria_worker=X/paqueteria_lifecycle_executor}",
            await environment.TextAsync("""
                SELECT pg_get_userbyid(proowner) || '|' || proacl::text
                FROM pg_proc WHERE oid='security.finalize_expired_orders(integer)'::regprocedure
                """));
        Assert.False(await environment.ScalarAsync(
            "SELECT has_schema_privilege('paqueteria_lifecycle_executor','security','CREATE')"));
        Assert.Equal(securityAclBefore, await environment.TextAsync(SecurityAclSql));
        Assert.Equal(membershipsBefore, await environment.TextAsync(MembershipSql));

        await using (var admin = new NpgsqlConnection(environment.AdminConnectionString))
        {
            await admin.OpenAsync();
            await new DatabaseBaselineAssertions().AssertAsync(admin);
            await using var transaction = await admin.BeginTransactionAsync();
            await using var finalize = new NpgsqlCommand(
                "SET LOCAL ROLE paqueteria_worker; SELECT security.finalize_expired_orders(10);", admin, transaction);
            Assert.Equal(0, await finalize.ExecuteScalarAsync());
        }

        await using (var deployment = new NpgsqlConnection(environment.DeploymentConnectionString))
        {
            await deployment.OpenAsync();
            var semantic = await new E002SemanticAssertions().AssertAsync(deployment, E002NotificationState.Applied);
            // The Notifications lane also installed the D8 DISPATCH lane and the Custody OPS-003 lane is applied
            // too: two routines each, owner + Worker.
            Assert.Equal(
                "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_V1",
                semantic.RoutineMap);
            Assert.Equal(48, semantic.ControlledIdentities);
            Assert.Equal(94, semantic.NormalizedExecuteRows);
        }

        async Task<string> OrdersLaneAsync() =>
            (await coordinator.PlanAsync(environment.DeploymentConnectionString, CancellationToken.None))
                .Single(state => state.Module == "Orders").Status;
    }

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a PostgresException, observed {exception.GetType().Name}: {exception.Message}");
    }

    public static TheoryData<string, bool, bool, string?> PlatformPreflightFailures => new()
    {
        { "E002_PLATFORM_DEPLOYMENT_ROLE_ATTRIBUTES", false, true, "POSTGIS,PGCRYPTO" },
        { "E002_PLATFORM_AZURE_ADMIN_CAPABILITY", true, false, "POSTGIS,PGCRYPTO" },
        { "E002_PLATFORM_EXTENSION_ALLOWLIST", true, true, "POSTGIS" },
        { "E002_PLATFORM_EXTENSION_ALLOWLIST", true, true, null },
    };

    [Theory]
    [MemberData(nameof(PlatformPreflightFailures))]
    public async Task Platform_preflight_failures_emit_distinct_normative_guards_before_mutation(
        string expectedGuard, bool bypassRls, bool azureAdmin, string? allowlist)
    {
        await using var environment = await new AzureLikeEnvironment(fixture).InitializeAsync(
            "bridgepreflight", bypassRls, azureAdmin, allowlist);
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        var stages = new List<string>();
        var exception = await Assert.ThrowsAsync<E002GuardException>(() => new DatabaseBaselineDeployer(stageObserver: stages.Add)
            .ApplyAsync(baseline, environment.DeploymentConnectionString, ownershipBridge: Bridge));

        Assert.Equal(expectedGuard, exception.GuardCode);
        Assert.Equal("platform-preflight", exception.Phase);
        Assert.Equal(["e002-platform-preflight"], stages);
        Assert.True(await environment.ScalarAsync("SELECT to_regnamespace('security') IS NULL"));
    }

    [PostgreSqlContractFact]
    public async Task Partial_database_fails_closed_without_completing_the_baseline()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("partial");
        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("CREATE SCHEMA identity", connection);
                await command.ExecuteNonQueryAsync();
            }

            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var deployer = new DatabaseBaselineDeployer();
            var exception = await Assert.ThrowsAsync<PartialDatabaseBaselineException>(
                () => deployer.ApplyAsync(baseline, connectionString));

            Assert.Equal(DatabaseBaselineStatus.Partial, exception.State.Status);
            Assert.Contains("schema:identity", exception.State.PresentCriticalObjects);

            await using var verificationConnection = new NpgsqlConnection(connectionString);
            await verificationConnection.OpenAsync();
            await using var verification = new NpgsqlCommand(
                "SELECT to_regclass('platform.outbox_events') IS NULL AND to_regnamespace('orders') IS NULL",
                verificationConnection);
            Assert.True((bool)(await verification.ExecuteScalarAsync())!);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_deployers_serialize_and_leave_one_complete_baseline()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("concurrent");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var first = new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var second = new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromMinutes(3));

            Assert.Equal(
                [DatabaseBaselineApplyStatus.Applied, DatabaseBaselineApplyStatus.AlreadyApplied],
                results.Select(result => result.Status).Order());

            var report = await new DatabaseBaselineDeployer().AssertAsync(baseline, connectionString);
            Assert.True(report.Checks >= 10);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Canonical_bytes_are_rejected_when_the_hash_does_not_match()
    {
        var temporaryRoot = await CreateTemporaryBaselineAsync();
        try
        {
            var schemaPath = Path.Combine(temporaryRoot, CanonicalBaselineContract.SchemaRelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(schemaPath, Environment.NewLine + "-- unauthorized mutation");

            var exception = await Assert.ThrowsAsync<BaselineVerificationException>(
                () => new DatabaseBaselineVerifier().VerifyAsync(temporaryRoot));
            Assert.Contains("Canonical hash mismatch", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Reordered_steps_are_rejected_before_any_database_connection_is_needed()
    {
        var temporaryRoot = await CreateTemporaryBaselineAsync();
        try
        {
            var manifestPath = Path.Combine(temporaryRoot, CanonicalBaselineContract.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var steps = manifest["steps"]!.AsArray();
            var first = steps[0]!.DeepClone();
            var second = steps[1]!.DeepClone();
            steps[0] = second;
            steps[1] = first;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new() { WriteIndented = true }));

            var exception = await Assert.ThrowsAsync<BaselineVerificationException>(
                () => new DatabaseBaselineVerifier().VerifyAsync(temporaryRoot));
            Assert.Contains("Baseline step 1 must be", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    public static TheoryData<string> AssertionMutations => new()
    {
        { "ALTER TABLE platform.outbox_events OWNER TO postgres" },
        { "GRANT CREATE ON SCHEMA public TO PUBLIC" },
        { "GRANT SELECT ON platform.outbox_events TO paqueteria_app" },
        { "GRANT UPDATE ON platform.outbox_events TO paqueteria_maintenance" },
        { "GRANT EXECUTE ON FUNCTION security.resolve_identity_context(text) TO PUBLIC" },
        { "ALTER TABLE orders.order_events DISABLE TRIGGER order_events_append_only" },
    };

    [Theory]
    [MemberData(nameof(AssertionMutations))]
    public async Task Assertions_detect_forbidden_catalog_mutations(string mutation)
    {
        await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(mutation, connection, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<DatabaseAssertionException>(
            () => new DatabaseBaselineAssertions().AssertAsync(connection, transaction));
        Assert.NotEmpty(exception.Violations);
        await transaction.RollbackAsync();
    }

    private static async Task<string> CreateTemporaryBaselineAsync()
    {
        var sourceRoot = RepositoryRootLocator.Find();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"paqueteria-dba001-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        foreach (var relativePath in new[]
        {
            CanonicalBaselineContract.SchemaRelativePath,
            CanonicalBaselineContract.RolesRelativePath,
            CanonicalBaselineContract.ManifestRelativePath,
        })
        {
            var source = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(temporaryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var sourceStream = File.OpenRead(source);
            await using var targetStream = File.Create(target);
            await sourceStream.CopyToAsync(targetStream);
        }

        return temporaryRoot;
    }
}
