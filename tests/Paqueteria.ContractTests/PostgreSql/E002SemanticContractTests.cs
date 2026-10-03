using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class E002SemanticContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task State_selected_maps_are_exact_on_pending_and_applied_catalogs()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002maps");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var deployer = new DatabaseBaselineDeployer();
            Assert.Equal(DatabaseBaselineApplyStatus.Applied,
                (await deployer.ApplyAsync(baseline, connectionString)).Status);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                Assert.Equal(E002NotificationState.Pending,
                    await E002NotificationStateReader.ReadAsync(connection));
                var pending = await new E002SemanticAssertions().AssertAsync(
                    connection, E002NotificationState.Pending);
                Assert.Equal("ROUTINE_MAP_AI18_PENDING_V1", pending.RoutineMap);
                Assert.Equal(11, pending.ControlledIdentities);
                Assert.Equal(24, pending.NormalizedExecuteRows);
            }

            var membershipsBefore = await CountMembershipsAsync(connectionString);
            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None,
                azureOwnershipBridge: true);
            var membershipsAfter = await CountMembershipsAsync(connectionString);
            Assert.Equal(membershipsBefore, membershipsAfter);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                Assert.Equal(E002NotificationState.Applied,
                    await E002NotificationStateReader.ReadAsync(connection));
                var applied = await new E002SemanticAssertions().AssertAsync(
                    connection, E002NotificationState.Applied);
                // The coordinator also applied the Orders LIF-001 lane (ADR-034): one more routine, owner + Worker,
                // plus the D8 DISPATCH lane (D8-OUTBOX-LANE-DISPATCH) and the Custody OPS-003 lane
                // (OPS-003-CLEANUP-ROLE): two more routines each, owner + Worker.
                // REG-001 adds the Organizations lane: five registration routines, owner + paqueteria_app.
                // The Identity BFF session lane (BFF-SESSION-TABLE-SHAPE, BFF-LOGOUT-JTI-PERSISTENCE) adds six
                // routines, owner + paqueteria_app, and the Custody BFF purge one routine, owner + Worker.
                // DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 adds the Dispatch lane: two operator outbox
                // routines, owner + paqueteria_app.
                Assert.Equal(
                    "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED_PLUS_LIF001_PLUS_D8DISPATCH_PLUS_OPS003_PLUS_REG001_PLUS_BFFSESSION_PLUS_BFFPURGE_PLUS_REG002_PLUS_MDM001_PLUS_DSPOPOUTBOX_V1",
                    applied.RoutineMap);
                Assert.Equal(50, applied.ControlledIdentities);
                Assert.Equal(98, applied.NormalizedExecuteRows);
            }

            Assert.All(await new ModuleMigrationCoordinator().AssertAsync(connectionString,
                CancellationToken.None), state => Assert.Equal("APPLIED", state.Status));
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Semantic_role_and_acl_drift_fail_closed_without_changing_structural_state()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002drift");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            var deployer = new DatabaseBaselineDeployer();
            await deployer.ApplyAsync(baseline, connectionString);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var mutate = new NpgsqlCommand("""
                ALTER ROLE paqueteria_worker NOINHERIT;
                ALTER ROLE paqueteria_lifecycle_executor NOBYPASSRLS;
                ALTER ROLE paqueteria_cleanup_executor NOBYPASSRLS;
                GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;
                GRANT CREATE ON SCHEMA security TO paqueteria_lifecycle_executor;
                GRANT CREATE ON SCHEMA security TO paqueteria_cleanup_executor;
                GRANT USAGE ON SCHEMA notifications TO paqueteria_maintenance;
                ALTER FUNCTION security.purge_outbox(timestamptz,timestamptz,integer,boolean) OWNER TO paqueteria_migrator;
                ALTER FUNCTION security.claim_outbox(text,integer,interval) RESET search_path;
                """, connection, transaction))
            {
                await mutate.ExecuteNonQueryAsync();
            }

            var structural = await new DatabaseBaselineStateDetector().DetectAsync(connection, transaction);
            Assert.Equal(DatabaseBaselineStatus.Applied, structural.Status);
            var exception = await Assert.ThrowsAsync<E002SemanticException>(() =>
                new E002SemanticAssertions().AssertAsync(connection, E002NotificationState.Pending,
                    transaction));
            Assert.Contains(exception.Violations, value => value.Contains("role attributes differ: paqueteria_worker"));
            Assert.Contains(exception.Violations, value => value.Contains("role attributes differ: paqueteria_lifecycle_executor"));
            Assert.Contains(exception.Violations, value => value.Contains("temporary CREATE residue: security/paqueteria_outbox_executor"));
            Assert.Contains(exception.Violations, value => value.Contains("temporary CREATE residue: security/paqueteria_lifecycle_executor"));
            Assert.Contains(exception.Violations, value => value.Contains("role attributes differ: paqueteria_cleanup_executor"));
            Assert.Contains(exception.Violations, value => value.Contains("temporary CREATE residue: security/paqueteria_cleanup_executor"));
            // E-002 v0.8 §36: each failed invariant is classified by its own normative guard, not a generic code.
            Assert.Contains("E002_ROLE_ATTRIBUTE_MISMATCH", exception.GuardCodes);
            Assert.Contains("E002_BASELINE_SECURITY_ACL_MISMATCH", exception.GuardCodes);
            Assert.Contains("E002_NOTIFICATIONS_ACL_RESTORE_MISMATCH", exception.GuardCodes);
            Assert.Contains("E002_OWNER_MAP_MISMATCH", exception.GuardCodes);
            Assert.Contains("E002_SECURITY_DEFINER_UNSAFE", exception.GuardCodes);
            Assert.Contains(exception.Violations, value =>
                value.StartsWith("E002_OWNER_MAP_MISMATCH: routine owner differs: security.purge_outbox", StringComparison.Ordinal));
            Assert.Contains(exception.Violations, value =>
                value.StartsWith("E002_SECURITY_DEFINER_UNSAFE: search_path unsafe or missing: security.claim_outbox", StringComparison.Ordinal));
            Assert.StartsWith("E002_SEMANTIC_PARTIAL; STOP_FOR_CONTRACT_REVIEW", exception.Message, StringComparison.Ordinal);
            await transaction.RollbackAsync();

            await new E002SemanticAssertions().AssertAsync(connection, E002NotificationState.Pending);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Notifications_bridge_failure_rolls_back_catalog_history_and_create_grants()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002rollback");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            var owner = Guid.NewGuid();
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var setup = new NpgsqlCommand("""
                    INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
                    VALUES(@owner,'E002 Guard','E002 Guard','BUSINESS');
                    INSERT INTO platform.outbox_events(
                      id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                      payload,priority,status,attempts,available_at,created_at)
                    VALUES(gen_random_uuid(),@owner,
                      jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
                      'orders.created','Order',gen_random_uuid(),1,'{}',50,'PENDING',0,
                      clock_timestamp(),clock_timestamp());
                    """, connection);
                setup.Parameters.AddWithValue("owner", owner);
                await setup.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None,
                    azureOwnershipBridge: true));
            Assert.Contains("E002_NTF_BRIDGE_FAILED", exception.Message, StringComparison.Ordinal);
            Assert.Contains("cutover blocked", exception.Message, StringComparison.Ordinal);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                Assert.Equal(E002NotificationState.Pending,
                    await E002NotificationStateReader.ReadAsync(connection));
                await new E002SemanticAssertions().AssertAsync(connection, E002NotificationState.Pending);
                await using var probe = new NpgsqlCommand(
                    "SELECT to_regprocedure('security.resolve_outbox_consumer(text)') IS NULL", connection);
                Assert.True(await probe.ExecuteScalarAsync() is true);
            }
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Pool_reset_guard_and_second_lease_restore_deployment_identity()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.DeploymentConnectionString)
        {
            MaxPoolSize = 1,
            MinPoolSize = 0,
            Pooling = true,
            NoResetOnClose = false,
        };
        E002SemanticAssertions.AssertConnectionReset(builder.ConnectionString);
        var originalUser = builder.Username;
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        await using (var first = await source.OpenConnectionAsync())
        await using (var setRole = new NpgsqlCommand("SET ROLE paqueteria_migrator", first))
        {
            await setRole.ExecuteNonQueryAsync();
        }

        await using (var second = await source.OpenConnectionAsync())
        await using (var user = new NpgsqlCommand("SELECT current_user", second))
        {
            // E-002 v0.9 Amendment 5: a leaked SET ROLE on the next pooled lease is E002_CONNECTION_ROLE_STATE_LEAK.
            Assert.True(string.Equals(originalUser, await user.ExecuteScalarAsync() as string, StringComparison.Ordinal),
                "E002_CONNECTION_ROLE_STATE_LEAK; STOP_FOR_CONTRACT_REVIEW");
        }

        builder.NoResetOnClose = true;
        Assert.Contains("E002_CONNECTION_RESET_DISABLED",
            Assert.Throws<InvalidOperationException>(() =>
                E002SemanticAssertions.AssertConnectionReset(builder.ConnectionString)).Message,
            StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task History_drift_and_invalid_target_context_fail_before_map_selection()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002history");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var create = new NpgsqlCommand("""
                    CREATE TABLE platform.__ef_migrations_history_notifications (
                      "MigrationId" varchar(150) PRIMARY KEY,
                      "ProductVersion" varchar(32) NOT NULL);
                    INSERT INTO platform.__ef_migrations_history_notifications
                    VALUES('unexpected_migration','10.0');
                    """, connection, transaction);
                await create.ExecuteNonQueryAsync();
                var state = await E002NotificationStateReader.ReadAsync(connection, transaction);
                Assert.Equal(E002NotificationState.Drift, state);
                Assert.Contains("E002_ROUTINE_MAP_STATE_UNRESOLVED",
                    Assert.Throws<InvalidOperationException>(() =>
                        E002NotificationStateReader.SelectMap(state)).Message, StringComparison.Ordinal);
                await transaction.RollbackAsync();
            }

            await new ModuleMigrationCoordinator().ApplyAsync(connectionString, CancellationToken.None,
                azureOwnershipBridge: true);
            await using var targetTransaction = await connection.BeginTransactionAsync();
            Assert.Contains("E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID",
                (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new E002SemanticAssertions().AssertNtf001TargetAsync(connection, targetTransaction)))
                .Message, StringComparison.Ordinal);
            await targetTransaction.RollbackAsync();
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Null_function_acl_expands_to_owner_and_public_defaults()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE FUNCTION security.e002_null_acl_probe() RETURNS integer
            LANGUAGE sql AS 'SELECT 1'
            """, connection, transaction))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("""
            SELECT p.proacl IS NULL,
                   count(*) FILTER (WHERE a.privilege_type='EXECUTE'),
                   count(*) FILTER (WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_catalog.pg_proc p
            CROSS JOIN LATERAL pg_catalog.aclexplode(
              COALESCE(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
            WHERE p.oid=to_regprocedure('security.e002_null_acl_probe()')
            GROUP BY p.proacl
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(2, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        await reader.DisposeAsync();
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Public_grantable_unexpected_grantee_and_grantor_are_rejected()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002acl");
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var mutation = new NpgsqlCommand("""
                GRANT EXECUTE ON FUNCTION security.resolve_identity_context(text) TO PUBLIC;
                GRANT EXECUTE ON FUNCTION security.resolve_identity_context(text)
                  TO paqueteria_app WITH GRANT OPTION;
                SET LOCAL ROLE paqueteria_app;
                GRANT EXECUTE ON FUNCTION security.resolve_identity_context(text)
                  TO paqueteria_worker;
                RESET ROLE;
                """, connection, transaction))
            {
                await mutation.ExecuteNonQueryAsync();
            }

            var exception = await Assert.ThrowsAsync<E002SemanticException>(() =>
                new E002SemanticAssertions().AssertAsync(connection, E002NotificationState.Pending,
                    transaction));
            Assert.Contains(exception.Violations, value =>
                value.Contains("Grantee = PUBLIC", StringComparison.Ordinal));
            Assert.Contains(exception.Violations, value =>
                value.Contains("Grantable = True", StringComparison.Ordinal));
            Assert.Contains(exception.Violations, value =>
                value.Contains("Grantee = paqueteria_worker", StringComparison.Ordinal) &&
                value.Contains("Grantor = paqueteria_app", StringComparison.Ordinal));
            Assert.Equal(["E002_ROUTINE_ACL_MISMATCH"], exception.GuardCodes);
            await transaction.RollbackAsync();
            await new E002SemanticAssertions().AssertAsync(connection, E002NotificationState.Pending);
        }
        finally
        {
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Applied_semantic_guard_precedes_module_history_drift_planning()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002precedence");
        const string variable = "PAQUETERIA_E002_PRECEDENCE_TEST_DB";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
            await new DatabaseBaselineDeployer().ApplyAsync(baseline, connectionString);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var setup = new NpgsqlCommand("""
                    SET ROLE paqueteria_migrator;
                    CREATE TABLE platform.__ef_migrations_history_identity (
                      "MigrationId" varchar(150) PRIMARY KEY,
                      "ProductVersion" varchar(32) NOT NULL);
                    INSERT INTO platform.__ef_migrations_history_identity
                    VALUES('unexpected_migration','10.0');
                    GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;
                    RESET ROLE;
                    """, connection);
                await setup.ExecuteNonQueryAsync();
            }

            Environment.SetEnvironmentVariable(variable, connectionString);
            var semanticFirst = await DatabaseMigratorProgram.RunAsync(
                ["plan", "--connection-env", variable]);
            Assert.Equal(5, semanticFirst);

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var restore = new NpgsqlCommand("""
                    SET ROLE paqueteria_migrator;
                    REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
                    RESET ROLE;
                    """, connection);
                await restore.ExecuteNonQueryAsync();
            }

            var driftAfterSemanticPass = await DatabaseMigratorProgram.RunAsync(
                ["plan", "--connection-env", variable]);
            Assert.Equal(4, driftAfterSemanticPass);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [PostgreSqlContractFact]
    public async Task Fully_applied_second_apply_is_a_successful_read_only_no_op()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync("e002noop");
        const string variable = "PAQUETERIA_E002_NOOP_TEST_DB";
        var previous = Environment.GetEnvironmentVariable(variable);
        var output = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable(variable, connectionString);
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(
                ["apply", "--connection-env", variable, "--confirm-initial-baseline"]));
            Assert.All(await new ModuleMigrationCoordinator().AssertAsync(connectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            var membershipsBefore = await CountMembershipsAsync(connectionString);
            var fingerprintBefore = await CatalogFingerprintAsync(connectionString);

            // E-002 v0.8 §35: the same apply command on a 13/13 APPLIED database is a successful read-only no-op.
            await using var capture = new StringWriter();
            Console.SetOut(capture);
            var exitCode = await DatabaseMigratorProgram.RunAsync(
                ["apply", "--connection-env", variable, "--confirm-initial-baseline"]);
            Console.SetOut(output);

            Assert.Equal(0, exitCode);
            Assert.Contains("E002_APPLIED_PATH_NO_OP modules=13/13 APPLIED bridge_activation=0", capture.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("E002_APPLIED_PATH_MODULE_STATE_UNEXPECTED", capture.ToString(), StringComparison.Ordinal);
            Assert.Contains("Result: AlreadyApplied", capture.ToString(), StringComparison.Ordinal);
            Assert.Equal(membershipsBefore, await CountMembershipsAsync(connectionString));
            Assert.Equal(fingerprintBefore, await CatalogFingerprintAsync(connectionString));
        }
        finally
        {
            Console.SetOut(output);
            Environment.SetEnvironmentVariable(variable, previous);
            await fixture.DropIsolatedDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public void Notifications_bridge_bearing_migration_never_suppresses_the_transaction()
    {
        // E-002 v0.8 §32: static validation, executable acceptance. Bridge-bearing migrations must not opt out of
        // the EF migration transaction (suppressTransaction: true has no authorized use in the Notifications module).
        var root = RepositoryRootLocator.Find();
        var migrations = Directory.GetFiles(
            Path.Combine(root, "src", "Modules", "Notifications"), "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(migrations);
        var suppressed = migrations
            .Where(path => File.ReadAllText(path).Contains("suppressTransaction: true", StringComparison.Ordinal))
            .ToArray();
        Assert.True(suppressed.Length == 0,
            $"E002_TRANSACTION_SUPPRESSED; STOP_FOR_CONTRACT_REVIEW: {string.Join(", ", suppressed.Select(Path.GetFileName))}");
    }

    private static async Task<string> CatalogFingerprintAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT md5(string_agg(line, ';' ORDER BY line)) FROM (
              SELECT format('%s:%s:%s', n.nspname, c.relname, pg_get_userbyid(c.relowner)) AS line
              FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
              UNION ALL
              SELECT format('%s:%s:%s', n.nspname, p.oid::regprocedure::text, pg_get_userbyid(p.proowner))
              FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
              WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
              UNION ALL
              SELECT format('%s:%s', n.nspname, n.nspacl::text) FROM pg_namespace n
              WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
            ) lines
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountMembershipsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_auth_members", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
