using Custody.Application.Cleanup;
using Custody.Infrastructure.Cleanup;
using Custody.Infrastructure.Persistence.Migrations;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// OPS-003 / OPS-003-CLEANUP-ROLE against real PostgreSQL. Both functions are deliberately global,
/// so every test first drains leftovers; the collection runs sequentially, which keeps the counts
/// that follow exact. Keys are aged by writing created_at directly, because the 72-hour floor is
/// measured against the function's own clock.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OperationalCleanupPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Executor = AddOperationalCleanupExecutor.ExecutorRole;
    private const string PurgeSignature = AddOperationalCleanupExecutor.IdempotencyPurgeSignature;
    private const string ExpireSignature = AddOperationalCleanupExecutor.SessionExpirySignature;
    private const string Scope = "OPS003:CONTRACT";

    [PostgreSqlContractFact]
    public async Task Executor_role_and_functions_match_the_cleanup_role_contract_exactly()
    {
        Assert.Equal(
            "f|t|f|f|f|f|t",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',rolcanlogin,rolbypassrls,rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolinherit)
                FROM pg_roles WHERE rolname=@executor
                """,
                ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM pg_auth_members WHERE member=@executor::regrole",
            ("executor", Executor)));
        foreach (var runtime in new[] { "paqueteria_app", "paqueteria_worker", PostgreSqlContractFixture.AppLogin, PostgreSqlContractFixture.WorkerLogin })
        {
            Assert.False(await ScalarAsync<bool>(
                "SELECT pg_has_role(@runtime,@executor,'MEMBER') OR pg_has_role(@runtime,@executor,'SET')",
                ("runtime", runtime), ("executor", Executor)));
        }

        Assert.Equal(
            "security.expire_proof_upload_sessions(integer),security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)",
            await ScalarAsync<string>(
                "SELECT string_agg(oid::regprocedure::text, ',' ORDER BY oid::regprocedure::text) FROM pg_proc WHERE proowner=@executor::regrole",
                ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT (SELECT count(*) FROM pg_class WHERE relowner=@executor::regrole)
                 + (SELECT count(*) FROM pg_namespace WHERE nspowner=@executor::regrole)
                 + (SELECT count(*) FROM pg_type WHERE typowner=@executor::regrole)
            """,
            ("executor", Executor)));
        Assert.Equal(
            "custody.proof_upload_sessions.expires_at:SELECT,custody.proof_upload_sessions.id:SELECT," +
            "custody.proof_upload_sessions.status:SELECT,custody.proof_upload_sessions.status:UPDATE," +
            "custody.proof_upload_sessions.updated_at:UPDATE,platform.idempotency_keys.created_at:SELECT," +
            "platform.idempotency_keys.expires_at:SELECT,platform.idempotency_keys.idempotency_key:SELECT," +
            "platform.idempotency_keys.owner_org_id:SELECT,platform.idempotency_keys.scope:SELECT",
            await ScalarAsync<string>(
                """
                SELECT string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type, ','
                  ORDER BY table_schema, table_name, column_name, privilege_type)
                FROM information_schema.column_privileges WHERE grantee=@executor
                """,
                ("executor", Executor)));
        Assert.Equal(
            "platform.idempotency_keys:DELETE",
            await ScalarAsync<string>(
                """
                SELECT string_agg(table_schema || '.' || table_name || ':' || privilege_type, ',')
                FROM information_schema.table_privileges WHERE grantee=@executor
                """,
                ("executor", Executor)));
        Assert.False(await ScalarAsync<bool>(
            """
            SELECT has_table_privilege(@executor,'platform.idempotency_keys','INSERT')
                OR has_table_privilege(@executor,'platform.idempotency_keys','UPDATE')
                OR has_column_privilege(@executor,'platform.idempotency_keys','request_hash','SELECT')
                OR has_column_privilege(@executor,'platform.idempotency_keys','response_body','SELECT')
                OR has_table_privilege(@executor,'custody.proof_upload_sessions','INSERT')
                OR has_table_privilege(@executor,'custody.proof_upload_sessions','DELETE')
                OR has_column_privilege(@executor,'custody.proof_upload_sessions','object_key_quarantine','SELECT')
                OR has_column_privilege(@executor,'custody.proof_upload_sessions','owner_org_id','SELECT')
                OR has_column_privilege(@executor,'custody.proof_upload_sessions','expires_at','UPDATE')
            """,
            ("executor", Executor)));
        foreach (var table in new[]
                 {
                     "orders.orders", "orders.order_events", "custody.proofs", "incidents.incidents", "finance.settlements",
                     "platform.outbox_events", "platform.location_outbox_events", "platform.audit_logs", "identity.users",
                 })
        {
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@executor,@table,'SELECT') OR has_table_privilege(@executor,@table,'INSERT')
                    OR has_table_privilege(@executor,@table,'UPDATE') OR has_table_privilege(@executor,@table,'DELETE')
                """,
                ("executor", Executor), ("table", table)));
        }

        Assert.Equal("custody,platform", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'USAGE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'CREATE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));

        foreach (var (signature, searchPath, arguments) in new[]
                 {
                     (PurgeSignature, "search_path=pg_catalog, platform, pg_temp",
                         "p_expired_before timestamp with time zone, p_batch_size integer, p_dry_run boolean"),
                     (ExpireSignature, "search_path=pg_catalog, custody, pg_temp", "p_batch_size integer"),
                 })
        {
            // SECURITY DEFINER, pinned search_path, Worker-only EXECUTE and a scalar count as the only result.
            Assert.Equal(
                $"t|{searchPath}|f|f|t|integer|{arguments}",
                await ScalarAsync<string>(
                    """
                    SELECT concat_ws('|',p.prosecdef,array_to_string(p.proconfig,';'),
                      has_function_privilege('public',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                      pg_get_function_result(p.oid),
                      pg_get_function_arguments(p.oid))
                    FROM pg_proc p WHERE p.oid=@function::regprocedure
                    """,
                    ("function", signature)));
            Assert.Equal(0, await ScalarAsync<long>(
                "SELECT count(*) FROM pg_proc WHERE oid=@function::regprocedure AND (proretset OR prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)')",
                ("function", signature)));
        }

        // The 72-hour floor is written into the function body against its own clock, not taken from a caller.
        var purge = await ScalarAsync<string>("SELECT pg_get_functiondef(@function::regprocedure)", ("function", PurgeSignature));
        Assert.Contains("v_now timestamptz := pg_catalog.clock_timestamp();", purge, StringComparison.Ordinal);
        Assert.Contains("v_floor timestamptz := v_now - interval '72 hours';", purge, StringComparison.Ordinal);
        Assert.Contains("v_cutoff := LEAST(p_expired_before, v_now);", purge, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(purge, "AND k.created_at < v_floor"));
        Assert.Equal(3, CountOccurrences(purge, "k.expires_at < v_cutoff"));
        Assert.Equal(TimeSpan.FromHours(72), AddOperationalCleanupExecutor.IdempotencyFloor);
    }

    [PostgreSqlContractFact]
    public async Task Runtime_credentials_reach_the_functions_only_as_the_worker_role()
    {
        foreach (var call in new[]
                 {
                     "SELECT security.purge_expired_idempotency_keys(clock_timestamp(), 1, true);",
                     "SELECT security.expire_proof_upload_sessions(1);",
                 })
        {
            await using (var app = await fixture.AppDataSource.OpenConnectionAsync())
            {
                await using var transaction = await app.BeginTransactionAsync();
                await ExecuteAsync(app, transaction, "SET LOCAL ROLE paqueteria_app;");
                var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, transaction, call));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }

            await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
            {
                await using var noRole = await worker.BeginTransactionAsync();
                var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(worker, noRole, call));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }

        foreach (var source in new[] { fixture.AppDataSource, fixture.WorkerDataSource })
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var escalate = await connection.BeginTransactionAsync();
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, escalate, $"SET LOCAL ROLE {Executor};"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task The_72_hour_floor_cannot_be_bypassed_by_any_cutoff()
    {
        await DrainKeysAsync();
        await using var tenant = await ScenarioAsync();
        var old = await KeyAsync(tenant, created: "-73 hours", expires: "-1 hour");
        var justPastFloor = await KeyAsync(tenant, created: "-72 hours -1 minute", expires: "-1 minute");
        var justInsideFloor = await KeyAsync(tenant, created: "-71 hours -59 minutes", expires: "-1 hour");
        var fresh = await KeyAsync(tenant, created: "-1 minute", expires: "-30 seconds");
        var oldButLive = await KeyAsync(tenant, created: "-100 hours", expires: "+1 hour");

        // No cutoff moves the floor or reaches a key that has not expired, in either mode.
        foreach (var cutoff in new[] { "infinity", "+3650 days", "+73 hours", "+1 second" })
        {
            Assert.Equal(2, await PurgeAsWorkerAsync(cutoff, 5_000, dryRun: true));
        }

        Assert.Equal(0, await PurgeAsWorkerAsync("-infinity", 5_000, dryRun: false));
        Assert.Equal(2, await PurgeAsWorkerAsync("infinity", 5_000, dryRun: false));
        Assert.Equal(0, await PurgeAsWorkerAsync("infinity", 5_000, dryRun: false));

        Assert.Equal(
            new[] { fresh, justInsideFloor, oldButLive }.Order(StringComparer.Ordinal),
            await RemainingKeysAsync(tenant));
        Assert.DoesNotContain(old, await RemainingKeysAsync(tenant));
        Assert.DoesNotContain(justPastFloor, await RemainingKeysAsync(tenant));

        // A cutoff before a key's expiry keeps it even when the key is older than the floor.
        var laterExpiry = await KeyAsync(tenant, created: "-90 hours", expires: "-2 hours");
        Assert.Equal(0, await PurgeAsWorkerAsync("-3 hours", 5_000, dryRun: false));
        Assert.Contains(laterExpiry, await RemainingKeysAsync(tenant));
        Assert.Equal(1, await PurgeAsWorkerAsync("-1 hour", 5_000, dryRun: false));
    }

    [PostgreSqlContractFact]
    public async Task Dry_run_counts_a_bounded_batch_and_mutates_nothing()
    {
        await DrainKeysAsync();
        await using var tenant = await ScenarioAsync();
        for (var index = 0; index < 5; index++)
        {
            await KeyAsync(tenant, created: "-80 hours", expires: "-1 hour");
        }

        var before = await KeyFingerprintAsync(tenant);
        Assert.Equal(3, await PurgeAsWorkerAsync("infinity", 3, dryRun: true));
        Assert.Equal(5, await PurgeAsWorkerAsync("infinity", 5_000, dryRun: true));
        Assert.Equal(before, await KeyFingerprintAsync(tenant));
    }

    [PostgreSqlContractFact]
    public async Task Key_batches_are_bounded_and_reruns_are_idempotent()
    {
        await DrainKeysAsync();
        await using var tenant = await ScenarioAsync();
        for (var index = 0; index < 5; index++)
        {
            await KeyAsync(tenant, created: "-80 hours", expires: $"-{index + 1} hours");
        }

        var progression = new List<int>();
        for (var call = 0; call < 4; call++)
        {
            progression.Add(await PurgeAsWorkerAsync("infinity", 2, dryRun: false));
        }

        Assert.Equal([2, 2, 1, 0], progression);
        Assert.Empty(await RemainingKeysAsync(tenant));

        // Through the C# port and the bounded cycle: a restart finds nothing left to do.
        var gateway = new PostgreSqlOperationalCleanupGateway(fixture.WorkerDataSource, 30);
        var rerun = await OperationalCleanupCycle.RunAsync(
            new OperationalCleanupPolicy(2, OperationalCleanupLimits.MaximumIdempotencyBatchSize, 10, dryRun: false),
            (size, token) => gateway.PurgeExpiredIdempotencyKeysAsync(DateTimeOffset.UtcNow, size, dryRun: false, token),
            CancellationToken.None);
        Assert.Equal(new OperationalCleanupCycleResult(1, 0, true), rerun);
    }

    [PostgreSqlContractFact]
    public async Task Arguments_outside_the_contract_are_rejected_without_effects()
    {
        await DrainKeysAsync();
        await DrainSessionsAsync();
        await using var tenant = await ScenarioAsync();
        var key = await KeyAsync(tenant, created: "-80 hours", expires: "-1 hour");
        var session = await SessionAsync(tenant, "CREATED", "-1 minute");

        foreach (var (cutoff, batch, dryRun) in new (string?, int?, bool?)[]
                 {
                     (null, 10, false), ("infinity", null, false), ("infinity", 10, null),
                     ("infinity", 0, false), ("infinity", -1, false),
                     ("infinity", AddOperationalCleanupExecutor.MaximumIdempotencyBatchSize + 1, false),
                 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => PurgeAsWorkerAsync(cutoff, batch, dryRun));
            Assert.Equal("22023", exception.SqlState);
            Assert.Equal("OPS003_ARGUMENT_OUT_OF_RANGE", exception.MessageText);
        }

        foreach (var batch in new int?[] { null, 0, -1, AddOperationalCleanupExecutor.MaximumSessionBatchSize + 1 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExpireAsWorkerAsync(batch));
            Assert.Equal("22023", exception.SqlState);
            Assert.Equal("OPS003_BATCH_SIZE_OUT_OF_RANGE", exception.MessageText);
        }

        Assert.Contains(key, await RemainingKeysAsync(tenant));
        Assert.Equal("CREATED", await SessionStatusAsync(session));
        Assert.Equal(1, await PurgeAsWorkerAsync("infinity", AddOperationalCleanupExecutor.MaximumIdempotencyBatchSize, dryRun: false));
        Assert.Equal(1, await ExpireAsWorkerAsync(AddOperationalCleanupExecutor.MaximumSessionBatchSize));
    }

    [PostgreSqlContractFact]
    public async Task Only_expired_non_terminal_sessions_become_expired_and_nothing_else_changes()
    {
        await DrainSessionsAsync();
        await using var tenant = await ScenarioAsync();
        var expiring = new List<Guid>();
        foreach (var status in new[] { "CREATED", "UPLOADED", "VALIDATING", "READY" })
        {
            expiring.Add(await SessionAsync(tenant, status, "-1 minute"));
        }

        var untouched = new List<Guid>
        {
            await SessionAsync(tenant, "READY", "+10 minutes"),
            await SessionAsync(tenant, "CREATED", "+1 minute"),
            await SessionAsync(tenant, "REJECTED", "-1 hour"),
            await SessionAsync(tenant, "CONSUMED", "-1 hour"),
            await SessionAsync(tenant, "EXPIRED", "-1 hour"),
        };
        var untouchedBefore = await SessionFingerprintAsync(untouched);
        var expiringBefore = await SessionFingerprintAsync(expiring, excludeLifecycle: true);

        Assert.Equal(4, await ExpireAsWorkerAsync(100));
        Assert.Equal(0, await ExpireAsWorkerAsync(100));

        foreach (var id in expiring)
        {
            Assert.Equal("EXPIRED", await SessionStatusAsync(id));
        }

        Assert.Equal(untouchedBefore, await SessionFingerprintAsync(untouched));
        Assert.Equal(expiringBefore, await SessionFingerprintAsync(expiring, excludeLifecycle: true));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(DISTINCT updated_at) FROM custody.proof_upload_sessions WHERE id=ANY(@ids)",
            ("ids", expiring.ToArray())));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM custody.proofs WHERE owner_org_id=@org", ("org", tenant.OrganizationId)));
    }

    [PostgreSqlContractFact]
    public async Task Session_batches_are_bounded_locked_rows_are_skipped_and_reruns_are_no_ops()
    {
        await DrainSessionsAsync();
        await using var tenant = await ScenarioAsync();
        var sessions = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            sessions.Add(await SessionAsync(tenant, "CREATED", $"-{index + 1} minutes"));
        }

        await using (var holder = await fixture.AdminDataSource.OpenConnectionAsync())
        {
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var lockRow = new NpgsqlCommand(
                "SELECT 1 FROM custody.proof_upload_sessions WHERE id=@id FOR UPDATE", holder, transaction))
            {
                lockRow.Parameters.AddWithValue("id", sessions[0]);
                await lockRow.ExecuteScalarAsync();
            }

            Assert.Equal(2, await ExpireAsWorkerAsync(2).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, await ExpireAsWorkerAsync(2).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, await ExpireAsWorkerAsync(2).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("CREATED", await SessionStatusAsync(sessions[0]));
            await transaction.RollbackAsync();
        }

        Assert.Equal(1, await ExpireAsWorkerAsync(2));

        // Two Worker replicas through the C# port: every session exactly once, then nothing.
        for (var index = 0; index < 40; index++)
        {
            await SessionAsync(tenant, "UPLOADED", "-5 minutes");
        }

        var policy = new OperationalCleanupPolicy(3, OperationalCleanupLimits.MaximumSessionBatchSize, 100, dryRun: false);
        var gateway = new PostgreSqlOperationalCleanupGateway(fixture.WorkerDataSource, 30);
        var results = await Task.WhenAll(
            Task.Run(() => OperationalCleanupCycle.RunAsync(policy, gateway.ExpireProofUploadSessionsAsync, CancellationToken.None)),
            Task.Run(() => OperationalCleanupCycle.RunAsync(policy, gateway.ExpireProofUploadSessionsAsync, CancellationToken.None)));
        Assert.All(results, result => Assert.True(result.Drained));
        Assert.Equal(40, results.Sum(result => result.Affected));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM custody.proof_upload_sessions WHERE owner_org_id=@org AND status<>'EXPIRED'",
            ("org", tenant.OrganizationId)));
    }

    [PostgreSqlContractFact]
    public async Task Cleanup_crosses_tenants_but_returns_only_a_count_and_never_widens_runtime_visibility()
    {
        await DrainKeysAsync();
        await DrainSessionsAsync();
        await using var tenantA = await ScenarioAsync();
        await using var tenantB = await ScenarioAsync();
        await KeyAsync(tenantA, created: "-80 hours", expires: "-1 hour");
        await KeyAsync(tenantB, created: "-80 hours", expires: "-1 hour");
        var liveB = await KeyAsync(tenantB, created: "-1 hour", expires: "+1 hour");
        await SessionAsync(tenantA, "CREATED", "-1 minute");
        await SessionAsync(tenantB, "CREATED", "-1 minute");

        // Called from inside tenant A's context, the result is one integer: no row, key, tenant or
        // session identifier from either tenant is ever returned.
        await using (var worker = await TenantTransaction.BeginAsync(
            fixture.WorkerDataSource, "paqueteria_worker", tenantA.UserId, [tenantA.OrganizationId]))
        {
            foreach (var (call, expected) in new[]
                     {
                         ("SELECT * FROM security.purge_expired_idempotency_keys('infinity'::timestamptz, 100, false)", 2),
                         ("SELECT * FROM security.expire_proof_upload_sessions(100)", 2),
                     })
            {
                await using var command = new NpgsqlCommand(call, worker.Connection, worker.Transaction);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.Equal(1, reader.FieldCount);
                Assert.Equal(typeof(int), reader.GetFieldType(0));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(expected, reader.GetInt32(0));
                Assert.False(await reader.ReadAsync());
            }

            // Tenant A still cannot see tenant B's surviving key or its sessions through RLS.
            await using var probe = new NpgsqlCommand(
                """
                SELECT (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@b)
                     + (SELECT count(*) FROM custody.proof_upload_sessions WHERE owner_org_id=@b)
                """,
                worker.Connection,
                worker.Transaction);
            probe.Parameters.AddWithValue("b", tenantB.OrganizationId);
            Assert.Equal(0L, await probe.ExecuteScalarAsync());
            await worker.CommitAsync();
        }

        Assert.Equal([liveB], await RemainingKeysAsync(tenantB));
        Assert.Empty(await RemainingKeysAsync(tenantA));

        // Without the functions the Worker cannot delete or expire anything of its own accord.
        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        {
            await using var transaction = await worker.BeginTransactionAsync();
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker;");
            await using var delete = new NpgsqlCommand(
                "DELETE FROM platform.idempotency_keys WHERE owner_org_id=@b", worker, transaction);
            delete.Parameters.AddWithValue("b", tenantB.OrganizationId);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
            await transaction.RollbackAsync();
        }

        Assert.Equal([liveB], await RemainingKeysAsync(tenantB));
    }

    [PostgreSqlContractFact]
    public async Task Baseline_assertions_accept_the_contract_and_fail_closed_on_widened_privileges()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);

        foreach (var widening in new[]
                 {
                     $"GRANT SELECT ON orders.orders TO {Executor};",
                     $"GRANT SELECT (request_hash) ON platform.idempotency_keys TO {Executor};",
                     $"GRANT INSERT ON platform.idempotency_keys TO {Executor};",
                     $"GRANT UPDATE (expires_at) ON custody.proof_upload_sessions TO {Executor};",
                     $"GRANT DELETE ON custody.proof_upload_sessions TO {Executor};",
                     $"GRANT USAGE ON SCHEMA orders TO {Executor};",
                     $"GRANT CREATE ON SCHEMA platform TO {Executor};",
                     $"REVOKE DELETE ON platform.idempotency_keys FROM {Executor};",
                     "GRANT EXECUTE ON FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean) TO paqueteria_app;",
                     "GRANT EXECUTE ON FUNCTION security.expire_proof_upload_sessions(integer) TO PUBLIC;",
                     "REVOKE EXECUTE ON FUNCTION security.expire_proof_upload_sessions(integer) FROM paqueteria_worker;",
                     $"GRANT {Executor} TO paqueteria_worker;",
                     $"GRANT paqueteria_worker TO {Executor};",
                     "ALTER FUNCTION security.purge_expired_idempotency_keys(timestamptz,integer,boolean) SET search_path = public, pg_temp;",
                     "ALTER FUNCTION security.expire_proof_upload_sessions(integer) SECURITY INVOKER;",
                     "ALTER FUNCTION security.expire_proof_upload_sessions(integer) OWNER TO paqueteria_migrator;",
                     $"""
                     CREATE FUNCTION security.ops003_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                     ALTER FUNCTION security.ops003_rogue() OWNER TO {Executor};
                     """,
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, widening);
            await Assert.ThrowsAsync<DatabaseAssertionException>(() =>
                new DatabaseBaselineAssertions().AssertAsync(connection, transaction));
            await transaction.RollbackAsync();
        }

        await new DatabaseBaselineAssertions().AssertAsync(connection);
        await using var installed = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertCleanupExecutorInstalledAsync(connection, installed);
        await installed.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Migration_reapplies_idempotently_and_refuses_contract_violations_before_installing()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        var definitions = await FunctionDefinitionsAsync(connection, null);
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddOperationalCleanupExecutor.UpSql);
            Assert.Equal(definitions, await FunctionDefinitionsAsync(connection, reapply));
            await reapply.RollbackAsync();
        }

        foreach (var (violation, message) in new[]
                 {
                     ("""
                      CREATE FUNCTION platform.ops003_touch() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN OLD; END';
                      CREATE TRIGGER ops003_touch BEFORE DELETE ON platform.idempotency_keys FOR EACH ROW EXECUTE FUNCTION platform.ops003_touch();
                      """, "OPS-003 refuses cleanup tables with user triggers"),
                     ("ALTER TABLE custody.proof_upload_sessions NO FORCE ROW LEVEL SECURITY;", "requires FORCE ROW LEVEL SECURITY"),
                     ($"ALTER ROLE {Executor} LOGIN;", "exists with attributes outside the OPS-003-CLEANUP-ROLE contract"),
                     ($"ALTER ROLE {Executor} NOBYPASSRLS;", "exists with attributes outside the OPS-003-CLEANUP-ROLE contract"),
                     ($"GRANT paqueteria_worker TO {Executor};", "must not inherit any other role"),
                     ($"GRANT SELECT ON orders.order_events TO {Executor};", "privileges differ from the OPS-003-CLEANUP-ROLE contract"),
                     ($"GRANT USAGE ON SCHEMA orders TO {Executor};", "schema privileges differ from the OPS-003-CLEANUP-ROLE contract"),
                     ($"""
                      CREATE FUNCTION security.ops003_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                      ALTER FUNCTION security.ops003_rogue() OWNER TO {Executor};
                      """, "already owns objects outside the OPS-003-CLEANUP-ROLE contract"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, violation);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddOperationalCleanupExecutor.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await using (var downgrade = await connection.BeginTransactionAsync())
        {
            var blocked = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, downgrade, AddOperationalCleanupExecutor.SchemaDowngradeNotSupportedSql));
            Assert.Equal("OPS003_SCHEMA_DOWNGRADE_NOT_SUPPORTED", blocked.MessageText);
            await downgrade.RollbackAsync();
        }

        await using (var rollback = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, rollback, AddOperationalCleanupExecutor.OperationalRollbackSql);
            await using var check = new NpgsqlCommand(
                """
                SELECT has_function_privilege('paqueteria_worker','security.purge_expired_idempotency_keys(timestamptz,integer,boolean)','EXECUTE')
                    OR has_function_privilege('paqueteria_worker','security.expire_proof_upload_sessions(integer)','EXECUTE')
                """,
                connection,
                rollback);
            Assert.Equal(false, await check.ExecuteScalarAsync());
            await rollback.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Populated_pre_ops003_installation_upgrades_through_the_custody_lane_without_rewriting_rows()
    {
        await DrainKeysAsync();
        await DrainSessionsAsync();
        await using var tenant = await ScenarioAsync();
        var key = await KeyAsync(tenant, created: "-80 hours", expires: "-1 hour");
        var session = await SessionAsync(tenant, "READY", "-1 minute");
        var keysBefore = await KeyFingerprintAsync(tenant);
        var sessionBefore = await SessionFingerprintAsync([session]);
        var coordinator = new ModuleMigrationCoordinator();
        try
        {
            // Roll this database back to an installation provisioned before AI-18 knew the cleanup
            // executor: no role, no functions, Custody history at POD-001.
            await ExecuteAdminAsync(
                $"""
                DROP OWNED BY {Executor};
                DROP ROLE {Executor};
                DELETE FROM platform."__ef_migrations_history_custody" WHERE "MigrationId"='{AddOperationalCleanupExecutor.MigrationId}';
                """);
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                Assert.Equal(DatabaseBaselineStatus.Applied, (await new DatabaseBaselineStateDetector().DetectAsync(connection)).Status);
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            var pending = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Custody").Status);
            Assert.All(pending.Where(state => state.Module != "Custody"), state => Assert.Equal("APPLIED", state.Status));

            await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);

            var applied = await coordinator.AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.All(applied, state => Assert.Equal("APPLIED", state.Status));
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            // The migration itself rewrote nothing; only the jobs change rows.
            Assert.Equal(keysBefore, await KeyFingerprintAsync(tenant));
            Assert.Equal(sessionBefore, await SessionFingerprintAsync([session]));
            Assert.Equal(1, await PurgeAsWorkerAsync("infinity", 10, dryRun: false));
            Assert.DoesNotContain(key, await RemainingKeysAsync(tenant));
            Assert.Equal(1, await ExpireAsWorkerAsync(10));
        }
        finally
        {
            var states = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            if (states.Any(state => state.Status != "APPLIED"))
            {
                await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task E002_semantic_map_includes_the_cleanup_routines_once_the_custody_lane_is_recorded()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        Assert.True(await E002CleanupStateReader.IsAppliedAsync(connection));
        var map = E002RoutineMap.Select(
            E002RoutineMapState.Applied,
            await E002LifecycleStateReader.IsAppliedAsync(connection),
            ops003Applied: true);
        foreach (var signature in AddOperationalCleanupExecutor.OwnedFunctions)
        {
            var entry = Assert.Single(map, routine => routine.Signature == signature);
            Assert.Equal(Executor, entry.Owner);
            Assert.Equal(["paqueteria_worker"], entry.Grantees);
            Assert.Equal(signature, await ScalarAsync<string>(
                "SELECT to_regprocedure(@signature)::regprocedure::text", ("signature", signature)));
        }
    }

    private async Task<SyntheticOrderScenario> ScenarioAsync()
    {
        var scenario = new SyntheticOrderScenario(fixture);
        try
        {
            await scenario.InitializeAsync("AT_PICKUP");
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    private async Task<string> KeyAsync(SyntheticOrderScenario tenant, string created, string expires)
    {
        var key = $"ops003-{Guid.NewGuid():N}";
        await tenant.ExecuteAdminAsync(
            """
            INSERT INTO platform.idempotency_keys(owner_org_id,scope,idempotency_key,request_hash,created_at,expires_at)
            VALUES (@org,@scope,@key,decode('00','hex'),clock_timestamp()+@created::interval,clock_timestamp()+@expires::interval);
            """,
            SyntheticOrderScenario.P("org", tenant.OrganizationId),
            SyntheticOrderScenario.P("scope", Scope),
            SyntheticOrderScenario.P("key", key),
            SyntheticOrderScenario.P("created", created),
            SyntheticOrderScenario.P("expires", expires));
        return key;
    }

    private async Task<Guid> SessionAsync(SyntheticOrderScenario tenant, string status, string expires)
    {
        var id = Guid.NewGuid();
        await tenant.ExecuteAdminAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,expected_content_type,maximum_bytes,
              status,expires_at,created_at,updated_at)
            VALUES (@id,@order,@org,@user,@quarantine,'image/jpeg',1024,@status,
              clock_timestamp()+@expires::interval,clock_timestamp()-interval '2 hours',clock_timestamp()-interval '2 hours');
            """,
            SyntheticOrderScenario.P("id", id),
            SyntheticOrderScenario.P("order", tenant.OrderId),
            SyntheticOrderScenario.P("org", tenant.OrganizationId),
            SyntheticOrderScenario.P("user", tenant.UserId),
            SyntheticOrderScenario.P("quarantine", $"quarantine/ops003/{id:N}"),
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("expires", expires));
        return id;
    }

    private async Task DrainKeysAsync()
    {
        while (await PurgeAsWorkerAsync("infinity", AddOperationalCleanupExecutor.MaximumIdempotencyBatchSize, dryRun: false) > 0)
        {
        }
    }

    private async Task DrainSessionsAsync()
    {
        while (await ExpireAsWorkerAsync(AddOperationalCleanupExecutor.MaximumSessionBatchSize) > 0)
        {
        }
    }

    /// <summary>
    /// Calls the purge as the Worker role. A cutoff is an interval relative to now or a special
    /// timestamp; out-of-contract (null) arguments bypass the C# port to prove the function refuses them.
    /// </summary>
    private async Task<int> PurgeAsWorkerAsync(string? cutoff, int? batchSize, bool? dryRun)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker;");
        await using var command = new NpgsqlCommand(
            """
            SELECT security.purge_expired_idempotency_keys(
              CASE
                WHEN @cutoff IS NULL THEN NULL
                WHEN @cutoff IN ('infinity','-infinity') THEN @cutoff::timestamptz
                ELSE clock_timestamp() + @cutoff::interval
              END,
              @batch,
              @dry_run);
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string?>("cutoff", NpgsqlDbType.Text) { TypedValue = cutoff });
        command.Parameters.Add(new NpgsqlParameter<int?>("batch", NpgsqlDbType.Integer) { TypedValue = batchSize });
        command.Parameters.Add(new NpgsqlParameter<bool?>("dry_run", NpgsqlDbType.Boolean) { TypedValue = dryRun });
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync();
        return result;
    }

    private async Task<int> ExpireAsWorkerAsync(int? batchSize)
    {
        if (batchSize is { } size && size is >= 1 and <= AddOperationalCleanupExecutor.MaximumSessionBatchSize)
        {
            return await new PostgreSqlOperationalCleanupGateway(fixture.WorkerDataSource, 30)
                .ExpireProofUploadSessionsAsync(size, CancellationToken.None);
        }

        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker;");
        await using var command = new NpgsqlCommand("SELECT security.expire_proof_upload_sessions(@size);", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<int?>("size", NpgsqlDbType.Integer) { TypedValue = batchSize });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<string>> RemainingKeysAsync(SyntheticOrderScenario tenant)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT idempotency_key FROM platform.idempotency_keys WHERE owner_org_id=@org AND scope=@scope ORDER BY idempotency_key");
        command.Parameters.AddWithValue("org", tenant.OrganizationId);
        command.Parameters.AddWithValue("scope", Scope);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private async Task<string> KeyFingerprintAsync(SyntheticOrderScenario tenant) =>
        await ScalarAsync<string>(
            "SELECT COALESCE(jsonb_agg(to_jsonb(k) ORDER BY k.idempotency_key)::text,'[]') FROM platform.idempotency_keys k WHERE k.owner_org_id=@org",
            ("org", tenant.OrganizationId));

    private async Task<string> SessionFingerprintAsync(IReadOnlyCollection<Guid> ids, bool excludeLifecycle = false) =>
        await ScalarAsync<string>(
            """
            SELECT jsonb_agg(CASE WHEN @exclude THEN to_jsonb(s) - 'status' - 'updated_at' ELSE to_jsonb(s) END ORDER BY s.id)::text
            FROM custody.proof_upload_sessions s WHERE s.id=ANY(@ids)
            """,
            ("ids", ids.ToArray()), ("exclude", excludeLifecycle));

    private async Task<string> SessionStatusAsync(Guid id) =>
        await ScalarAsync<string>("SELECT status FROM custody.proof_upload_sessions WHERE id=@id", ("id", id));

    private static async Task<string> FunctionDefinitionsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(pg_get_functiondef(p.oid) || pg_get_userbyid(p.proowner) || p.proacl::text, '|' ORDER BY p.proname)
            FROM pg_proc p WHERE p.proowner='paqueteria_cleanup_executor'::regrole
            """,
            connection,
            transaction);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        for (var index = value.IndexOf(fragment, StringComparison.Ordinal);
             index >= 0;
             index = value.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
