using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Lifecycle;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure;
using Orders.Infrastructure.Lifecycle;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence.Migrations;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10 against real PostgreSQL, through the Worker composition (<c>AddOrdersAutoClose</c> with
/// the least-privilege Worker login). Owner discovery is deliberately global, so every test that runs a pass first
/// closes whatever other tests left closable and then asserts only on its own organizations; the collection runs
/// sequentially, which keeps the closed counts that follow exact.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class OrderAutoClosePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Function = AddOrderAutoCloseDiscovery.FunctionSignature;
    private const string Executor = AddOrderAutoCloseDiscovery.ExecutorRole;
    private const string HealthCheckName = "orders_auto_close";

    private static readonly OrderAutoClosePolicy FullPass = new(
        OrderAutoClosePolicy.MaximumBatchSize,
        OrderAutoClosePolicy.MaximumBatchesPerCycle);

    [PostgreSqlContractFact]
    public async Task An_eligible_order_without_COD_closes_with_the_rows_of_a_manual_close_and_no_actor()
    {
        await DrainAsync();
        await using var automatic = await DeliveredAsync();
        await using var manual = await DeliveredAsync();
        var rowBefore = await OrderRowAsync(automatic.OrderId, excludeTransitionColumns: true);

        await using (var api = CreateApiTransitions())
        {
            var closed = await api.Service.TransitionAsync(
                ManualCommand(manual, OrderStatus.Closed, 1),
                CancellationToken.None);
            Assert.Equal("CLOSED", closed.Status);
        }

        await using var worker = CreateWorker();
        var result = await RunPassAsync(worker);

        Assert.Equal(1, result.Closed);
        Assert.Equal(0, result.Failed);
        Assert.True(result.Drained);
        await AssertClosedOnceAsync(automatic);
        Assert.Equal(rowBefore, await OrderRowAsync(automatic.OrderId, excludeTransitionColumns: true));

        // Same order update, event, outbox rows (status and timeline topics), audit and idempotency rows as the
        // manual "Cerrar orden", so SignalR, the dashboard and public tracking see the same thing; only the actor,
        // the reason and the request id differ.
        Assert.Equal(await NormalizedTransitionRowsAsync(manual), await NormalizedTransitionRowsAsync(automatic));
        Assert.Equal(
            $"{manual.UserId:D}|synthetic manual close|synthetic-request-id",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',e.actor_id::text,e.payload->>'reason_redacted',a.request_id)
                FROM orders.order_events e
                JOIN platform.audit_logs a ON a.entity_id=e.order_id AND a.action='ORDER_STATUS_CHANGED'
                WHERE e.order_id=@order
                """,
                ("order", manual.OrderId)));

        // A second pass finds nothing left to do.
        var rerun = await RunPassAsync(worker);
        Assert.Equal(0, rerun.Closed);
        await AssertClosedOnceAsync(automatic);
    }

    [PostgreSqlContractFact]
    public async Task A_COD_order_closes_only_after_its_cash_is_reconciled_for_the_expected_amount()
    {
        await DrainAsync();
        await using var cod = await DeliveredAsync(codExpectedCents: 5_000);
        await InsertCodAsync(cod, "RECORDED", 5_000);
        await using var worker = CreateWorker();
        var attempter = worker.GetRequiredService<IOrderAutoCloseAttempter>();

        Assert.Equal(
            OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.CodNotReconciled),
            await attempter.CloseAsync(Candidate(cod), CancellationToken.None));
        var recorded = await RunPassAsync(worker);
        Assert.Equal(0, recorded.Closed);
        Assert.True(recorded.NotEligibleByRule.GetValueOrDefault(OrderTransitionRejectionCodes.CodNotReconciled) >= 1);
        await AssertUntouchedAsync(cod, OrderStatus.Delivered);

        // RECONCILED for another amount is still not the expected cash.
        await cod.ExecuteAdminAsync(
            "UPDATE finance.cod_transactions SET status='RECONCILED',reconciled_at=clock_timestamp(),amount_cents=4999 WHERE order_id=@order;",
            SyntheticOrderScenario.P("order", cod.OrderId));
        Assert.Equal(
            OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.CodNotReconciled),
            await attempter.CloseAsync(Candidate(cod), CancellationToken.None));
        Assert.Equal(0, (await RunPassAsync(worker)).Closed);
        await AssertUntouchedAsync(cod, OrderStatus.Delivered);

        await cod.ExecuteAdminAsync(
            "UPDATE finance.cod_transactions SET amount_cents=5000 WHERE order_id=@order;",
            SyntheticOrderScenario.P("order", cod.OrderId));
        var reconciled = await RunPassAsync(worker);
        Assert.Equal(1, reconciled.Closed);
        Assert.Equal(0, reconciled.Failed);
        await AssertClosedOnceAsync(cod);
    }

    [PostgreSqlContractFact]
    public async Task Orders_whose_CLOSED_guards_do_not_hold_stay_delivered_with_nothing_written()
    {
        await DrainAsync();
        await using var incident = await DeliveredAsync();
        var incidentId = await InsertIncidentAsync(incident);
        await using var noWindow = await DeliveredAsync(window: null);
        await using var strayCod = await DeliveredAsync();
        await InsertCodAsync(strayCod, "RECORDED", 1_000);
        await using var worker = CreateWorker();
        var attempter = worker.GetRequiredService<IOrderAutoCloseAttempter>();

        foreach (var (scenario, rule) in new[]
                 {
                     (incident, OrderTransitionRejectionCodes.UnresolvedIncident),
                     (noWindow, OrderTransitionRejectionCodes.ClaimWindowNotSet),
                     (strayCod, OrderTransitionRejectionCodes.FinancialReconciliationIncomplete),
                 })
        {
            Assert.Equal(
                OrderAutoCloseAttempt.NotEligible(rule),
                await attempter.CloseAsync(Candidate(scenario), CancellationToken.None));
        }

        var blocked = await RunPassAsync(worker);
        Assert.Equal(0, blocked.Closed);
        Assert.Equal(0, blocked.Failed);
        Assert.True(blocked.NotEligible >= 3);
        foreach (var scenario in new[] { incident, noWindow, strayCod })
        {
            await AssertUntouchedAsync(scenario, OrderStatus.Delivered);
        }

        // Once the incident is resolved the next pass closes that order, and only that one.
        await incident.ExecuteAdminAsync(
            "UPDATE incidents.incidents SET status='RESOLVED',resolved_at=clock_timestamp() WHERE id=@incident;",
            SyntheticOrderScenario.P("incident", incidentId));
        var resolved = await RunPassAsync(worker);
        Assert.Equal(1, resolved.Closed);
        await AssertClosedOnceAsync(incident);
        await AssertUntouchedAsync(noWindow, OrderStatus.Delivered);
        await AssertUntouchedAsync(strayCod, OrderStatus.Delivered);
    }

    [PostgreSqlContractFact]
    public async Task An_order_without_monetary_integrity_stays_delivered()
    {
        // AI-06 CHECK constraints make every storable order pass the monetary-integrity half of
        // financial_reconciliation_complete. To prove the transition still refuses such an order, this test lifts
        // the minimum-total constraint for its own duration, stores an order below its minimum without a financial
        // override, and restores the constraint verbatim (same name and definition) before it ends.
        await DrainAsync();
        await using var scenario = await DeliveredAsync();
        var (name, definition) = await MinimumTotalConstraintAsync();
        await ExecuteAdminAsync($"ALTER TABLE orders.orders DROP CONSTRAINT {name};");
        try
        {
            await scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET minimum_total_cents_snapshot=total_cents+1 WHERE id=@order;",
                SyntheticOrderScenario.P("order", scenario.OrderId));
            var rowBefore = await OrderRowAsync(scenario.OrderId, excludeTransitionColumns: false);
            await using var worker = CreateWorker();

            Assert.Equal(
                OrderAutoCloseAttempt.NotEligible(OrderTransitionRejectionCodes.FinancialReconciliationIncomplete),
                await worker.GetRequiredService<IOrderAutoCloseAttempter>().CloseAsync(
                    Candidate(scenario),
                    CancellationToken.None));
            Assert.Equal(0, (await RunPassAsync(worker)).Closed);
            await AssertUntouchedAsync(scenario, OrderStatus.Delivered);
            Assert.Equal(rowBefore, await OrderRowAsync(scenario.OrderId, excludeTransitionColumns: false));
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET minimum_total_cents_snapshot=LEAST(minimum_total_cents_snapshot,total_cents) WHERE id=@order;",
                SyntheticOrderScenario.P("order", scenario.OrderId));
            await ExecuteAdminAsync($"ALTER TABLE orders.orders ADD CONSTRAINT {name} {definition};");
        }

        Assert.Equal((name, definition), await MinimumTotalConstraintAsync());
    }

    [PostgreSqlContractFact]
    public async Task The_close_runs_in_the_owner_tenant_only_and_leaves_operators_and_other_tenants_untouched()
    {
        await DrainAsync();
        // Declared before the owner: the owner's order and event reference the operator, so the owner goes first.
        await using var operatorOrganization = await DeliveredAsync(window: null);
        await using var foreign = await DeliveredAsync();
        await InsertIncidentAsync(foreign);
        await using var owner = await DeliveredAsync(operatorOrganizationId: operatorOrganization.OrganizationId);
        var operatorBefore = await TenantFingerprintAsync(operatorOrganization);
        var foreignBefore = await TenantFingerprintAsync(foreign);
        await using var worker = CreateWorker();

        // Each organization's pass lists only the DELIVERED orders it owns, never one it merely operates.
        var reader = worker.GetRequiredService<IOrderAutoCloseCandidateReader>();
        Assert.Equal(
            [Candidate(operatorOrganization)],
            await reader.ListDeliveredAsync(operatorOrganization.OrganizationId, null, 10, CancellationToken.None));
        Assert.Equal(
            [Candidate(owner)],
            await reader.ListDeliveredAsync(owner.OrganizationId, null, 10, CancellationToken.None));
        Assert.Empty(await reader.ListDeliveredAsync(owner.OrganizationId, owner.OrderId, 10, CancellationToken.None));

        // Neither the operator nor another tenant can close the owner's order as the system actor.
        await using (var scope = worker.CreateAsyncScope())
        {
            var system = scope.ServiceProvider.GetRequiredService<IOrderSystemTransitionService>();
            foreach (var outsider in new[] { operatorOrganization.OrganizationId, foreign.OrganizationId })
            {
                var hidden = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                    system.CloseDeliveredAsync(
                        new SystemCloseOrderCommand(outsider, owner.OrderId, 1),
                        CancellationToken.None));
                Assert.Equal(OrderTransitionConflictCode.OrderUnavailable, hidden.Code);
            }
        }

        Assert.Equal(
            OrderAutoCloseAttempt.Superseded,
            await worker.GetRequiredService<IOrderAutoCloseAttempter>().CloseAsync(
                new OrderAutoCloseCandidate(operatorOrganization.OrganizationId, owner.OrderId, 1),
                CancellationToken.None));
        await AssertUntouchedAsync(owner, OrderStatus.Delivered);

        var result = await RunPassAsync(worker);

        Assert.Equal(1, result.Closed);
        await AssertClosedOnceAsync(owner, operatorOrganization.OrganizationId);
        Assert.Equal(operatorBefore, await TenantFingerprintAsync(operatorOrganization));
        Assert.Equal(foreignBefore, await TenantFingerprintAsync(foreign));
    }

    [PostgreSqlContractFact]
    public async Task A_failure_at_any_transition_stage_writes_nothing_and_a_later_pass_closes_the_order()
    {
        await DrainAsync();
        foreach (var stage in Enum.GetValues<OrderTransitionStage>())
        {
            await using var scenario = await DeliveredAsync();
            await using (var failing = CreateWorker(new ThrowAtTransitionStage(stage)))
            {
                var injected = await Assert.ThrowsAsync<InjectedTransitionFailure>(() =>
                    failing.GetRequiredService<IOrderAutoCloseAttempter>().CloseAsync(
                        Candidate(scenario),
                        CancellationToken.None));
                Assert.Equal(stage, injected.Stage);

                var failed = await RunPassAsync(failing);
                Assert.True(failed.Failed >= 1, $"the pass must count the failure injected at {stage}");
                Assert.Contains(nameof(InjectedTransitionFailure), failed.FailureTypes);
                Assert.Equal(0, failed.Closed);
            }

            await AssertUntouchedAsync(scenario, OrderStatus.Delivered);
            await using var worker = CreateWorker();
            Assert.Equal(1, (await RunPassAsync(worker)).Closed);
            await AssertClosedOnceAsync(scenario);
        }
    }

    [PostgreSqlContractFact]
    public async Task Two_workers_racing_over_the_same_orders_close_each_exactly_once()
    {
        await DrainAsync();
        const int organizations = 3;
        const int ordersPerOrganization = 8;
        var scenarios = new List<SyntheticOrderScenario>();
        try
        {
            for (var index = 0; index < organizations; index++)
            {
                var scenario = await DeliveredAsync();
                scenarios.Add(scenario);
                await CloneDeliveredOrdersAsync(scenario, ordersPerOrganization - 1);
            }

            await using var workerA = CreateWorker();
            await using var workerB = CreateWorker();
            var cycleA = workerA.GetRequiredService<OrderAutoCloseCycle>();
            var cycleB = workerB.GetRequiredService<OrderAutoCloseCycle>();
            var policy = new OrderAutoClosePolicy(3, OrderAutoClosePolicy.MaximumBatchesPerCycle);

            var results = await Task.WhenAll(
                Task.Run(() => cycleA.RunAsync(policy, CancellationToken.None)),
                Task.Run(() => cycleB.RunAsync(policy, CancellationToken.None)));

            Assert.All(results, result => Assert.True(result.Drained));
            Assert.All(results, result => Assert.Equal(0, result.Failed));
            Assert.Equal(organizations * ordersPerOrganization, results.Sum(result => result.Closed));
            var fingerprints = new List<string>();
            foreach (var scenario in scenarios)
            {
                Assert.Equal(
                    $"{ordersPerOrganization}|{ordersPerOrganization}|{ordersPerOrganization}|{ordersPerOrganization}|{2 * ordersPerOrganization}|{ordersPerOrganization}|{ordersPerOrganization}",
                    await ScalarAsync<string>(
                        """
                        SELECT concat_ws('|',
                          (SELECT count(*) FROM orders.orders WHERE owner_org_id=@org AND status='CLOSED' AND version=2),
                          (SELECT count(*) FROM orders.order_events WHERE owner_org_id=@org
                             AND payload->>'new_status'='CLOSED' AND aggregate_version=2 AND actor_id IS NULL),
                          (SELECT count(DISTINCT order_id) FROM orders.order_events WHERE owner_org_id=@org),
                          (SELECT count(*) FROM orders.order_events WHERE owner_org_id=@org),
                          (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org AND aggregate_version=2),
                          (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ORDER_STATUS_CHANGED'),
                          (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org
                             AND scope='ORD-002:TRANSITION_ORDER' AND response_status=200
                             AND idempotency_key LIKE 'ord-auto-close-%'))
                        """,
                        ("org", scenario.OrganizationId)));
                fingerprints.Add(await TenantFingerprintAsync(scenario));
            }

            // Restart and repeat: both workers again, concurrently, change nothing.
            var rerun = await Task.WhenAll(
                cycleA.RunAsync(policy, CancellationToken.None),
                cycleB.RunAsync(policy, CancellationToken.None));
            Assert.All(rerun, result => Assert.Equal(0, result.Closed));
            for (var index = 0; index < scenarios.Count; index++)
            {
                Assert.Equal(fingerprints[index], await TenantFingerprintAsync(scenarios[index]));
            }
        }
        finally
        {
            foreach (var scenario in scenarios)
            {
                await scenario.DisposeAsync();
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task A_claim_can_still_be_opened_within_the_window_after_an_automatic_close()
    {
        await DrainAsync();
        await using var scenario = await DeliveredAsync(window: "+72 hours");
        await using var worker = CreateWorker();
        Assert.Equal(1, (await RunPassAsync(worker)).Closed);
        await AssertClosedOnceAsync(scenario);

        await using (var api = CreateApiTransitions())
        {
            var claim = await api.Service.TransitionAsync(
                ManualCommand(scenario, OrderStatus.ClaimOpen, 2, "synthetic claim"),
                CancellationToken.None);
            Assert.Equal("CLAIM_OPEN", claim.Status);
            Assert.Equal(3, claim.Version);
            Assert.Null(claim.FinalizedAt);
        }

        // The claimed order is no longer DELIVERED: passes neither list nor touch it, and the system actor can
        // take it nowhere.
        var before = await TenantFingerprintAsync(scenario);
        Assert.Empty(await worker.GetRequiredService<IOrderAutoCloseCandidateReader>()
            .ListDeliveredAsync(scenario.OrganizationId, null, 10, CancellationToken.None));
        Assert.Equal(0, (await RunPassAsync(worker)).Closed);
        await using (var scope = worker.CreateAsyncScope())
        {
            var refused = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.ServiceProvider.GetRequiredService<IOrderSystemTransitionService>().CloseDeliveredAsync(
                    new SystemCloseOrderCommand(scenario.OrganizationId, scenario.OrderId, 3),
                    CancellationToken.None));
            Assert.Equal(OrderTransitionConflictCode.InvalidState, refused.Code);
        }

        Assert.Equal(before, await TenantFingerprintAsync(scenario));
    }

    [PostgreSqlContractFact]
    public async Task Lifecycle_finalization_still_finalizes_an_automatically_closed_order_after_its_window()
    {
        await DrainAsync();
        await using var scenario = await DeliveredAsync(window: "-1 hour");
        await using var worker = CreateWorker();
        Assert.Equal(1, (await RunPassAsync(worker)).Closed);
        await AssertClosedOnceAsync(scenario);
        Assert.Equal("CLOSED|f", await LifecycleAsync(scenario.OrderId));

        var finalizer = new PostgreSqlExpiredClaimWindowFinalizer(fixture.WorkerDataSource);
        while (await finalizer.FinalizeExpiredBatchAsync(
                   AddOrderLifecycleFinalizationExecutor.MaximumBatchSize,
                   CancellationToken.None) > 0)
        {
        }

        Assert.Equal("CLOSED|t", await LifecycleAsync(scenario.OrderId));
        Assert.True(await ScalarAsync<bool>(
            "SELECT finalized_at > claim_window_ends_at FROM orders.orders WHERE id=@order",
            ("order", scenario.OrderId)));
        await using (var api = CreateApiTransitions())
        {
            var refused = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                api.Service.TransitionAsync(
                    ManualCommand(scenario, OrderStatus.ClaimOpen, 2, "synthetic late claim"),
                    CancellationToken.None));
            Assert.Equal(OrderTransitionConflictCode.InvalidState, refused.Code);
        }

        await AssertClosedOnceAsync(scenario);
    }

    [PostgreSqlContractFact]
    public async Task Discovery_lists_each_owner_with_a_delivered_order_once_in_ascending_order_after_the_cursor()
    {
        await using var closedOnly = new SyntheticOrderScenario(fixture);
        await closedOnly.InitializeAsync(OrderStatus.Closed.ToContractValue());
        await using var claimOpen = new SyntheticOrderScenario(fixture);
        await claimOpen.InitializeAsync(OrderStatus.ClaimOpen.ToContractValue());
        await using var twoDelivered = await DeliveredAsync(window: null);
        await CloneDeliveredOrdersAsync(twoDelivered, 1);
        await using var operated = await DeliveredAsync(window: null, operatorOrganizationId: closedOnly.OrganizationId);

        var all = await DiscoverAsWorkerAsync(null, AddOrderAutoCloseDiscovery.MaximumLimit);
        Assert.Equal(all.Distinct().Count(), all.Count);
        Assert.Equal(all.OrderBy(Canonical, StringComparer.Ordinal), all);
        Assert.Contains(twoDelivered.OrganizationId, all);
        Assert.Contains(operated.OrganizationId, all);
        Assert.DoesNotContain(closedOnly.OrganizationId, all);
        Assert.DoesNotContain(claimOpen.OrganizationId, all);

        var cursor = all.IndexOf(twoDelivered.OrganizationId);
        Assert.Equal(
            all.Skip(cursor + 1),
            await DiscoverAsWorkerAsync(twoDelivered.OrganizationId, AddOrderAutoCloseDiscovery.MaximumLimit));
        Assert.Equal(all.Take(1), await DiscoverAsWorkerAsync(null, 1));
        Assert.Equal(all.Skip(cursor + 1).Take(1), await DiscoverAsWorkerAsync(twoDelivered.OrganizationId, 1));

        // The Worker port reads the same answer.
        await using (var worker = CreateWorker())
        {
            Assert.Equal(
                all,
                await worker.GetRequiredService<IOrderAutoCloseOwnerDiscovery>().ListOwnersAsync(
                    null,
                    AddOrderAutoCloseDiscovery.MaximumLimit,
                    CancellationToken.None));
        }

        foreach (var limit in new int?[] { null, 0, -1, AddOrderAutoCloseDiscovery.MaximumLimit + 1 })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => DiscoverAsWorkerAsync(null, limit));
            Assert.Equal("22023", refused.SqlState);
            Assert.Equal("ORD_AUTO_CLOSE_LIMIT_OUT_OF_RANGE", refused.MessageText);
        }
    }

    [PostgreSqlContractFact]
    public async Task Executor_role_and_function_catalog_match_the_contract_exactly()
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
        foreach (var runtime in new[]
                 {
                     "paqueteria_app", "paqueteria_worker", "paqueteria_bootstrap",
                     PostgreSqlContractFixture.AppLogin, PostgreSqlContractFixture.WorkerLogin,
                 })
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
            string.Join(',', AddOrderAutoCloseDiscovery.ExecutorColumnGrants),
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
        Assert.False(await ScalarAsync<bool>(
            """
            SELECT has_table_privilege(@executor,'orders.orders','SELECT')
                OR has_column_privilege(@executor,'orders.orders','id','SELECT')
                OR has_column_privilege(@executor,'orders.orders','public_id','SELECT')
                OR has_column_privilege(@executor,'orders.orders','total_cents','SELECT')
                OR has_table_privilege(@executor,'orders.orders','INSERT')
                OR has_table_privilege(@executor,'orders.orders','UPDATE')
                OR has_table_privilege(@executor,'orders.orders','DELETE')
            """,
            ("executor", Executor)));
        foreach (var table in new[]
                 {
                     "orders.order_events", "orders.order_acceptances", "custody.proofs", "incidents.incidents",
                     "finance.cod_transactions", "platform.outbox_events", "platform.location_outbox_events",
                     "platform.audit_logs", "platform.idempotency_keys", "organizations.organization_memberships",
                     "identity.users",
                 })
        {
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@executor,@table,'SELECT') OR has_table_privilege(@executor,@table,'INSERT')
                    OR has_table_privilege(@executor,@table,'UPDATE') OR has_table_privilege(@executor,@table,'DELETE')
                """,
                ("executor", Executor), ("table", table)));
        }

        Assert.Equal("orders", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',') FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'USAGE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'CREATE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));

        Assert.Equal(
            "t|s|search_path=pg_catalog, orders, pg_temp|f|f|t|f|SETOF uuid|p_after_owner_org_id uuid, p_limit integer",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',p.prosecdef,p.provolatile,array_to_string(p.proconfig,';'),
                  has_function_privilege('public',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_bootstrap',p.oid,'EXECUTE'),
                  pg_get_function_result(p.oid),
                  pg_get_function_arguments(p.oid))
                FROM pg_proc p WHERE p.oid=@function::regprocedure
                """,
                ("function", Function)));

        // Structurally: one read of owner_org_id filtered on DELIVERED, distinct, ascending, after the cursor,
        // bounded; nothing else is selected, nothing is written and no dynamic SQL is possible.
        var definition = await ScalarAsync<string>(
            "SELECT pg_get_functiondef(@function::regprocedure)", ("function", Function));
        Assert.Contains("SELECT DISTINCT o.owner_org_id", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE o.status = 'DELIVERED'", definition, StringComparison.Ordinal);
        Assert.Contains("o.owner_org_id > p_after_owner_org_id", definition, StringComparison.Ordinal);
        Assert.Contains("ORDER BY o.owner_org_id", definition, StringComparison.Ordinal);
        Assert.Contains("LIMIT p_limit", definition, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "INSERT", "UPDATE", "DELETE", "EXECUTE ", "RETURNING", "o.id" })
        {
            Assert.DoesNotContain(forbidden, definition, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgreSqlContractFact]
    public async Task Runtime_credentials_reach_the_function_only_as_the_worker_role_and_rls_still_hides_orders()
    {
        await using var scenario = await DeliveredAsync();
        await using (var app = await fixture.AppDataSource.OpenConnectionAsync())
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ExecuteAsync(app, transaction, "SET LOCAL ROLE paqueteria_app;");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(app, transaction, "SELECT * FROM security.list_auto_close_owner_organizations(NULL,1);"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        {
            await using (var noRole = await worker.BeginTransactionAsync())
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(worker, noRole, "SELECT * FROM security.list_auto_close_owner_organizations(NULL,1);"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }

            await using (var escalate = await worker.BeginTransactionAsync())
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(worker, escalate, $"SET LOCAL ROLE {Executor};"));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }

            // As paqueteria_worker without a tenant context FORCE RLS hides every order (the reason the discovery
            // function exists), and the function itself hands out the owner organization only.
            await using var transaction = await worker.BeginTransactionAsync();
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker;");
            await using (var hidden = new NpgsqlCommand(
                "SELECT count(*) FROM orders.orders WHERE id=@order", worker, transaction))
            {
                hidden.Parameters.AddWithValue("order", scenario.OrderId);
                Assert.Equal(0L, await hidden.ExecuteScalarAsync());
            }

            await using (var discovery = new NpgsqlCommand(
                "SELECT count(*) FROM security.list_auto_close_owner_organizations(NULL,1000) AS owners(id) WHERE owners.id=@org",
                worker,
                transaction))
            {
                discovery.Parameters.AddWithValue("org", scenario.OrganizationId);
                Assert.Equal(1L, await discovery.ExecuteScalarAsync());
            }

            await transaction.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task The_ready_check_reports_whether_the_worker_can_execute_the_discovery_function()
    {
        await using (var disabled = CreateWorker(enabled: false))
        {
            var report = await disabled.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(candidate => candidate.Name == HealthCheckName);
            Assert.Equal(HealthStatus.Healthy, report.Entries[HealthCheckName].Status);
            Assert.Equal("Order auto-close is disabled.", report.Entries[HealthCheckName].Description);
        }

        await using var worker = CreateWorker();
        var ready = await worker.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == HealthCheckName);
        Assert.Equal(HealthStatus.Healthy, ready.Entries[HealthCheckName].Status);

        await ExecuteAdminAsync($"REVOKE EXECUTE ON FUNCTION {Function} FROM paqueteria_worker;");
        try
        {
            var notReady = await worker.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(candidate => candidate.Name == HealthCheckName);
            Assert.Equal(HealthStatus.Unhealthy, notReady.Entries[HealthCheckName].Status);
            Assert.Equal(
                "Order auto-close discovery function is unavailable to the Worker.",
                notReady.Entries[HealthCheckName].Description);
        }
        finally
        {
            await ExecuteAdminAsync($"GRANT EXECUTE ON FUNCTION {Function} TO paqueteria_worker;");
        }

        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertAutoCloseExecutorInstalledAsync(connection, transaction);
        await transaction.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Baseline_assertions_accept_the_contract_and_fail_closed_on_widened_privileges()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);
        await using (var installed = await connection.BeginTransactionAsync())
        {
            await DatabaseBaselineAssertions.AssertAutoCloseExecutorInstalledAsync(connection, installed);
            await installed.RollbackAsync();
        }

        foreach (var widening in new[]
                 {
                     $"GRANT SELECT ON orders.order_events TO {Executor};",
                     $"GRANT SELECT (id) ON orders.orders TO {Executor};",
                     $"GRANT UPDATE (status) ON orders.orders TO {Executor};",
                     $"GRANT SELECT ON orders.orders TO {Executor};",
                     $"REVOKE SELECT (owner_org_id,status) ON orders.orders FROM {Executor}; GRANT SELECT (owner_org_id,status) ON orders.orders TO {Executor} WITH GRANT OPTION;",
                     $"GRANT USAGE ON SCHEMA platform TO {Executor};",
                     $"GRANT CREATE ON SCHEMA orders TO {Executor};",
                     $"GRANT EXECUTE ON FUNCTION {Function} TO paqueteria_app;",
                     $"GRANT EXECUTE ON FUNCTION {Function} TO PUBLIC;",
                     $"GRANT {Executor} TO paqueteria_worker;",
                     $"GRANT paqueteria_worker TO {Executor};",
                     $"ALTER ROLE {Executor} LOGIN;",
                     $"ALTER FUNCTION {Function} SET search_path = public, pg_temp;",
                     $"ALTER FUNCTION {Function} VOLATILE;",
                     $"ALTER FUNCTION {Function} SECURITY INVOKER;",
                     $"""
                      CREATE FUNCTION security.ord_auto_close_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                      ALTER FUNCTION security.ord_auto_close_rogue() OWNER TO {Executor};
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
    public async Task Migration_reapplies_idempotently_rolls_down_and_up_and_refuses_contract_violations()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddOrderAutoCloseDiscovery.UpSql);
            await ExecuteAsync(connection, reapply, "RESET ROLE;");
            await DatabaseBaselineAssertions.AssertAutoCloseExecutorInstalledAsync(connection, reapply);
            await reapply.RollbackAsync();
        }

        await using (var roundTrip = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, roundTrip, AddOrderAutoCloseDiscovery.DownSql);
            await ExecuteAsync(connection, roundTrip, "RESET ROLE;");
            Assert.Equal(
                "f|t|orders.orders.owner_org_id:SELECT,orders.orders.status:SELECT",
                await ScalarInAsync<string>(
                    connection,
                    roundTrip,
                    """
                    SELECT concat_ws('|',
                      to_regprocedure(@function) IS NOT NULL,
                      to_regrole(@executor) IS NOT NULL,
                      (SELECT string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type, ','
                         ORDER BY privilege_type, column_name)
                       FROM information_schema.column_privileges WHERE grantee=@executor))
                    """,
                    ("function", Function),
                    ("executor", Executor)));
            // Rolled back, the role and its grants stay inert and the baseline accepts them; the lane check does not.
            await new DatabaseBaselineAssertions().AssertAsync(connection, roundTrip);
            await Assert.ThrowsAsync<DatabaseAssertionException>(() =>
                DatabaseBaselineAssertions.AssertAutoCloseExecutorInstalledAsync(connection, roundTrip));

            await ExecuteAsync(connection, roundTrip, AddOrderAutoCloseDiscovery.UpSql);
            await ExecuteAsync(connection, roundTrip, "RESET ROLE;");
            await DatabaseBaselineAssertions.AssertAutoCloseExecutorInstalledAsync(connection, roundTrip);
            await new DatabaseBaselineAssertions().AssertAsync(connection, roundTrip);
            await roundTrip.RollbackAsync();
        }

        foreach (var (violation, message) in new[]
                 {
                     ("ALTER TABLE orders.orders NO FORCE ROW LEVEL SECURITY;", "requires FORCE ROW LEVEL SECURITY on orders.orders"),
                     ($"ALTER ROLE {Executor} LOGIN;", "exists with attributes outside the ORD-AUTO-CLOSE contract"),
                     ($"ALTER ROLE {Executor} NOBYPASSRLS;", "exists with attributes outside the ORD-AUTO-CLOSE contract"),
                     ($"GRANT paqueteria_worker TO {Executor};", "must not inherit any other role"),
                     ($"""
                       CREATE FUNCTION security.ord_auto_close_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                       ALTER FUNCTION security.ord_auto_close_rogue() OWNER TO {Executor};
                       """, "already owns objects outside the ORD-AUTO-CLOSE contract"),
                     ($"GRANT SELECT ON orders.order_events TO {Executor};", "privileges differ from the ORD-AUTO-CLOSE contract"),
                     ($"GRANT SELECT (id) ON orders.orders TO {Executor};", "privileges differ from the ORD-AUTO-CLOSE contract"),
                     ($"GRANT USAGE ON SCHEMA platform TO {Executor};", "schema privileges differ from the ORD-AUTO-CLOSE contract"),
                     ($"GRANT EXECUTE ON FUNCTION {Function} TO paqueteria_app;", "function security verification failed"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, violation);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddOrderAutoCloseDiscovery.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await new DatabaseBaselineAssertions().AssertAsync(connection);
    }

    [PostgreSqlContractFact]
    public async Task Populated_installation_without_the_executor_upgrades_through_the_orders_lane_without_rewriting_rows()
    {
        await DrainAsync();
        await using var scenario = await DeliveredAsync();
        var rowBefore = await OrderRowAsync(scenario.OrderId, excludeTransitionColumns: false);
        var coordinator = new ModuleMigrationCoordinator();
        try
        {
            // Roll this database back to an installation provisioned before ORD-AUTO-CLOSE: no executor role, no
            // function, Orders history without the auto-close step.
            await ExecuteAdminAsync(
                $"""
                DROP OWNED BY {Executor};
                DROP ROLE {Executor};
                DELETE FROM platform."__ef_migrations_history_orders" WHERE "MigrationId"='{AddOrderAutoCloseDiscovery.MigrationId}';
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
                await using var transaction = await connection.BeginTransactionAsync();
                await DatabaseBaselineAssertions.AssertAutoCloseExecutorInstalledAsync(connection, transaction);
                await transaction.RollbackAsync();
            }

            Assert.Equal(rowBefore, await OrderRowAsync(scenario.OrderId, excludeTransitionColumns: false));
            await using var worker = CreateWorker();
            Assert.Equal(1, (await RunPassAsync(worker)).Closed);
            await AssertClosedOnceAsync(scenario);
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

    /// <summary>
    /// The Worker composition exactly as the Worker host builds it, without starting its hosted loop: the job's
    /// cycle, ports and ORD-002 transition over the least-privilege Worker login.
    /// </summary>
    private ServiceProvider CreateWorker(
        IOrderTransitionFailureInjector? failureInjector = null,
        bool enabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaqueteriaWorker"] = fixture.WorkerConnectionString,
                ["Orders:AutoClose:Enabled"] = enabled ? "true" : "false",
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration);
        if (failureInjector is not null)
        {
            services.AddSingleton(failureInjector);
        }

        services.AddOrdersAutoClose(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    private static Task<OrderAutoCloseCycleResult> RunPassAsync(ServiceProvider worker) =>
        worker.GetRequiredService<OrderAutoCloseCycle>().RunAsync(FullPass, CancellationToken.None);

    /// <summary>Closes whatever other tests left closable, so the closed counts of a test are its own.</summary>
    private async Task DrainAsync()
    {
        await using var worker = CreateWorker();
        while ((await RunPassAsync(worker)).Closed > 0)
        {
        }
    }

    private async Task<SyntheticOrderScenario> DeliveredAsync(
        string? window = "+72 hours",
        long codExpectedCents = 0,
        Guid? operatorOrganizationId = null)
    {
        var scenario = new SyntheticOrderScenario(fixture);
        try
        {
            await scenario.InitializeAsync(OrderStatus.Delivered.ToContractValue());
            await scenario.ExecuteAdminAsync(
                """
                UPDATE orders.orders SET
                  claim_window_ends_at = CASE WHEN @window IS NULL THEN NULL
                    ELSE clock_timestamp() + @window::interval END,
                  cod_expected_cents = @cod,
                  operator_org_id = @operator
                WHERE id=@order;
                """,
                new NpgsqlParameter<string?>("window", NpgsqlDbType.Text) { TypedValue = window },
                new NpgsqlParameter<long>("cod", NpgsqlDbType.Bigint) { TypedValue = codExpectedCents },
                new NpgsqlParameter<Guid?>("operator", NpgsqlDbType.Uuid) { TypedValue = operatorOrganizationId },
                SyntheticOrderScenario.P("order", scenario.OrderId));
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    /// <summary>Adds DELIVERED orders with the same shape and claim window to the scenario's organization.</summary>
    private static async Task CloneDeliveredOrdersAsync(SyntheticOrderScenario scenario, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await scenario.ExecuteAdminAsync(
            """
            WITH source AS (
              SELECT o.*, q.request_snapshot_redacted, q.breakdown, q.input_hash, q.expires_at
              FROM orders.orders o JOIN pricing.quotes q ON q.id=o.quote_id WHERE o.id=@order
            ), wanted AS (
              SELECT id, gen_random_uuid() AS quote_id FROM unnest(@ids::uuid[]) AS t(id)
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
              id,public_id,quote_id,owner_org_id,operator_org_id,city_id,origin_location_id,destination_location_id,service_type,
              pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,package_snapshot,cod_expected_cents,version,
              claim_window_ends_at)
            SELECT w.id,'ORDAC-' || replace(w.id::text,'-',''),w.quote_id,s.owner_org_id,s.operator_org_id,s.city_id,
              s.origin_location_id,s.destination_location_id,s.service_type,s.pricing_tier,s.consolidated_route,s.payer_type,
              'DELIVERED',s.subtotal_cents,s.discount_cents,s.tax_cents,s.total_cents,s.minimum_total_cents_snapshot,s.currency,
              s.pricing_policy_version,s.package_snapshot,s.cod_expected_cents,1,s.claim_window_ends_at
            FROM wanted w CROSS JOIN source s
            WHERE EXISTS (SELECT 1 FROM quotes q WHERE q.id=w.quote_id);
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = ids });
    }

    private static OrderAutoCloseCandidate Candidate(SyntheticOrderScenario scenario) =>
        new(scenario.OrganizationId, scenario.OrderId, 1);

    private static TransitionOrderCommand ManualCommand(
        SyntheticOrderScenario scenario,
        OrderStatus target,
        int expectedVersion,
        string reason = "synthetic manual close") =>
        new(
            scenario.UserId,
            scenario.OrganizationId,
            $"ordac-manual-{Guid.NewGuid():N}",
            scenario.OrderId,
            target.ToContractValue(),
            reason,
            expectedVersion,
            null,
            true,
            "synthetic-request-id");

    /// <summary>The API composition of the ORD-002 transition (paqueteria_app), as a dispatcher uses it.</summary>
    private ApiTransitions CreateApiTransitions()
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(dbOptions, state);
        var service = new PostgreSqlOrderTransitionService(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new PostgreSqlOrderTransitionAuthorizationReader(),
            new PostgreSqlOrderTransitionReplayAuthorizationReader(),
            new PostgreSqlOrderQuoteAcceptanceGuardReader(),
            new PostgreSqlOrderAssignmentGuardReader(Options.Create(OrderAttemptCustodyPostgreSqlContractTests.EligibilityOptions())),
            new PostgreSqlOrderProofGuardReader(),
            new PostgreSqlOrderCustodyGuardReader(),
            new PostgreSqlOrderIncidentGuardReader(),
            new PostgreSqlOrderCodGuardReader(),
            new OrderTransitionAuthorizer(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new NoOpOrderTransitionFailureInjector(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                IdempotencyLifetimeMinutes = 60,
                ClaimWindowHours = 72,
                TransitionMetadataMaximumBytes = 4_096,
            }),
            new SystemClock());
        return new ApiTransitions(context, service);
    }

    private async Task AssertClosedOnceAsync(SyntheticOrderScenario scenario, Guid? expectedOperator = null)
    {
        Assert.Equal(
            "CLOSED|2|1|1|orders.status-changed,orders.timeline-event-added|2|1|1|1|1",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|', o.status, o.version,
                  (SELECT count(*) FROM orders.order_events e WHERE e.order_id=o.id),
                  (SELECT count(*) FROM orders.order_events e
                    WHERE e.order_id=o.id AND e.aggregate_version=2 AND e.event_type='ORDER_STATUS_CHANGED'
                      AND e.actor_id IS NULL AND e.public_event_code IS NULL
                      AND e.owner_org_id=o.owner_org_id AND COALESCE(e.operator_org_id::text,'')=@operator
                      AND e.payload=jsonb_build_object(
                        'previous_status','DELIVERED','new_status','CLOSED','reason_redacted',@reason::text)),
                  (SELECT string_agg(x.topic, ',' ORDER BY x.topic) FROM platform.outbox_events x
                    WHERE x.aggregate_id=o.id AND x.owner_org_id=o.owner_org_id AND x.aggregate_version=2
                      AND x.status='PENDING'
                      AND x.tenant_context=jsonb_build_object('organization_ids',jsonb_build_array(o.owner_org_id::text))),
                  (SELECT count(*) FROM platform.outbox_events x WHERE x.aggregate_id=o.id),
                  (SELECT count(*) FROM platform.audit_logs a
                    WHERE a.entity_id=o.id AND a.action='ORDER_STATUS_CHANGED' AND a.entity_type='Order'
                      AND a.org_id=o.owner_org_id AND a.actor_id IS NULL AND a.request_id IS NULL
                      AND a.payload_redacted->>'reason_redacted'=@reason::text
                      AND a.payload_redacted->>'previous_status'='DELIVERED'
                      AND a.payload_redacted->>'new_status'='CLOSED'),
                  (SELECT count(*) FROM platform.audit_logs a WHERE a.entity_id=o.id),
                  (SELECT count(*) FROM platform.idempotency_keys k
                    WHERE k.owner_org_id=o.owner_org_id AND k.scope='ORD-002:TRANSITION_ORDER'
                      AND k.idempotency_key LIKE 'ord-auto-close-%' AND k.response_status=200 AND k.resource_id=o.id),
                  (SELECT count(*) FROM platform.idempotency_keys k WHERE k.owner_org_id=o.owner_org_id))
                FROM orders.orders o WHERE o.id=@order
                """,
                ("order", scenario.OrderId),
                ("operator", expectedOperator?.ToString("D") ?? string.Empty),
                ("reason", OrderSystemTransitionPolicy.AutoCloseReason)));

        // Nothing in the rows the close wrote carries personal data: the event payload is exactly the two statuses
        // and the fixed reason, and the outbox payloads carry identifiers, statuses and the fixed summary only.
        Assert.False(await ScalarAsync<bool>(
            """
            SELECT EXISTS (
              SELECT 1 FROM platform.outbox_events x
              WHERE x.aggregate_id=@order
                AND (x.payload::text ILIKE '%synthetic%' OR x.payload::text ILIKE '%oidc|%'))
            """,
            ("order", scenario.OrderId)));
    }

    private async Task AssertUntouchedAsync(SyntheticOrderScenario scenario, OrderStatus expectedStatus)
    {
        Assert.Equal(
            $"{expectedStatus.ToContractValue()}|1|0|0|0|0",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|', o.status, o.version,
                  (SELECT count(*) FROM orders.order_events WHERE order_id=o.id),
                  (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=o.id),
                  (SELECT count(*) FROM platform.audit_logs WHERE entity_id=o.id),
                  (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=o.owner_org_id))
                FROM orders.orders o WHERE o.id=@order
                """,
                ("order", scenario.OrderId)));
    }

    /// <summary>Everything an organization owns that a close could change.</summary>
    private async Task<string> TenantFingerprintAsync(SyntheticOrderScenario scenario) =>
        await ScalarAsync<string>(
            """
            SELECT concat_ws('|',
              (SELECT jsonb_agg(to_jsonb(o) ORDER BY o.id)::text FROM orders.orders o WHERE o.owner_org_id=@org),
              (SELECT jsonb_agg(to_jsonb(e) ORDER BY e.id)::text FROM orders.order_events e WHERE e.owner_org_id=@org),
              (SELECT jsonb_agg(to_jsonb(x) ORDER BY x.id)::text FROM platform.outbox_events x WHERE x.owner_org_id=@org),
              (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id)::text FROM platform.audit_logs a WHERE a.org_id=@org),
              (SELECT jsonb_agg(to_jsonb(k) ORDER BY k.idempotency_key)::text FROM platform.idempotency_keys k
                WHERE k.owner_org_id=@org),
              (SELECT jsonb_agg(to_jsonb(i) ORDER BY i.id)::text FROM incidents.incidents i WHERE i.owner_org_id=@org),
              (SELECT jsonb_agg(to_jsonb(c) ORDER BY c.id)::text FROM finance.cod_transactions c WHERE c.owner_org_id=@org))
            """,
            ("org", scenario.OrganizationId));

    /// <summary>
    /// The rows one DELIVERED -> CLOSED transition wrote, without what legitimately differs between two orders or
    /// between a member and the system actor (identifiers, instants, actor, reason and request id).
    /// </summary>
    private async Task<string> NormalizedTransitionRowsAsync(SyntheticOrderScenario scenario) =>
        await ScalarAsync<string>(
            """
            SELECT jsonb_build_object(
              'order', (SELECT jsonb_build_object('status',o.status,'version',o.version,
                          'finalized',o.finalized_at IS NOT NULL,'window',o.claim_window_ends_at IS NOT NULL)
                        FROM orders.orders o WHERE o.id=@order),
              'events', (SELECT jsonb_agg(jsonb_build_object(
                          'version',e.aggregate_version,'type',e.event_type,'public_event_code',e.public_event_code,
                          'owner',e.owner_org_id=@org,'operator',e.operator_org_id,
                          'payload',e.payload - 'reason_redacted',
                          'reason_present',e.payload ? 'reason_redacted') ORDER BY e.aggregate_version)
                        FROM orders.order_events e WHERE e.order_id=@order),
              'outbox', (SELECT jsonb_agg(jsonb_build_object(
                          'topic',x.topic,'aggregate_type',x.aggregate_type,'version',x.aggregate_version,
                          'priority',x.priority,'status',x.status,'attempts',x.attempts,
                          'tenant',x.tenant_context=jsonb_build_object('organization_ids',jsonb_build_array(@org::text)),
                          'event_link',COALESCE(x.payload->>'order_event_id',x.payload->>'timeline_event_id')=
                            (SELECT e.id::text FROM orders.order_events e WHERE e.order_id=@order AND e.aggregate_version=2),
                          'order_link',x.payload->>'order_id'=@order::text,
                          'payload',x.payload - 'order_id' - 'order_event_id' - 'timeline_event_id' - 'public_order_id'
                            - 'occurred_at') ORDER BY x.topic)
                        FROM platform.outbox_events x WHERE x.aggregate_id=@order),
              'audit', (SELECT jsonb_agg(jsonb_build_object(
                          'action',a.action,'entity_type',a.entity_type,'org',a.org_id=@org,
                          'payload',a.payload_redacted - 'order_id' - 'reason_redacted' - 'request_id'))
                        FROM platform.audit_logs a WHERE a.entity_id=@order),
              'idempotency', (SELECT jsonb_agg(jsonb_build_object(
                          'scope',k.scope,'status',k.response_status,'resource',k.resource_id=@order))
                        FROM platform.idempotency_keys k WHERE k.owner_org_id=@org))::text
            """,
            ("order", scenario.OrderId),
            ("org", scenario.OrganizationId));

    private async Task<string> OrderRowAsync(Guid orderId, bool excludeTransitionColumns) =>
        await ScalarAsync<string>(
            """
            SELECT (CASE WHEN @exclude THEN to_jsonb(o) - 'status' - 'version' - 'updated_at' ELSE to_jsonb(o) END)::text
            FROM orders.orders o WHERE o.id=@order
            """,
            ("order", orderId),
            ("exclude", excludeTransitionColumns));

    private async Task<string> LifecycleAsync(Guid orderId) =>
        await ScalarAsync<string>(
            "SELECT concat_ws('|',status,finalized_at IS NOT NULL) FROM orders.orders WHERE id=@order",
            ("order", orderId));

    private async Task<(string Name, string Definition)> MinimumTotalConstraintAsync()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT quote_ident(conname), pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conrelid='orders.orders'::regclass AND contype='c'
              AND pg_get_constraintdef(oid) LIKE '%total_cents >= minimum_total_cents_snapshot%'
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var constraint = (reader.GetString(0), reader.GetString(1));
        Assert.False(await reader.ReadAsync());
        return constraint;
    }

    private async Task<List<Guid>> DiscoverAsWorkerAsync(Guid? after, int? limit)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker;");
        await using var command = new NpgsqlCommand(
            "SELECT owners.id FROM security.list_auto_close_owner_organizations(@after,@limit) AS owners(id);",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid?>("after", NpgsqlDbType.Uuid) { TypedValue = after });
        command.Parameters.Add(new NpgsqlParameter<int?>("limit", NpgsqlDbType.Integer) { TypedValue = limit });
        var owners = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                owners.Add(reader.GetGuid(0));
            }
        }

        await transaction.RollbackAsync();
        return owners;
    }

    private static string Canonical(Guid value) => value.ToString("D");

    private static async Task InsertCodAsync(SyntheticOrderScenario scenario, string status, long amount) =>
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO finance.cod_transactions(
              id,order_id,owner_org_id,amount_cents,status,recorded_at)
            VALUES (gen_random_uuid(),@order,@org,@amount,@status,clock_timestamp());
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("amount", amount),
            SyntheticOrderScenario.P("status", status));

    /// <summary>
    /// An OPEN incident with its INC-001 reason, next action, SLA and evidence (a proof of the same order), inserted
    /// in one statement because the evidence requirement is deferred to commit.
    /// </summary>
    private static async Task<Guid> InsertIncidentAsync(SyntheticOrderScenario scenario)
    {
        var incidentId = Guid.NewGuid();
        var uploadId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            VALUES (
              @upload,@order,@org,@user,@quarantine,'image/jpeg',1024,'READY',clock_timestamp()+interval '1 day');
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,captured_at,created_by)
            VALUES (
              @proof,@order,@org,@upload,'DELIVERY_PHOTO',@object_key,
              decode(repeat('04',32),'hex'),'image/jpeg',100,clock_timestamp(),@user);
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              reason_code,next_action,occurred_at,sla_due_at,created_by)
            VALUES (
              @incident,@order,@org,'SYNTHETIC','LOW','OPEN',false,
              'RECIPIENT_ABSENT','RESCHEDULED',clock_timestamp(),clock_timestamp()+interval '72 hours',@user);
            INSERT INTO incidents.incident_evidence(
              id,incident_id,order_id,owner_org_id,proof_id,created_by)
            VALUES (gen_random_uuid(),@incident,@order,@org,@proof,@user);
            """,
            SyntheticOrderScenario.P("incident", incidentId),
            SyntheticOrderScenario.P("upload", uploadId),
            SyntheticOrderScenario.P("proof", proofId),
            SyntheticOrderScenario.P("quarantine", $"quarantine/ordac/{uploadId:N}"),
            SyntheticOrderScenario.P("object_key", $"proofs/ordac/{uploadId:N}"),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId));
        return incidentId;
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

    private static async Task<T> ScalarInAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
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

    private sealed class ApiTransitions(OrdersDbContext context, IOrderTransitionService service) : IAsyncDisposable
    {
        internal IOrderTransitionService Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class ThrowAtTransitionStage(OrderTransitionStage target) : IOrderTransitionFailureInjector
    {
        public Task OnStageAsync(OrderTransitionStage stage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return stage == target ? throw new InjectedTransitionFailure(stage) : Task.CompletedTask;
        }
    }

    private sealed class InjectedTransitionFailure(OrderTransitionStage stage)
        : Exception($"Injected automatic close failure at {stage}.")
    {
        public OrderTransitionStage Stage { get; } = stage;
    }
}
