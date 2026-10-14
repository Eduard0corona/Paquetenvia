using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orders.Application.Orders;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;
using Routing.Application.Routes;
using Routing.Infrastructure;
using Routing.Infrastructure.Persistence;
using Routing.Infrastructure.Routes;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// Pilot defect: an order created in "Nueva orden" without package measurements (the wizard marks them optional)
/// could not be assigned or routed. Pricing snapshots the empty measurements as JSON nulls, order creation stores
/// <c>{"length_mm":null,"width_mm":null,"height_mm":null}</c> in <c>orders.package_items.dimensions_mm</c>, and
/// the Dispatch, Routing and Orders readers took a present <c>null</c> for an invalid value instead of "no
/// measurement": the picker said PACKAGE_REQUIREMENT_INVALID and assignDriver answered CAPACITY_INSUFFICIENT.
/// Here the order goes through the real flow on PostgreSQL as the NOBYPASSRLS runtime role.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    private const string NoMeasurementsQuotePackages =
        """
        [{"description":"synthetic package without measurements","weight_grams":500,"declared_value_cents":0,
          "length_mm":null,"width_mm":null,"height_mm":null}]
        """;

    [PostgreSqlContractFact]
    public async Task An_order_created_from_a_quote_without_measurements_is_assignable_and_routable()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture, createOrder: false);
        try
        {
            var orderId = await CreateReadyOrderWithoutMeasurementsAsync(scenario);

            // The capacity rule is unchanged: a vehicle that requires dimensions still refuses a package without
            // them, with the dimension codes and not PACKAGE_REQUIREMENT_INVALID.
            var picker = PickerQuery(scenario) with { OrderId = orderId };
            var strict = Assert.Single((await CreateAssignableDriversQuery(EligibilityOptions(requireDimensions: true))
                .ListAsync(picker, default)).Items);
            Assert.False(strict.Eligible);
            Assert.Equal(
                ["PACKAGE_LENGTH_EXCEEDED", "PACKAGE_WIDTH_EXCEEDED", "PACKAGE_HEIGHT_EXCEEDED"],
                strict.IneligibilityReasons);
            var refused = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource, eligibility: EligibilityOptions(requireDimensions: true))
                    .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrderId = orderId }, default));
            Assert.Equal(AssignmentConflictCode.CapacityInsufficient, refused.Code);
            Assert.Equal(0L, await CountAssignmentsAsync(orderId));

            // listAssignableDrivers: the eligible OWN driver is listed as eligible.
            var eligibility = EligibilityOptions(requireDimensions: false);
            var listed = Assert.Single(
                (await CreateAssignableDriversQuery(eligibility).ListAsync(picker, default)).Items);
            Assert.Equal(scenario.DriverId, listed.DriverId);
            Assert.True(listed.Eligible);
            Assert.Empty(listed.IneligibilityReasons);

            // assignDriver accepts it.
            var assignment = await CreateAssignmentService(fixture.AppDataSource, eligibility: eligibility)
                .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrderId = orderId }, default);
            Assert.Equal(scenario.DriverId, assignment.DriverId);
            Assert.Equal(orderId, assignment.OrderId);
            Assert.Equal("ACCEPTED", assignment.Status);
            Assert.Equal(1L, await CountAssignmentsAsync(orderId));

            // The ORD-002 guards towards ASSIGNED read the same package as attesting capacity.
            await using (var tenant = await TenantTransaction.BeginAsync(
                fixture.AppDataSource, "paqueteria_app", scenario.DispatcherUserId, [scenario.OrganizationId]))
            {
                var guards = await new PostgreSqlOrderAssignmentGuardReader(
                        Options.Create(OrderTransitionEligibilityOptions()))
                    .ReadAsync(
                        tenant.Connection,
                        tenant.Transaction,
                        scenario.OrganizationId,
                        orderId,
                        scenario.CityId,
                        OccurredAt,
                        default);
                Assert.Equal(new AssignmentGuardSnapshot(true, true, true, true), guards);
                await tenant.RollbackAsync();
            }

            // addRouteStop: the order joins a route of its driver. A vehicle that requires dimensions still refuses.
            var routeService = CreateRouteService(requireDimensions: false);
            var route = await routeService.CreateAsync(
                new CreateRouteCommand(
                    scenario.DispatcherUserId,
                    scenario.OrganizationId,
                    Key(),
                    scenario.DriverId,
                    scenario.CityId,
                    null,
                    DateOnly.FromDateTime(OccurredAt.UtcDateTime.AddDays(1)),
                    false,
                    "dimensions-null-route"),
                default);
            var addStop = new AddRouteStopCommand(
                scenario.DispatcherUserId,
                scenario.OrganizationId,
                Key(),
                route.Id,
                orderId,
                route.Version,
                false,
                "dimensions-null-stop");
            var strictRoute = await Assert.ThrowsAsync<RoutingConflictException>(() =>
                CreateRouteService(requireDimensions: true)
                    .AddStopAsync(addStop with { IdempotencyKey = Key() }, default));
            Assert.Equal(RoutingConflictCode.DriverIneligible, strictRoute.Code);

            var routed = await routeService.AddStopAsync(addStop, default);
            Assert.Equal(orderId, Assert.Single(routed.Stops).OrderId);
            Assert.Equal(route.Version + 1, routed.Version);
            await using var linked = fixture.AdminDataSource.CreateCommand(
                "SELECT route_id FROM dispatch.assignments WHERE id=@assignment;");
            linked.Parameters.AddWithValue("assignment", assignment.Id);
            Assert.Equal(route.Id, await linked.ExecuteScalarAsync());
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM routes.route_stops WHERE operator_org_id=@org;
                UPDATE dispatch.assignments SET route_id=NULL WHERE owner_org_id=@org;
                DELETE FROM routes.routes WHERE operator_org_id=@org;
                """,
                P("org", scenario.OrganizationId));
        }
    }

    /// <summary>
    /// The external offers read the same package through the same Dispatch reader: before the fix, creating the
    /// offer answered INVALID_ORDER_STATE, listing skipped it and accepting answered DRIVER_INELIGIBLE.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task An_order_created_from_a_quote_without_measurements_can_be_offered_to_an_external_driver()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture, createOrder: false);
        try
        {
            var orderId = await CreateReadyOrderWithoutMeasurementsAsync(scenario);
            var driverUserId = Assert.Single(await scenario.CreateExternalDriversAsync(1));
            var eligibility = EligibilityOptions(requireDimensions: false);

            var offer = await CreateExternalOfferService(fixture.AppDataSource, eligibility: eligibility).CreateAsync(
                new CreateExternalOfferCommand(
                    scenario.DispatcherUserId,
                    scenario.OrganizationId,
                    Key(),
                    orderId,
                    12_345,
                    OccurredAt.AddHours(2),
                    new ExternalOfferConstraints(["MOTORCYCLE"], [], false),
                    false,
                    "dimensions-null-offer"),
                default);
            Assert.Equal(orderId, offer.OrderId);

            var page = await CreateExternalOfferService(fixture.AppDataSource, eligibility: eligibility)
                .ListEligibleAsync(driverUserId, scenario.OrganizationId, null, default);
            Assert.Contains(page.Items, item => item.Id == offer.Id);

            var accepted = await CreateExternalOfferService(fixture.AppDataSource, eligibility: eligibility)
                .AcceptAsync(
                    new AcceptExternalOfferCommand(
                        driverUserId,
                        scenario.OrganizationId,
                        Key(),
                        offer.Id,
                        "dimensions-null-accept"),
                    default);
            Assert.Equal(scenario.DriverId, accepted.DriverId);
            Assert.Equal(orderId, accepted.OrderId);
            Assert.Equal("ACCEPTED", accepted.Status);
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                "DELETE FROM dispatch.external_offers WHERE owner_org_id=@org;",
                P("org", scenario.OrganizationId));
        }
    }

    /// <summary>
    /// The pilot flow up to READY_FOR_PICKUP: the quote as Pricing stores it when the measurements are left empty
    /// (explicit JSON nulls), createOrder through the real coordinator, then DRAFT -> CONFIRMED -> READY_FOR_PICKUP
    /// through ORD-002.
    /// </summary>
    private async Task<Guid> CreateReadyOrderWithoutMeasurementsAsync(DispatchScenario scenario)
    {
        await scenario.ExecuteAdminAsync(
            "UPDATE pricing.quotes SET package_snapshot=@packages::jsonb WHERE id=@quote;",
            P("packages", NoMeasurementsQuotePackages),
            P("quote", scenario.QuoteId));

        var created = await CreateOrderService(OccurredAt.AddHours(-2)).CreateAsync(
            new CreateOrderCommand(
                scenario.DispatcherUserId,
                scenario.OrganizationId,
                Key(),
                scenario.QuoteId,
                "SENDER",
                new OrderAcceptanceInput(
                    "terms-synthetic-v1",
                    "privacy-synthetic-v1",
                    OccurredAt.AddHours(-3),
                    "WEB"),
                "dimensions-null-create",
                RestrictedGoodsAcknowledged: true),
            default);
        Assert.Equal("DRAFT", created.Status);
        Assert.True(await PackageItemsHaveNoMeasurementsAsync(created.Id));

        var transitions = CreateOrderTransitionService(OccurredAt.AddHours(-1));
        var confirmed = await transitions.TransitionAsync(
            TransitionCommand(scenario, created.Id, "CONFIRMED", created.Version,
                """{"restricted_goods_acknowledged":true}"""),
            default);
        var ready = await transitions.TransitionAsync(
            TransitionCommand(scenario, created.Id, "READY_FOR_PICKUP", confirmed.Version, null),
            default);
        Assert.Equal("READY_FOR_PICKUP", ready.Status);
        return created.Id;
    }

    /// <summary>What order creation stored, compared as jsonb: every measurement a JSON null.</summary>
    private async Task<bool> PackageItemsHaveNoMeasurementsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT count(*)=1 AND bool_and(
              dimensions_mm='{"length_mm":null,"width_mm":null,"height_mm":null}'::jsonb)
            FROM orders.package_items WHERE order_id=@order;
            """);
        command.Parameters.AddWithValue("order", orderId);
        return await command.ExecuteScalarAsync() is true;
    }

    private static TransitionOrderCommand TransitionCommand(
        DispatchScenario scenario,
        Guid orderId,
        string target,
        int expectedVersion,
        string? metadata) => new(
        scenario.DispatcherUserId,
        scenario.OrganizationId,
        Key(),
        orderId,
        target,
        "synthetic reason",
        expectedVersion,
        metadata,
        false,
        "dimensions-null-transition");

    private IOrderService CreateOrderService(DateTimeOffset now)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(dbOptions, state);
        return new QuoteSnapshotToOrderCoordinator(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new CryptographicOrderPublicIdGenerator(),
            new NoOpOrderCreationFailureInjector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            Options.Create(OrdersRuntimeOptions()),
            new FixedClock(now));
    }

    private IOrderTransitionService CreateOrderTransitionService(DateTimeOffset now)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(dbOptions, state);
        return new PostgreSqlOrderTransitionService(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new PostgreSqlOrderTransitionAuthorizationReader(),
            new PostgreSqlOrderTransitionReplayAuthorizationReader(),
            new PostgreSqlOrderQuoteAcceptanceGuardReader(),
            new PostgreSqlOrderAssignmentGuardReader(Options.Create(OrderTransitionEligibilityOptions())),
            new PostgreSqlOrderProofGuardReader(),
            new PostgreSqlOrderCustodyGuardReader(),
            new PostgreSqlOrderIncidentGuardReader(),
            new PostgreSqlOrderCodGuardReader(),
            new OrderTransitionAuthorizer(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new NoOpOrderTransitionFailureInjector(),
            Options.Create(OrdersRuntimeOptions()),
            new FixedClock(now));
    }

    private PostgreSqlRouteService CreateRouteService(bool requireDimensions)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<RoutingDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new RoutingDbContext(dbOptions, state);
        return new PostgreSqlRouteService(
            new TenantTransactionContext<RoutingDbContext>(context, state),
            Options.Create(new RoutingOptions { Provider = RoutingProviderKind.PostgreSql }),
            Options.Create(new RoutingDriverEligibilityOptions
            {
                RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal) { ["MOTORCYCLE"] = ["IDENTITY"] },
                VehicleCapacity = new(StringComparer.Ordinal)
                {
                    ["MOTORCYCLE"] = new()
                    {
                        MaximumPackageCount = 4,
                        MaximumTotalWeightGrams = 4_000,
                        MaximumSinglePackageWeightGrams = 2_000,
                        MaximumLengthMillimeters = 500,
                        MaximumWidthMillimeters = 400,
                        MaximumHeightMillimeters = 300,
                        RequireDimensions = requireDimensions,
                    },
                },
            }),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new NoOpRoutingFailureInjector(),
            new FixedClock(OccurredAt.AddMinutes(30)));
    }

    private static OrdersOptions OrdersRuntimeOptions() => new()
    {
        Provider = OrdersProviderKind.PostgreSql,
        CommandTimeoutSeconds = 30,
        IdempotencyLifetimeMinutes = 60,
        PublicIdCollisionRetryCount = 2,
        ClaimWindowHours = 72,
        TransitionMetadataMaximumBytes = 4_096,
    };

    private static OrderTransitionDriverEligibilityOptions OrderTransitionEligibilityOptions() => new()
    {
        RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal) { ["MOTORCYCLE"] = ["IDENTITY"] },
        VehicleCapacity = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = new()
            {
                MaximumPackageCount = 4,
                MaximumTotalWeightGrams = 4_000,
                MaximumSinglePackageWeightGrams = 2_000,
                MaximumLengthMillimeters = 500,
                MaximumWidthMillimeters = 400,
                MaximumHeightMillimeters = 300,
                RequireDimensions = false,
            },
        },
    };
}
