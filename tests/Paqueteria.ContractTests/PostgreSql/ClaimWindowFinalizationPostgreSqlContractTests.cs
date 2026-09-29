using Npgsql;
using Orders.Application.Lifecycle;
using Orders.Infrastructure.Lifecycle;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// LIF-001 / ADR-034 against real PostgreSQL. The function is deliberately global, so every test
/// first drains leftovers from other tests; the collection runs sequentially, which keeps the
/// counts that follow exact.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class ClaimWindowFinalizationPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Function = "security.finalize_expired_orders(integer)";
    private const string Executor = "paqueteria_lifecycle_executor";

    [PostgreSqlContractFact]
    public async Task Only_strictly_expired_unfinalized_closed_orders_are_finalized()
    {
        await DrainAsync();
        await using var expired = await OrderAsync("CLOSED", "-1 hour");
        await using var future = await OrderAsync("CLOSED", "+1 hour");
        await using var missingWindow = await OrderAsync("CLOSED", null);
        await using var alreadyFinalized = await OrderAsync("CLOSED", "-2 hours", finalizedAgo: "-90 minutes");
        await using var claimOpen = await OrderAsync("CLAIM_OPEN", "-1 hour");
        await using var claimResolved = await OrderAsync("CLAIM_RESOLVED", "-1 hour");
        await using var delivered = await OrderAsync("DELIVERED", "-1 hour");
        await using var cancelled = await OrderAsync("CANCELLED", "-1 hour");
        var untouched = new[] { future, missingWindow, alreadyFinalized, claimOpen, claimResolved, delivered, cancelled };
        var before = new Dictionary<Guid, string>();
        foreach (var scenario in untouched.Append(expired))
        {
            before[scenario.OrderId] = await SnapshotAsync(scenario.OrderId, includeFinalizedAt: !ReferenceEquals(scenario, expired));
        }

        Assert.Equal(1, await FinalizeAsWorkerAsync(100));
        Assert.Equal(0, await FinalizeAsWorkerAsync(100));

        foreach (var scenario in untouched)
        {
            Assert.Equal(before[scenario.OrderId], await SnapshotAsync(scenario.OrderId, includeFinalizedAt: true));
        }

        Assert.Equal(before[expired.OrderId], await SnapshotAsync(expired.OrderId, includeFinalizedAt: false));
        var (status, finalizedAt, windowEndsAt, archivedAt) = await LifecycleAsync(expired.OrderId);
        Assert.Equal("CLOSED", status);
        Assert.NotNull(finalizedAt);
        Assert.True(finalizedAt > windowEndsAt, "finalized_at must be strictly after claim_window_ends_at");
        Assert.Null(archivedAt);
    }

    [PostgreSqlContractFact]
    public async Task Finalization_never_touches_order_events_proofs_outbox_audit_or_economics()
    {
        await DrainAsync();
        await using var scenario = await OrderAsync("CLOSED", "-1 hour");
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO orders.order_events(order_id,owner_org_id,aggregate_version,event_type,payload,actor_id)
            VALUES (@order,@org,1,'SYNTHETIC_LIF001_BASELINE','{}',@user);
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            VALUES (@upload,@order,@org,@user,@quarantine,'image/jpeg',1024,'READY',clock_timestamp()+interval '1 day');
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,captured_at,created_by)
            VALUES (gen_random_uuid(),@order,@org,@upload,'DELIVERY_PHOTO',@object_key,
              decode(repeat('02',32),'hex'),'image/jpeg',100,clock_timestamp(),@user);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("upload", Guid.NewGuid()),
            SyntheticOrderScenario.P("quarantine", $"quarantine/lif001/{Guid.NewGuid():N}"),
            SyntheticOrderScenario.P("object_key", $"proofs/lif001/{Guid.NewGuid():N}"));
        var evidenceBefore = await EvidenceFingerprintAsync(scenario);
        var rowBefore = await SnapshotAsync(scenario.OrderId, includeFinalizedAt: false);

        Assert.Equal(1, await FinalizeAsWorkerAsync(10));

        Assert.Equal(evidenceBefore, await EvidenceFingerprintAsync(scenario));
        Assert.Equal(rowBefore, await SnapshotAsync(scenario.OrderId, includeFinalizedAt: false));
    }

    [PostgreSqlContractFact]
    public async Task Batches_are_bounded_and_follow_window_then_id_order()
    {
        await DrainAsync();
        await using var scenario = await OrderAsync("CLOSED", "-10 hours");
        var ordered = await CloneExpiredOrdersAsync(scenario, 4);
        ordered.Insert(0, scenario.OrderId);

        var progression = new List<int>();
        var finalizedOrder = new List<Guid>();
        for (var call = 0; call < 3; call++)
        {
            progression.Add(await FinalizeAsWorkerAsync(2));
        }

        Assert.Equal([2, 2, 1], progression);
        Assert.Equal(0, await FinalizeAsWorkerAsync(2));

        // Each call stamps its whole batch with one instant, so finalized_at groups the batches.
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT id FROM orders.orders WHERE owner_org_id=@org
            ORDER BY finalized_at, claim_window_ends_at, id
            """);
        command.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                finalizedOrder.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal(ordered, finalizedOrder);
        Assert.Equal(3, await ScalarAsync<long>(
            "SELECT count(DISTINCT finalized_at) FROM orders.orders WHERE owner_org_id=@org",
            ("org", scenario.OrganizationId)));
    }

    [PostgreSqlContractFact]
    public async Task Batch_size_outside_the_contract_is_rejected_without_effects()
    {
        await DrainAsync();
        await using var scenario = await OrderAsync("CLOSED", "-1 hour");
        foreach (var batchSize in new int?[] { null, 0, -1, AddOrderLifecycleFinalizationExecutor.MaximumBatchSize + 1 })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => FinalizeAsWorkerAsync(batchSize));
            Assert.Equal("22023", exception.SqlState);
            Assert.Equal("LIF001_BATCH_SIZE_OUT_OF_RANGE", exception.MessageText);
        }

        Assert.Null((await LifecycleAsync(scenario.OrderId)).FinalizedAt);
        Assert.Equal(1, await FinalizeAsWorkerAsync(AddOrderLifecycleFinalizationExecutor.MaximumBatchSize));
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_workers_finalize_every_order_exactly_once_and_reruns_are_no_ops()
    {
        await DrainAsync();
        await using var scenario = await OrderAsync("CLOSED", "-3 hours");
        await CloneExpiredOrdersAsync(scenario, 59);
        var policy = new ClaimWindowFinalizationPolicy(batchSize: 4, maxBatchesPerCycle: ClaimWindowFinalizationPolicy.MaximumBatchesPerCycle);
        var workerA = new ClaimWindowFinalizationCycle(new PostgreSqlExpiredClaimWindowFinalizer(fixture.WorkerDataSource));
        var workerB = new ClaimWindowFinalizationCycle(new PostgreSqlExpiredClaimWindowFinalizer(fixture.WorkerDataSource));

        var results = await Task.WhenAll(
            Task.Run(() => workerA.RunAsync(policy, CancellationToken.None)),
            Task.Run(() => workerB.RunAsync(policy, CancellationToken.None)));

        Assert.All(results, result => Assert.True(result.Drained));
        Assert.Equal(60, results.Sum(result => result.Finalized));
        var stamped = await ScalarAsync<string>(
            "SELECT string_agg(id::text || '=' || finalized_at::text, ',' ORDER BY id) FROM orders.orders WHERE owner_org_id=@org",
            ("org", scenario.OrganizationId));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM orders.orders WHERE owner_org_id=@org AND (finalized_at IS NULL OR status<>'CLOSED')",
            ("org", scenario.OrganizationId)));

        // Restart and repeat: a second pair of cycles and a direct call change nothing.
        var rerun = await Task.WhenAll(
            workerA.RunAsync(policy, CancellationToken.None),
            workerB.RunAsync(policy, CancellationToken.None));
        Assert.All(rerun, result => Assert.Equal(new ClaimWindowFinalizationCycleResult(1, 0, true), result));
        Assert.Equal(0, await FinalizeAsWorkerAsync(100));
        Assert.Equal(stamped, await ScalarAsync<string>(
            "SELECT string_agg(id::text || '=' || finalized_at::text, ',' ORDER BY id) FROM orders.orders WHERE owner_org_id=@org",
            ("org", scenario.OrganizationId)));
    }

    [PostgreSqlContractFact]
    public async Task A_row_locked_by_a_claim_transition_is_skipped_not_waited_on()
    {
        await DrainAsync();
        await using var scenario = await OrderAsync("CLOSED", "-1 hour");
        await using var holder = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand(
            "SELECT 1 FROM orders.orders WHERE id=@order FOR UPDATE", holder, transaction))
        {
            lockRow.Parameters.AddWithValue("order", scenario.OrderId);
            await lockRow.ExecuteScalarAsync();
        }

        var skipped = await FinalizeAsWorkerAsync(10).WaitAsync(TimeSpan.FromSeconds(10));
        await transaction.RollbackAsync();

        Assert.Equal(0, skipped);
        Assert.Equal(1, await FinalizeAsWorkerAsync(10));
    }

    [PostgreSqlContractFact]
    public async Task Executor_role_and_function_catalog_match_adr_034_exactly()
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

        Assert.Equal(Function, await ScalarAsync<string>(
            "SELECT string_agg(oid::regprocedure::text, ',') FROM pg_proc WHERE proowner=@executor::regrole",
            ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT (SELECT count(*) FROM pg_class WHERE relowner=@executor::regrole)
                 + (SELECT count(*) FROM pg_namespace WHERE nspowner=@executor::regrole)
                 + (SELECT count(*) FROM pg_type WHERE typowner=@executor::regrole)
            """,
            ("executor", Executor)));
        Assert.Equal(
            "orders.orders.claim_window_ends_at:SELECT,orders.orders.finalized_at:SELECT,orders.orders.id:SELECT,orders.orders.status:SELECT,orders.orders.finalized_at:UPDATE",
            await ScalarAsync<string>(
                """
                SELECT string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type, ','
                  ORDER BY privilege_type, column_name)
                FROM information_schema.column_privileges WHERE grantee=@executor
                """,
                ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.table_privileges WHERE grantee=@executor",
            ("executor", Executor)));
        foreach (var table in new[]
                 {
                     "orders.order_events", "custody.proofs", "incidents.incidents", "finance.settlements",
                     "finance.cod_transactions", "platform.outbox_events", "platform.location_outbox_events",
                     "platform.audit_logs", "orders.order_acceptances",
                 })
        {
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@executor,@table,'SELECT') OR has_table_privilege(@executor,@table,'INSERT')
                    OR has_table_privilege(@executor,@table,'UPDATE') OR has_table_privilege(@executor,@table,'DELETE')
                """,
                ("executor", Executor), ("table", table)));
        }

        Assert.False(await ScalarAsync<bool>(
            "SELECT has_table_privilege(@executor,'orders.orders','INSERT') OR has_table_privilege(@executor,'orders.orders','DELETE')",
            ("executor", Executor)));
        Assert.Equal("orders", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',') FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'USAGE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));

        Assert.Equal(
            "t|search_path=pg_catalog, orders, pg_temp|f|f|t",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',p.prosecdef,array_to_string(p.proconfig,';'),
                  has_function_privilege('public',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'))
                FROM pg_proc p WHERE p.oid=@function::regprocedure
                """,
                ("function", Function)));

        // ADR-024 boundary, structurally: both predicates compare against the one captured instant
        // with a strict '<'; the function accepts no time source a caller could shift.
        var definition = await ScalarAsync<string>(
            "SELECT pg_get_functiondef(@function::regprocedure)", ("function", Function));
        Assert.Equal(2, CountOccurrences(definition, "o.claim_window_ends_at < v_now"));
        Assert.DoesNotContain("<= v_now", definition, StringComparison.Ordinal);
        Assert.Contains("v_now timestamptz := pg_catalog.clock_timestamp();", definition, StringComparison.Ordinal);
        Assert.Contains("ORDER BY o.claim_window_ends_at, o.id", definition, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE SKIP LOCKED", definition, StringComparison.Ordinal);
        Assert.Contains("SET finalized_at = v_now", definition, StringComparison.Ordinal);
        Assert.Equal("p_batch_size integer", await ScalarAsync<string>(
            "SELECT pg_get_function_arguments(@function::regprocedure)", ("function", Function)));
    }

    [PostgreSqlContractFact]
    public async Task Runtime_credentials_reach_the_function_only_as_the_worker_role()
    {
        await DrainAsync();
        await using (var app = await fixture.AppDataSource.OpenConnectionAsync())
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ExecuteAsync(app, transaction, "SET LOCAL ROLE paqueteria_app;");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(app, transaction, "SELECT security.finalize_expired_orders(1);"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        {
            await using (var noRole = await worker.BeginTransactionAsync())
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(worker, noRole, "SELECT security.finalize_expired_orders(1);"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }

            await using (var escalate = await worker.BeginTransactionAsync())
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(worker, escalate, "SET LOCAL ROLE paqueteria_lifecycle_executor;"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }

        // Without the function the Worker still cannot see, let alone finalize, a foreign tenant.
        await using var scenario = await OrderAsync("CLOSED", "-1 hour");
        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        {
            await using var transaction = await worker.BeginTransactionAsync();
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker;");
            await using var update = new NpgsqlCommand(
                "UPDATE orders.orders SET finalized_at=clock_timestamp() WHERE id=@order", worker, transaction);
            update.Parameters.AddWithValue("order", scenario.OrderId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
            await transaction.RollbackAsync();
        }

        Assert.Null((await LifecycleAsync(scenario.OrderId)).FinalizedAt);
    }

    [PostgreSqlContractFact]
    public async Task Baseline_assertions_accept_the_contract_and_fail_closed_on_widened_privileges()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);

        foreach (var widening in new[]
                 {
                     "GRANT SELECT ON orders.order_events TO paqueteria_lifecycle_executor;",
                     "GRANT UPDATE (status) ON orders.orders TO paqueteria_lifecycle_executor;",
                     "GRANT USAGE ON SCHEMA platform TO paqueteria_lifecycle_executor;",
                     "GRANT EXECUTE ON FUNCTION security.finalize_expired_orders(integer) TO paqueteria_app;",
                     "GRANT paqueteria_lifecycle_executor TO paqueteria_worker;",
                     "ALTER FUNCTION security.finalize_expired_orders(integer) SET search_path = public, pg_temp;",
                     """
                     CREATE FUNCTION security.lif001_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                     ALTER FUNCTION security.lif001_rogue() OWNER TO paqueteria_lifecycle_executor;
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
    }

    [PostgreSqlContractFact]
    public async Task Migration_reapplies_idempotently_and_refuses_contract_violations_before_installing()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddOrderLifecycleFinalizationExecutor.UpSql);
            await reapply.RollbackAsync();
        }

        foreach (var (violation, message) in new[]
                 {
                     ("""
                      CREATE FUNCTION orders.lif001_touch() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END';
                      CREATE TRIGGER lif001_touch BEFORE UPDATE ON orders.orders FOR EACH ROW EXECUTE FUNCTION orders.lif001_touch();
                      """, "LIF-001 refuses orders.orders with user triggers"),
                     ("ALTER ROLE paqueteria_lifecycle_executor LOGIN;", "exists with attributes outside the ADR-034 contract"),
                     ("GRANT paqueteria_worker TO paqueteria_lifecycle_executor;", "must not inherit any other role"),
                     ("GRANT SELECT ON orders.order_events TO paqueteria_lifecycle_executor;", "privileges differ from the ADR-034 contract"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, violation);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddOrderLifecycleFinalizationExecutor.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await using (var downgrade = await connection.BeginTransactionAsync())
        {
            var blocked = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, downgrade, AddOrderLifecycleFinalizationExecutor.SchemaDowngradeNotSupportedSql));
            Assert.Equal("LIF001_SCHEMA_DOWNGRADE_NOT_SUPPORTED", blocked.MessageText);
            await downgrade.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Populated_pre_lif_installation_upgrades_through_the_orders_lane_without_rewriting_rows()
    {
        await DrainAsync();
        await using var scenario = await OrderAsync("CLOSED", "-1 hour");
        var rowBefore = await SnapshotAsync(scenario.OrderId, includeFinalizedAt: true);
        var coordinator = new ModuleMigrationCoordinator();
        try
        {
            // Roll this database back to what an installation provisioned before AI-18 knew the
            // lifecycle executor looks like: no role, no function, Orders history at RTM-002. The later
            // TRK-002-AUTO-LINK step leaves the history with it and reapplies by adopting its AI-06 shape.
            await ExecuteAdminAsync(
                $"""
                DROP OWNED BY {Executor};
                DROP ROLE {Executor};
                DELETE FROM platform."__ef_migrations_history_orders" WHERE "MigrationId" IN (
                  '{AddOrderLifecycleFinalizationExecutor.MigrationId}','{AddTrackingLinkGenerations.MigrationId}');
                """);
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                Assert.Equal(DatabaseBaselineStatus.Applied, (await new DatabaseBaselineStateDetector().DetectAsync(connection)).Status);
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            var pending = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Orders").Status);
            Assert.All(pending.Where(state => state.Module != "Orders"), state => Assert.Equal("APPLIED", state.Status));

            await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);

            var applied = await coordinator.AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.All(applied, state => Assert.Equal("APPLIED", state.Status));
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            Assert.Equal(rowBefore, await SnapshotAsync(scenario.OrderId, includeFinalizedAt: true));
            Assert.Equal(1, await FinalizeAsWorkerAsync(10));
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

    private async Task<SyntheticOrderScenario> OrderAsync(string status, string? windowOffset, string? finalizedAgo = null)
    {
        var scenario = new SyntheticOrderScenario(fixture);
        try
        {
            await scenario.InitializeAsync(status);
            await scenario.ExecuteAdminAsync(
                """
                UPDATE orders.orders SET
                  claim_window_ends_at = CASE WHEN @window IS NULL THEN NULL
                    ELSE clock_timestamp() + @window::interval END,
                  finalized_at = CASE WHEN @finalized IS NULL THEN NULL
                    ELSE clock_timestamp() + @finalized::interval END
                WHERE id=@order;
                """,
                new NpgsqlParameter<string?>("window", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = windowOffset },
                new NpgsqlParameter<string?>("finalized", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = finalizedAgo },
                SyntheticOrderScenario.P("order", scenario.OrderId));
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    /// <summary>Adds CLOSED orders for the scenario's tenant, one minute apart, oldest window first.</summary>
    private async Task<List<Guid>> CloneExpiredOrdersAsync(SyntheticOrderScenario scenario, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await scenario.ExecuteAdminAsync(
            """
            WITH source AS (
              SELECT o.*, q.request_snapshot_redacted, q.breakdown, q.input_hash, q.expires_at
              FROM orders.orders o JOIN pricing.quotes q ON q.id=o.quote_id WHERE o.id=@order
            ), wanted AS (
              SELECT id, ordinality AS position, gen_random_uuid() AS quote_id
              FROM unnest(@ids::uuid[]) WITH ORDINALITY AS t(id, ordinality)
            ), quotes AS (
              INSERT INTO pricing.quotes(
                id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
                subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
                request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
              SELECT w.quote_id,s.owner_org_id,s.city_id,s.origin_location_id,s.destination_location_id,s.service_type,s.pricing_tier,
                s.consolidated_route,s.subtotal_cents,s.discount_cents,s.tax_cents,s.total_cents,s.minimum_total_cents_snapshot,
                s.currency,s.pricing_policy_version,s.request_snapshot_redacted,s.package_snapshot,s.breakdown,s.input_hash,
                'ACTIVE',s.expires_at
              FROM wanted w CROSS JOIN source s
              RETURNING id
            )
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,cod_expected_cents,version,claim_window_ends_at)
            SELECT w.id,'LIF001-' || replace(w.id::text,'-',''),w.quote_id,s.owner_org_id,s.city_id,s.origin_location_id,
              s.destination_location_id,s.service_type,s.pricing_tier,s.consolidated_route,s.payer_type,'CLOSED',s.subtotal_cents,
              s.discount_cents,s.tax_cents,s.total_cents,s.minimum_total_cents_snapshot,s.currency,s.pricing_policy_version,
              s.package_snapshot,s.cod_expected_cents,s.version,
              s.claim_window_ends_at + make_interval(mins => w.position::integer)
            FROM wanted w CROSS JOIN source s
            WHERE EXISTS (SELECT 1 FROM quotes q WHERE q.id=w.quote_id);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            new NpgsqlParameter<Guid[]>("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { TypedValue = ids });
        return [.. ids];
    }

    private async Task DrainAsync()
    {
        while (await FinalizeAsWorkerAsync(AddOrderLifecycleFinalizationExecutor.MaximumBatchSize) > 0)
        {
        }
    }

    private async Task<int> FinalizeAsWorkerAsync(int? batchSize)
    {
        if (batchSize is { } size && size is >= 1 and <= AddOrderLifecycleFinalizationExecutor.MaximumBatchSize)
        {
            return await new PostgreSqlExpiredClaimWindowFinalizer(fixture.WorkerDataSource)
                .FinalizeExpiredBatchAsync(size, CancellationToken.None);
        }

        // Out-of-contract sizes bypass the C# port to prove the function itself refuses them.
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker;");
        await using var command = new NpgsqlCommand("SELECT security.finalize_expired_orders(@size);", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<int?>("size", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = batchSize });
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> SnapshotAsync(Guid orderId, bool includeFinalizedAt) =>
        await ScalarAsync<string>(
            "SELECT (CASE WHEN @with_finalized THEN to_jsonb(o) ELSE to_jsonb(o) - 'finalized_at' END)::text FROM orders.orders o WHERE id=@order",
            ("order", orderId), ("with_finalized", includeFinalizedAt));

    private async Task<(string Status, DateTimeOffset? FinalizedAt, DateTimeOffset? WindowEndsAt, DateTimeOffset? ArchivedAt)> LifecycleAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT status,finalized_at,claim_window_ends_at,archived_at FROM orders.orders WHERE id=@order");
        command.Parameters.AddWithValue("order", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task<string> EvidenceFingerprintAsync(SyntheticOrderScenario scenario) =>
        await ScalarAsync<string>(
            """
            SELECT concat_ws('|',
              (SELECT jsonb_agg(to_jsonb(e) ORDER BY e.id)::text FROM orders.order_events e WHERE e.owner_org_id=@org),
              (SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id)::text FROM custody.proofs p WHERE p.owner_org_id=@org),
              (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org),
              (SELECT count(*) FROM incidents.incidents WHERE owner_org_id=@org))
            """,
            ("org", scenario.OrganizationId));

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
