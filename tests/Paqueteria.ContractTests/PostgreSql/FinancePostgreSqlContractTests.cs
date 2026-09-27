using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Financials;
using Finance.Domain;
using Finance.Infrastructure;
using Finance.Infrastructure.Cod;
using Finance.Infrastructure.Financials;
using Finance.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class FinancePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 17, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task Fin001_cod_reaches_recorded_before_delivery_and_reconciled_before_close()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(fixture.AppDataSource);
        var financials = CreateFinancialsService(fixture.AppDataSource);

        var recorded = await cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "record"), default);
        Assert.Equal("RECORDED", recorded.Status);
        Assert.Equal(5_000, recorded.AmountCents);
        Assert.Equal(Now, recorded.RecordedAt);
        Assert.Null(recorded.ReconciledAt);
        Assert.Equal(scenario.DriverId, await scenario.CollectingDriverAsync(scenario.CodOrderId));

        // The same key replays the stored response instead of creating a second collection.
        Assert.Equal(recorded, await cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "record"), default));
        Assert.Equal(1, await scenario.CodRowCountAsync(scenario.CodOrderId));

        var afterRecord = await financials.GetOrderFinancialsAsync(scenario.OrderQuery(scenario.CodOrderId), default);
        Assert.True(afterRecord.Cod.SatisfiesDeliveryRequirement);
        Assert.False(afterRecord.Cod.SatisfiesCloseRequirement);

        await scenario.SetOrderStatusAsync(scenario.CodOrderId, "DELIVERED");
        var reconciled = await cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "reconcile"), default);
        Assert.Equal("RECONCILED", reconciled.Status);
        Assert.Equal(Now, reconciled.ReconciledAt);
        Assert.Equal(recorded.RecordedAt, reconciled.RecordedAt);
        Assert.Equal(reconciled, await cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "reconcile"), default));

        var afterReconcile = await financials.GetOrderFinancialsAsync(
            scenario.OrderQuery(scenario.CodOrderId), default);
        Assert.True(afterReconcile.Cod.SatisfiesDeliveryRequirement);
        Assert.True(afterReconcile.Cod.SatisfiesCloseRequirement);

        // A second reconciliation under a fresh key is a state conflict, not a silent no-op.
        var repeat = await Assert.ThrowsAsync<FinanceConflictException>(() =>
            cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "reconcile-again"), default));
        Assert.Equal(FinanceConflictCode.CodStateConflict, repeat.Code);

        // Exactly one audit record per accepted mutation: replays and conflicts add none, and FIN-001
        // publishes no outbox topic that would be claimed as UNOWNED and settled DEAD.
        var evidence = await scenario.CodEvidenceAsync();
        Assert.Equal(2, evidence.Audits);
        Assert.Equal(0, evidence.FinanceOutbox);
    }

    [PostgreSqlContractFact]
    public async Task Fin001_cod_recording_fails_closed_on_amount_state_and_expectation()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(fixture.AppDataSource);

        var mismatch = await Assert.ThrowsAsync<FinanceConflictException>(() =>
            cod.RecordAsync(scenario.Record(scenario.CodOrderId, 4_999, "mismatch"), default));
        Assert.Equal(FinanceConflictCode.CodAmountMismatch, mismatch.Code);

        var notExpected = await Assert.ThrowsAsync<FinanceConflictException>(() =>
            cod.RecordAsync(scenario.Record(scenario.OwnOrderId, 5_000, "not-expected"), default));
        Assert.Equal(FinanceConflictCode.CodNotExpected, notExpected.Code);

        await scenario.SetOrderStatusAsync(scenario.CodOrderId, "DELIVERED");
        var tooLate = await Assert.ThrowsAsync<FinanceConflictException>(() =>
            cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "too-late"), default));
        Assert.Equal(FinanceConflictCode.OrderStateConflict, tooLate.Code);
        Assert.Equal(0, await scenario.CodRowCountAsync(scenario.CodOrderId));

        await scenario.SetOrderStatusAsync(scenario.CodOrderId, "DELIVERING");
        var recorded = await cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "ok"), default);
        var duplicate = await Assert.ThrowsAsync<FinanceConflictException>(() =>
            cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "duplicate"), default));
        Assert.Equal(FinanceConflictCode.CodAlreadyRecorded, duplicate.Code);

        // Reusing a key with a different payload is an idempotency conflict, never a second write.
        var reused = await Assert.ThrowsAsync<FinanceConflictException>(() =>
            cod.RecordAsync(scenario.Record(scenario.CodOrderId, 4_000, "ok"), default));
        Assert.Equal(FinanceConflictCode.IdempotencyConflict, reused.Code);
        Assert.Equal(1, await scenario.CodRowCountAsync(scenario.CodOrderId));
        Assert.Equal("RECORDED", (await scenario.CodStatusAsync(recorded.Id))!);
    }

    [PostgreSqlContractFact]
    public async Task Fin001_unit_economics_derive_revenue_cost_by_modality_and_margin()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var financials = CreateFinancialsService(fixture.AppDataSource);

        var own = await financials.GetOrderFinancialsAsync(scenario.OrderQuery(scenario.OwnOrderId), default);
        Assert.Equal("MXN", own.Currency);
        Assert.Equal(12_000, own.RevenueCents);
        Assert.Equal(4_500, own.CostCents);
        Assert.Equal(7_500, own.MarginCents);
        Assert.Equal(6_250, own.MarginBasisPoints);
        Assert.Equal(4_500, Bucket(own, DeliveryModality.Own).CostCents);
        Assert.Equal(0, Bucket(own, DeliveryModality.External).CostCents);

        var external = await financials.GetOrderFinancialsAsync(
            scenario.OrderQuery(scenario.ExternalOrderId), default);
        Assert.Equal(20_000, external.RevenueCents);
        Assert.Equal(8_000, external.CostCents);
        Assert.Equal(12_000, external.MarginCents);
        Assert.Equal(8_000, Bucket(external, DeliveryModality.External).CostCents);
        Assert.Equal(1, Bucket(external, DeliveryModality.External).AssignmentCount);
        Assert.Equal(0, Bucket(external, DeliveryModality.Own).AssignmentCount);

        // A cancelled assignment carries no cost, so it must not move the margin.
        await scenario.CancelAssignmentAsync(scenario.OwnOrderId);
        var afterCancel = await financials.GetOrderFinancialsAsync(
            scenario.OrderQuery(scenario.OwnOrderId), default);
        Assert.Equal(0, afterCancel.CostCents);
        Assert.Equal(12_000, afterCancel.MarginCents);
        Assert.Equal(10_000, afterCancel.MarginBasisPoints);

        await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
            financials.GetOrderFinancialsAsync(scenario.OrderQuery(Guid.NewGuid()), default));
    }

    [PostgreSqlContractFact]
    public async Task Fin001_route_totals_reconcile_against_the_orders_on_the_route()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(fixture.AppDataSource);
        var financials = CreateFinancialsService(fixture.AppDataSource);

        // Cash that is expected but not yet collected is an expectation, never cash awaiting reconciliation.
        var uncollected = await financials.GetRouteFinancialsAsync(scenario.RouteQuery(scenario.RouteId), default);
        Assert.Equal(5_000, uncollected.CodExpectedCentsTotal);
        Assert.Equal(0, uncollected.CodPendingReconciliationCentsTotal);
        Assert.Equal(0, uncollected.CodPendingReconciliationCount);
        Assert.Null(uncollected.Orders.Single(order => order.OrderId == scenario.CodOrderId).Cod.Status);

        var recorded = await cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "route-record"), default);

        var route = await financials.GetRouteFinancialsAsync(scenario.RouteQuery(scenario.RouteId), default);
        Assert.Equal("MXN", route.Currency);
        Assert.Equal("DRAFT", route.RouteStatus);
        Assert.Equal(3, route.OrderCount);
        Assert.Equal(41_000, route.RevenueCentsTotal);
        Assert.Equal(15_500, route.CostCentsTotal);
        Assert.Equal(25_500, route.MarginCentsTotal);
        Assert.Equal(6_219, route.MarginBasisPoints);
        Assert.Equal(7_500, RouteBucket(route, DeliveryModality.Own).CostCents);
        Assert.Equal(2, RouteBucket(route, DeliveryModality.Own).AssignmentCount);
        Assert.Equal(8_000, RouteBucket(route, DeliveryModality.External).CostCents);
        Assert.Equal(0, RouteBucket(route, DeliveryModality.AllyCapacity).AssignmentCount);
        Assert.Equal(5_000, route.CodExpectedCentsTotal);
        Assert.Equal(5_000, route.CodPendingReconciliationCentsTotal);
        Assert.Equal(1, route.CodPendingReconciliationCount);

        Assert.Equal(
            route.RevenueCentsTotal,
            route.Orders.Sum(order => order.RevenueCents));
        Assert.Equal(
            route.CostCentsTotal,
            route.Orders.Sum(order => order.CostCents));
        Assert.Equal(route.MarginCentsTotal, route.Orders.Sum(order => order.MarginCents));
        Assert.Equal(route.CodExpectedCentsTotal, route.Orders.Sum(order => order.Cod.ExpectedCents));
        Assert.Equal(
            route.CodPendingReconciliationCentsTotal,
            route.Orders.Where(order => order.Cod.Status == "RECORDED").Sum(order => order.Cod.AmountCents!.Value));
        foreach (var order in route.Orders)
        {
            var single = await financials.GetOrderFinancialsAsync(scenario.OrderQuery(order.OrderId), default);
            Assert.Equal(single.RevenueCents, order.RevenueCents);
            Assert.Equal(single.CostCents, order.CostCents);
            Assert.Equal(single.MarginCents, order.MarginCents);
            Assert.Equal(single.Cod, order.Cod);
        }

        // Reconciled cash is no longer pending, while the expectation total is unchanged.
        await scenario.SetOrderStatusAsync(scenario.CodOrderId, "DELIVERED");
        await cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "route-reconcile"), default);
        var reconciled = await financials.GetRouteFinancialsAsync(scenario.RouteQuery(scenario.RouteId), default);
        Assert.Equal(5_000, reconciled.CodExpectedCentsTotal);
        Assert.Equal(0, reconciled.CodPendingReconciliationCentsTotal);
        Assert.Equal(0, reconciled.CodPendingReconciliationCount);

        await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
            financials.GetRouteFinancialsAsync(scenario.RouteQuery(Guid.NewGuid()), default));
    }

    [PostgreSqlContractFact]
    public async Task Fin001_a_driver_whose_assignment_changes_while_waiting_for_the_order_lock_cannot_record()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        await scenario.CreateReplacementDriverAsync();
        var cod = CreateCodService(fixture.AppDataSource);

        // Dispatch replaces an assignment while holding the order row lock. Hold that lock with the
        // replacement staged, let the assigned driver's request — authorized on everything the wait cannot
        // change — block behind it, and only then commit the replacement.
        await using var dispatch = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var replacement = await dispatch.BeginTransactionAsync();
        var dispatchPid = await scenario.ReplaceAssignmentUnderOrderLockAsync(
            dispatch, replacement, scenario.CodOrderId);

        var stale = Task.Run(() => cod.RecordAsync(
            scenario.DriverRecord(scenario.CodOrderId, 5_000, "stale-driver"), default));
        await WaitUntilBlockedByAsync(dispatchPid, stale);
        await replacement.CommitAsync();

        // The claim is decided from the assignment observed under the lock, so the stale driver fails closed
        // and leaves no collection, audit or idempotency evidence behind.
        await Assert.ThrowsAsync<FinanceForbiddenException>(() => stale);
        Assert.Equal(0, await scenario.CodRowCountAsync(scenario.CodOrderId));
        Assert.Equal(0, (await scenario.CodEvidenceAsync()).Audits);
        Assert.Equal((0L, (int?)null), await scenario.IdempotencyRecordAsync("stale-driver"));

        // The driver now holding the assignment records it and is the one credited with the collection.
        var current = await cod.RecordAsync(
            scenario.ReplacementDriverRecord(scenario.CodOrderId, 5_000, "current-driver"), default);
        Assert.Equal("RECORDED", current.Status);
        Assert.Equal(scenario.ReplacementDriverId, await scenario.CollectingDriverAsync(scenario.CodOrderId));
        Assert.Equal(1, (await scenario.CodEvidenceAsync()).Audits);
        Assert.Equal((1L, (int?)201), await scenario.IdempotencyRecordAsync("current-driver"));
    }

    [PostgreSqlContractFact]
    public async Task Fin001_a_store_failure_inside_the_transaction_is_unavailable_and_leaves_no_evidence()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(
            fixture.AppDataSource,
            new ThrowingFailureInjector(
                FinanceTransactionStage.AuditInserted,
                new NpgsqlException("Simulated store failure after the audit row was written.")));

        var failure = await Assert.ThrowsAsync<FinanceUnavailableException>(() =>
            cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "store-failure"), default));
        Assert.IsType<NpgsqlException>(failure.InnerException);
        Assert.Equal(0, await scenario.CodRowCountAsync(scenario.CodOrderId));
        Assert.Equal(0, (await scenario.CodEvidenceAsync()).Audits);
        Assert.Equal((0L, (int?)null), await scenario.IdempotencyRecordAsync("store-failure"));
    }

    [PostgreSqlContractFact]
    public async Task Fin001_finance_is_tenant_safe_and_capability_fail_closed()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        await using var foreignScenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(fixture.AppDataSource);
        var financials = CreateFinancialsService(fixture.AppDataSource);

        // Another tenant's order and route are indistinguishable from an absent one.
        await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
            cod.RecordAsync(scenario.Record(foreignScenario.CodOrderId, 5_000, "cross-tenant"), default));
        await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
            financials.GetOrderFinancialsAsync(scenario.OrderQuery(foreignScenario.OwnOrderId), default));
        await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
            financials.GetRouteFinancialsAsync(scenario.RouteQuery(foreignScenario.RouteId), default));

        var foreignCod = CreateCodService(fixture.AppDataSource);
        var recorded = await foreignCod.RecordAsync(
            foreignScenario.Record(foreignScenario.CodOrderId, 5_000, "foreign-record"), default);
        await Assert.ThrowsAsync<FinanceNotFoundException>(() =>
            cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "cross-tenant-reconcile"), default));

        // The driver holding the active assignment may record the collection they physically took.
        var driverRecorded = await cod.RecordAsync(
            scenario.DriverRecord(scenario.CodOrderId, 5_000, "driver-record"), default);
        Assert.Equal("RECORDED", driverRecorded.Status);

        // That same driver may never reconcile their own collection, nor read order margins.
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            cod.ReconcileAsync(scenario.DriverReconcile(driverRecorded.Id, "driver-reconcile"), default));
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            financials.GetOrderFinancialsAsync(scenario.DriverOrderQuery(scenario.OwnOrderId), default));

        // A driver with no assignment on the order cannot record against it either.
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            cod.RecordAsync(
                foreignScenario.Record(foreignScenario.CodOrderId, 5_000, "unrelated-driver") with
                {
                    ActorId = foreignScenario.UnassignedDriverUserId,
                },
                default));

        // A PLATFORM_ADMIN without a satisfied MFA challenge cannot reconcile.
        await scenario.SetActorRoleAsync("PLATFORM_ADMIN");
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            cod.ReconcileAsync(scenario.Reconcile(driverRecorded.Id, "admin-no-mfa"), default));
        var withMfa = await cod.ReconcileAsync(
            scenario.Reconcile(driverRecorded.Id, "admin-mfa") with { MfaSatisfied = true }, default);
        Assert.Equal("RECONCILED", withMfa.Status);
    }

    /// <summary>
    /// FINANCE-COD-RECONCILIATION and FINANCE-COD-MFA-2026-09-27 on the runtime role: FINANCE closes the cash
    /// position of a collection someone else recorded and reads financials, both only with a satisfied MFA
    /// challenge, and never records a collection.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task Finance_reconciles_collected_cod_and_reads_financials_only_with_mfa()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(fixture.AppDataSource);
        var financials = CreateFinancialsService(fixture.AppDataSource);
        var recorded = await cod.RecordAsync(
            scenario.DriverRecord(scenario.CodOrderId, 5_000, "finance-driver-record"), default);
        Assert.Equal("RECORDED", recorded.Status);

        await scenario.SetActorRoleAsync("FINANCE");

        // Without MFA: the uniform 403 for reading and reconciling, and nothing changes.
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "finance-no-mfa"), default));
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            financials.GetOrderFinancialsAsync(scenario.OrderQuery(scenario.OwnOrderId), default));
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            financials.GetRouteFinancialsAsync(scenario.RouteQuery(scenario.RouteId), default));
        Assert.Equal("RECORDED", await scenario.CodStatusAsync(recorded.Id));

        // Recording a collection is never FINANCE's, with or without MFA.
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            cod.RecordAsync(scenario.Record(scenario.CodOrderId, 5_000, "finance-record"), default));
        await Assert.ThrowsAsync<FinanceForbiddenException>(() =>
            cod.RecordAsync(
                scenario.Record(scenario.CodOrderId, 5_000, "finance-record-mfa") with { MfaSatisfied = true },
                default));

        // With MFA: financials are readable and the collection is reconciled exactly once.
        await financials.GetOrderFinancialsAsync(
            scenario.OrderQuery(scenario.OwnOrderId) with { MfaSatisfied = true }, default);
        await financials.GetRouteFinancialsAsync(
            scenario.RouteQuery(scenario.RouteId) with { MfaSatisfied = true }, default);
        var reconcile = scenario.Reconcile(recorded.Id, "finance-mfa") with { MfaSatisfied = true };
        var reconciled = await cod.ReconcileAsync(reconcile, default);
        Assert.Equal("RECONCILED", reconciled.Status);
        Assert.Equal(recorded.Id, reconciled.Id);
        Assert.Equal(5_000, reconciled.AmountCents);
        Assert.Equal("RECONCILED", await scenario.CodStatusAsync(recorded.Id));
        Assert.Equal(reconciled, await cod.ReconcileAsync(reconcile, default));
    }

    /// <summary>FINANCE-COD-MFA-2026-09-27: DISPATCHER keeps reconciling and reading without MFA.</summary>
    [PostgreSqlContractFact]
    public async Task Dispatcher_keeps_reconciling_and_reading_financials_without_mfa()
    {
        await using var scenario = await FinanceScenario.CreateAsync(fixture);
        var cod = CreateCodService(fixture.AppDataSource);
        var financials = CreateFinancialsService(fixture.AppDataSource);
        var recorded = await cod.RecordAsync(
            scenario.DriverRecord(scenario.CodOrderId, 5_000, "dispatcher-driver-record"), default);

        await scenario.SetActorRoleAsync("DISPATCHER");
        await financials.GetOrderFinancialsAsync(scenario.OrderQuery(scenario.OwnOrderId), default);
        var reconciled = await cod.ReconcileAsync(scenario.Reconcile(recorded.Id, "dispatcher-no-mfa"), default);
        Assert.Equal("RECONCILED", reconciled.Status);
    }

    private PostgreSqlCodTransactionService CreateCodService(
        NpgsqlDataSource dataSource,
        IFinanceFailureInjector? failureInjector = null)
    {
        var (gateway, state) = CreateGateway(dataSource);
        return new(
            gateway,
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            failureInjector ?? new NoOpFinanceFailureInjector(),
            new FixedClock(Now));
    }

    /// <summary>
    /// Waits, without a fixed sleep, until a backend is blocked by <paramref name="blockerPid"/>. PostgreSQL
    /// contract tests run one at a time, so the only session that can be is the request under test.
    /// </summary>
    private async Task WaitUntilBlockedByAsync(int blockerPid, Task pending)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                Assert.Fail("The request completed without waiting for the order lock.");
            }

            await using var probe = fixture.AdminDataSource.CreateCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)));");
            probe.Parameters.AddWithValue("blocker", blockerPid);
            if ((bool)(await probe.ExecuteScalarAsync())!)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The request never waited for the order lock.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private PostgreSqlOrderFinancialsService CreateFinancialsService(NpgsqlDataSource dataSource) =>
        new(CreateGateway(dataSource).Gateway);

    private static (FinanceTenantGateway Gateway, TenantDatabaseExecutionState State) CreateGateway(
        NpgsqlDataSource dataSource)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new FinanceDbContext(options, state);
        return (
            new FinanceTenantGateway(
                new TenantTransactionContext<FinanceDbContext>(context, state),
                Options.Create(new FinanceOptions { Provider = FinanceProviderKind.PostgreSql })),
            state);
    }

    private static ModalityCostResult Bucket(OrderFinancialsResult result, DeliveryModality modality) =>
        result.CostByModality.Single(bucket => bucket.Modality == modality.ToContractValue());

    private static ModalityCostResult RouteBucket(RouteFinancialsResult result, DeliveryModality modality) =>
        result.CostByModality.Single(bucket => bucket.Modality == modality.ToContractValue());

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; } = now; }

    private sealed class ThrowingFailureInjector(FinanceTransactionStage stage, Exception failure)
        : IFinanceFailureInjector
    {
        public Task OnStageAsync(FinanceTransactionStage current, CancellationToken cancellationToken) =>
            current == stage ? Task.FromException(failure) : Task.CompletedTask;
    }

    /// <summary>
    /// Synthetic FIN-001 fixture: one route with an OWN order, an EXTERNAL order and a COD order, so a
    /// single scenario covers cost by modality, route totals and the COD lifecycle.
    /// </summary>
    private sealed class FinanceScenario : IAsyncDisposable
    {
        private readonly SyntheticOrderScenario scenario;
        private readonly PostgreSqlContractFixture fixture;
        private readonly List<Guid> orderIds = [];

        private FinanceScenario(PostgreSqlContractFixture fixture)
        {
            this.fixture = fixture;
            scenario = new SyntheticOrderScenario(fixture);
        }

        public Guid OrganizationId => scenario.OrganizationId;
        public Guid ActorId => scenario.UserId;
        public Guid DriverId { get; } = Guid.NewGuid();
        public Guid DriverUserId { get; } = Guid.NewGuid();
        public Guid UnassignedDriverUserId { get; } = Guid.NewGuid();

        /// <summary>
        /// Profile of the unassigned DRIVER member, created only by tests that reassign an order to them.
        /// </summary>
        public Guid ReplacementDriverId { get; } = Guid.NewGuid();
        public Guid RouteId { get; } = Guid.NewGuid();
        public Guid OwnOrderId => orderIds[0];
        public Guid ExternalOrderId => orderIds[1];
        public Guid CodOrderId => orderIds[2];

        public static async Task<FinanceScenario> CreateAsync(PostgreSqlContractFixture fixture)
        {
            var value = new FinanceScenario(fixture);
            await value.scenario.InitializeAsync("READY_FOR_PICKUP", "USED", createOrder: false);
            await value.ExecuteAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status,created_at) VALUES
                  (@driver_user,@subject,'ACTIVE',@created),
                  (@other_driver_user,@other_subject,'ACTIVE',@created);
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at) VALUES
                  (gen_random_uuid(),@driver_user,@org,'DRIVER','ACTIVE',true,@created),
                  (gen_random_uuid(),@other_driver_user,@org,'DRIVER','ACTIVE',true,@created);
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                VALUES (@driver,@driver_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
                INSERT INTO routes.routes(
                  id,operator_org_id,city_id,driver_id,status,version,scheduled_for,created_at,updated_at)
                VALUES (@route,@org,@city,@driver,'DRAFT',1,@scheduled,@created,@created);
                """,
                P("driver_user", value.DriverUserId),
                P("subject", $"fin001-{value.DriverUserId:N}"),
                P("other_driver_user", value.UnassignedDriverUserId),
                P("other_subject", $"fin001-{value.UnassignedDriverUserId:N}"),
                P("created", Now.AddDays(-1)),
                P("org", value.OrganizationId),
                P("driver", value.DriverId),
                P("city", value.scenario.CityId),
                P("route", value.RouteId),
                P("scheduled", new DateOnly(2026, 9, 23)));
            await value.AddOrderAsync(12_000, 4_500, "OWN", 0, 1);
            await value.AddOrderAsync(20_000, 8_000, "EXTERNAL", 0, 2);
            await value.AddOrderAsync(9_000, 3_000, "OWN", 5_000, 3);
            return value;
        }

        private async Task AddOrderAsync(
            long totalCents,
            long costCents,
            string assignmentType,
            long codExpectedCents,
            int sequence)
        {
            var quote = Guid.NewGuid();
            var order = Guid.NewGuid();
            orderIds.Add(order);
            await ExecuteAsync(
                """
                INSERT INTO pricing.quotes(
                  id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
                  consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                  minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,
                  package_snapshot,breakdown,input_hash,status,expires_at)
                VALUES (@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
                  @total,0,0,@total,0,'MXN','fin-001-price-snapshot-v1','{}','[{"weight_grams":500}]','{}',
                  decode(repeat('02',32),'hex'),'USED',@expires);
                INSERT INTO orders.orders(
                  id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
                  service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
                  tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
                  package_snapshot,financial_override,cod_expected_cents,version)
                VALUES (@order,@public_id,@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
                  'SENDER','DELIVERING',@total,0,0,@total,0,'MXN','fin-001-price-snapshot-v1',
                  '[{"weight_grams":500}]',NULL,@cod,1);
                INSERT INTO orders.package_items(
                  id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
                VALUES (gen_random_uuid(),@order,@org,'synthetic FIN package',500,0,
                  '{"length_mm":100,"width_mm":80,"height_mm":60}');
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,route_id,assignment_type,status,cost_cents,
                  accepted_at,created_at)
                VALUES (gen_random_uuid(),@order,@org,@driver,@route,@assignment_type,'ACTIVE',@cost,@now,@now);
                INSERT INTO routes.route_stops(
                  id,route_id,order_id,operator_org_id,sequence,stop_type,status)
                VALUES (gen_random_uuid(),@route,@order,@org,@sequence,'DELIVERY','PENDING');
                """,
                P("quote", quote),
                P("order", order),
                P("public_id", $"FIN001-{Guid.NewGuid():N}"),
                P("org", OrganizationId),
                P("city", scenario.CityId),
                P("origin", scenario.OriginLocationId),
                P("destination", scenario.DestinationLocationId),
                P("total", totalCents),
                P("cost", costCents),
                P("cod", codExpectedCents),
                P("assignment_type", assignmentType),
                P("sequence", sequence),
                P("route", RouteId),
                P("driver", DriverId),
                P("expires", Now.AddDays(1)),
                P("now", Now));
        }

        public RecordCodCollectionCommand Record(Guid orderId, long amountCents, string key) => new(
            ActorId, OrganizationId, Key(key), orderId, amountCents, $"cash-{key}", false, key);

        public ReconcileCodCommand Reconcile(Guid codId, string key) => new(
            ActorId, OrganizationId, Key(key), codId, false, key);

        public GetOrderFinancialsQuery OrderQuery(Guid orderId) => new(ActorId, OrganizationId, orderId, false);

        public GetRouteFinancialsQuery RouteQuery(Guid routeId) => new(ActorId, OrganizationId, routeId, false);

        public RecordCodCollectionCommand DriverRecord(Guid orderId, long amountCents, string key) =>
            Record(orderId, amountCents, key) with { ActorId = DriverUserId };

        public ReconcileCodCommand DriverReconcile(Guid codId, string key) =>
            Reconcile(codId, key) with { ActorId = DriverUserId };

        public GetOrderFinancialsQuery DriverOrderQuery(Guid orderId) =>
            OrderQuery(orderId) with { ActorId = DriverUserId };

        public RecordCodCollectionCommand ReplacementDriverRecord(Guid orderId, long amountCents, string key) =>
            Record(orderId, amountCents, key) with { ActorId = UnassignedDriverUserId };

        public Task CreateReplacementDriverAsync() => ExecuteAsync(
            """
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
            """,
            P("driver", ReplacementDriverId),
            P("user", UnassignedDriverUserId),
            P("org", OrganizationId),
            P("city", scenario.CityId),
            P("created", Now.AddDays(-1)));

        /// <summary>
        /// What dispatch does to replace an assignment — lock the order row, cancel the active assignment,
        /// create the replacement driver's — inside a transaction the caller keeps open. Returns the backend
        /// that holds the order row lock.
        /// </summary>
        public async Task<int> ReplaceAssignmentUnderOrderLockAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid orderId)
        {
            int backend;
            await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid();", connection, transaction))
            {
                backend = (int)(await pid.ExecuteScalarAsync())!;
            }

            await using var replace = new NpgsqlCommand(
                """
                SELECT id FROM orders.orders WHERE id=@order FOR UPDATE;
                UPDATE dispatch.assignments SET status='CANCELLED',route_id=NULL
                WHERE order_id=@order AND status IN ('ACCEPTED','ACTIVE');
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,route_id,assignment_type,status,cost_cents,
                  accepted_at,created_at)
                VALUES (gen_random_uuid(),@order,@org,@driver,NULL,'OWN','ACTIVE',3000,@now,@now);
                """,
                connection,
                transaction);
            replace.Parameters.Add(P("order", orderId));
            replace.Parameters.Add(P("org", OrganizationId));
            replace.Parameters.Add(P("driver", ReplacementDriverId));
            replace.Parameters.Add(P("now", Now));
            await replace.ExecuteNonQueryAsync();
            return backend;
        }

        /// <summary>How many idempotency records exist for the key, and the response status completed on it.</summary>
        public async Task<(long Count, int? Status)> IdempotencyRecordAsync(string logicalKey)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT count(*),max(response_status)
                FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND idempotency_key=@key;
                """);
            command.Parameters.AddWithValue("org", OrganizationId);
            command.Parameters.AddWithValue("key", Key(logicalKey));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt32(1));
        }

        private static string Key(string logicalKey) => $"fin001-contract-{logicalKey}";

        public Task SetOrderStatusAsync(Guid orderId, string status) => ExecuteAsync(
            "UPDATE orders.orders SET status=@status WHERE id=@order;",
            P("status", status),
            P("order", orderId));

        public Task SetActorRoleAsync(string role) => ExecuteAsync(
            """
            UPDATE organizations.organization_memberships
            SET role=@role
            WHERE user_id=@actor AND organization_id=@org;
            """,
            P("role", role),
            P("actor", ActorId),
            P("org", OrganizationId));

        public Task CancelAssignmentAsync(Guid orderId) => ExecuteAsync(
            "UPDATE dispatch.assignments SET status='CANCELLED',route_id=NULL WHERE order_id=@order;",
            P("order", orderId));

        public async Task<Guid?> CollectingDriverAsync(Guid orderId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT collected_by_driver_id FROM finance.cod_transactions WHERE order_id=@order;");
            command.Parameters.AddWithValue("order", orderId);
            return await command.ExecuteScalarAsync() is Guid driverId ? driverId : null;
        }

        public async Task<string?> CodStatusAsync(Guid codId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT status FROM finance.cod_transactions WHERE id=@cod;");
            command.Parameters.AddWithValue("cod", codId);
            return await command.ExecuteScalarAsync() as string;
        }

        public async Task<long> CodRowCountAsync(Guid orderId)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT count(*) FROM finance.cod_transactions WHERE order_id=@order;");
            command.Parameters.AddWithValue("order", orderId);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task<(long Audits, long FinanceOutbox)> CodEvidenceAsync()
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM platform.audit_logs
                    WHERE org_id=@org AND entity_type='CodTransaction'),
                  (SELECT count(*) FROM platform.outbox_events
                    WHERE owner_org_id=@org AND topic LIKE 'finance.%');
                """);
            command.Parameters.AddWithValue("org", OrganizationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        public Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters) =>
            scenario.ExecuteAdminAsync(sql, parameters);

        public async ValueTask DisposeAsync()
        {
            await ExecuteAsync(
                """
                DELETE FROM finance.cod_transactions WHERE owner_org_id=@org OR operator_org_id=@org;
                DELETE FROM routes.route_stops WHERE operator_org_id=@org;
                DELETE FROM dispatch.assignments WHERE owner_org_id=@org;
                DELETE FROM routes.routes WHERE operator_org_id=@org;
                DELETE FROM drivers.driver_profiles WHERE id IN (@driver,@replacement_driver);
                DELETE FROM organizations.organization_memberships
                  WHERE user_id IN (@driver_user,@other_driver_user);
                """,
                P("org", OrganizationId),
                P("driver", DriverId),
                P("replacement_driver", ReplacementDriverId),
                P("driver_user", DriverUserId),
                P("other_driver_user", UnassignedDriverUserId));

            // platform.audit_logs.actor_id references identity.users, and a driver-recorded collection
            // leaves an audit row owned by the driver, so the driver users can only go once the base
            // scenario has removed this organization's append-only audit rows with the migrator role.
            await scenario.DisposeAsync();
            await ExecuteAsync(
                "DELETE FROM identity.users WHERE id IN (@driver_user,@other_driver_user);",
                P("driver_user", DriverUserId),
                P("other_driver_user", UnassignedDriverUserId));
        }

        private static NpgsqlParameter P(string name, object value) => new(name, value);
    }
}
