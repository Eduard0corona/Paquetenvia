using Dispatch.Application.Assignments;
using Dispatch.Infrastructure;
using Microsoft.Extensions.Options;
using Orders.Application.Orders;
using Orders.Infrastructure.Orders;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Routing.Application.Routes;
using Routing.Infrastructure;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20 (owner, H4: "Que el sistema las ordene"): every caller of the shared
/// DriverEligibilityPolicy passes each package's own sides, and the policy sorts them and the vehicle's limits from
/// largest to smallest. Here an order whose two packages were typed with their sides in different boxes goes through
/// the real readers on PostgreSQL as the NOBYPASSRLS runtime role: the picker and assignDriver (Dispatch), the ORD-002
/// <c>capacity_available</c> guard reader (Orders) and addRouteStop (Routing).
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    /// <summary>
    /// 300 x 450 x 200 and 200 x 300 x 450 mm. Compared box by box against 500 x 400 x 300, the largest width (450) and
    /// height (450) across the packages exceed their limits; turned, each package is 450 x 300 x 200 and fits.
    /// </summary>
    private const string SidewaysQuotePackages =
        """
        [{"description":"synthetic package typed sideways","weight_grams":500,"declared_value_cents":0,
          "length_mm":300,"width_mm":450,"height_mm":200},
         {"description":"synthetic package typed upright","weight_grams":500,"declared_value_cents":0,
          "length_mm":200,"width_mm":300,"height_mm":450}]
        """;

    [PostgreSqlContractFact]
    public async Task An_order_whose_packages_were_typed_sideways_is_judged_by_whether_each_package_fits()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture, createOrder: false);
        try
        {
            var orderId = await CreateReadyOrderFromQuotePackagesAsync(scenario, SidewaysQuotePackages);
            var picker = PickerQuery(scenario) with { OrderId = orderId };

            // Limits written 199 x 450 x 300 sort to 450 x 300 x 199: only the smallest side (200) does not fit, and
            // it reports the code of the smallest place.
            var strict = SortedDimensionsDispatchOptions(199, 450, 300);
            var tight = Assert.Single(
                (await CreateAssignableDriversQuery(strict).ListAsync(picker, default)).Items);
            Assert.False(tight.Eligible);
            Assert.Equal(["PACKAGE_HEIGHT_EXCEEDED"], tight.IneligibilityReasons);
            var refused = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource, eligibility: strict)
                    .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrderId = orderId }, default));
            Assert.Equal(AssignmentConflictCode.CapacityInsufficient, refused.Code);
            Assert.Equal(0L, await CountAssignmentsAsync(orderId));

            // 500 x 400 x 300: each package fits turned, so the picker lists the driver as eligible and assignDriver
            // accepts.
            var loose = SortedDimensionsDispatchOptions(500, 400, 300);
            var listed = Assert.Single((await CreateAssignableDriversQuery(loose).ListAsync(picker, default)).Items);
            Assert.True(listed.Eligible);
            Assert.Empty(listed.IneligibilityReasons);
            var assignment = await CreateAssignmentService(fixture.AppDataSource, eligibility: loose)
                .CreateOwnDriverAssignmentAsync(Command(scenario) with { OrderId = orderId }, default);
            Assert.Equal("ACCEPTED", assignment.Status);

            // ORD-002 capacity_available reads the same packages.
            Assert.Equal(
                new AssignmentGuardSnapshot(true, true, true, true),
                await ReadAssignmentGuardsAsync(scenario, orderId, SortedDimensionsOrderOptions(300, 500, 400)));
            Assert.Equal(
                new AssignmentGuardSnapshot(true, true, false, true),
                await ReadAssignmentGuardsAsync(scenario, orderId, SortedDimensionsOrderOptions(450, 199, 300)));

            // addRouteStop evaluates the same order.
            var route = await CreateRouteService(SortedDimensionsRoutingOptions(500, 400, 300)).CreateAsync(
                new CreateRouteCommand(
                    scenario.DispatcherUserId,
                    scenario.OrganizationId,
                    Key(),
                    scenario.DriverId,
                    scenario.CityId,
                    null,
                    DateOnly.FromDateTime(OccurredAt.UtcDateTime.AddDays(1)),
                    false,
                    "sorted-dimensions-route"),
                default);
            var addStop = new AddRouteStopCommand(
                scenario.DispatcherUserId,
                scenario.OrganizationId,
                Key(),
                route.Id,
                orderId,
                route.Version,
                false,
                "sorted-dimensions-stop");
            var tightRoute = await Assert.ThrowsAsync<RoutingConflictException>(() =>
                CreateRouteService(SortedDimensionsRoutingOptions(300, 199, 450))
                    .AddStopAsync(addStop with { IdempotencyKey = Key() }, default));
            Assert.Equal(RoutingConflictCode.DriverIneligible, tightRoute.Code);
            var routed = await CreateRouteService(SortedDimensionsRoutingOptions(400, 300, 500))
                .AddStopAsync(addStop, default);
            Assert.Equal(orderId, Assert.Single(routed.Stops).OrderId);
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

    private async Task<AssignmentGuardSnapshot> ReadAssignmentGuardsAsync(
        DispatchScenario scenario,
        Guid orderId,
        OrderTransitionDriverEligibilityOptions eligibility)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", scenario.DispatcherUserId, [scenario.OrganizationId]);
        var guards = await new PostgreSqlOrderAssignmentGuardReader(Options.Create(eligibility)).ReadAsync(
            tenant.Connection,
            tenant.Transaction,
            scenario.OrganizationId,
            orderId,
            scenario.CityId,
            OccurredAt,
            default);
        await tenant.RollbackAsync();
        return guards;
    }

    private static DispatchDriverEligibilityOptions SortedDimensionsDispatchOptions(int length, int width, int height)
    {
        var options = EligibilityOptions(requireDimensions: true);
        var motorcycle = options.VehicleCapacity["MOTORCYCLE"];
        motorcycle.MaximumLengthMillimeters = length;
        motorcycle.MaximumWidthMillimeters = width;
        motorcycle.MaximumHeightMillimeters = height;
        return options;
    }

    private static OrderTransitionDriverEligibilityOptions SortedDimensionsOrderOptions(
        int length,
        int width,
        int height) =>
        OrderTransitionEligibilityOptions(new OrderTransitionVehicleCapacityOptions
        {
            MaximumPackageCount = 4,
            MaximumTotalWeightGrams = 4_000,
            MaximumSinglePackageWeightGrams = 2_000,
            MaximumLengthMillimeters = length,
            MaximumWidthMillimeters = width,
            MaximumHeightMillimeters = height,
            RequireDimensions = true,
        });

    private static RoutingVehicleCapacityOptions SortedDimensionsRoutingOptions(int length, int width, int height) =>
        new()
        {
            MaximumPackageCount = 4,
            MaximumTotalWeightGrams = 4_000,
            MaximumSinglePackageWeightGrams = 2_000,
            MaximumLengthMillimeters = length,
            MaximumWidthMillimeters = width,
            MaximumHeightMillimeters = height,
            RequireDimensions = true,
        };
}
