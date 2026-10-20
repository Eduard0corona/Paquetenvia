using Drivers.Application.Eligibility;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// The Orders reader behind the ORD-002 <c>eligible_driver</c> and <c>capacity_available</c> guards (towards
/// <c>ASSIGNED</c> and on re-delivery).
/// </summary>
public sealed class OrderPackageDimensionsReaderTests : PackageDimensionsReaderContract
{
    private const string NoMeasurements = """{"length_mm":null,"width_mm":null,"height_mm":null}""";
    private static readonly Guid OrganizationId = Guid.Parse("0b3b0000-0000-4000-8000-000000000001");
    private static readonly Guid DriverId = Guid.Parse("0b3b0000-0000-4000-8000-000000000002");
    private static readonly Guid CityId = Guid.Parse("0b3b0000-0000-4000-8000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    protected override bool TryRead(string json, out int? length, out int? width, out int? height) =>
        OrderTransitionEligibility.TryReadDimensions(json, out length, out width, out height);

    [Fact]
    public void A_package_stored_without_measurements_is_a_valid_requirement()
    {
        var requirement = OrderTransitionEligibility.Aggregate([(500, NoMeasurements)]);

        Assert.Equal(new DriverCapacityRequirement(1, 500, 500, [new PackageSides(null, null, null)]), requirement);
        Assert.NotEqual(OrderTransitionEligibility.InvalidRequirement, requirement);
    }

    [Fact]
    public void Null_and_present_measurements_stay_with_their_package()
    {
        var requirement = OrderTransitionEligibility.Aggregate(
        [
            (500, NoMeasurements),
            (700, """{"length_mm":120,"width_mm":null,"height_mm":60}"""),
        ]);

        Assert.Equal(
            new DriverCapacityRequirement(
                2,
                1_200,
                700,
                [new PackageSides(null, null, null), new PackageSides(120, null, 60)]),
            requirement);
    }

    [Fact]
    public void A_package_typed_with_its_sides_in_another_order_attests_capacity()
    {
        // DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20: 300 x 400 x 500 fits the 500 x 400 x 300 limits turned.
        var requirement = OrderTransitionEligibility.Aggregate(
            [(500, """{"length_mm":300,"width_mm":400,"height_mm":500}""")]);

        Assert.Equal((true, true), Evaluate(requirement, requireDimensions: true));
        Assert.Equal(
            (true, false),
            Evaluate(
                OrderTransitionEligibility.Aggregate([(500, """{"length_mm":300,"width_mm":400,"height_mm":501}""")]),
                requireDimensions: true));
    }

    [Fact]
    public void An_invalid_measurement_next_to_a_package_without_measurements_still_fails_closed()
    {
        Assert.Equal(
            OrderTransitionEligibility.InvalidRequirement,
            OrderTransitionEligibility.Aggregate(
            [
                (500, NoMeasurements),
                (700, """{"length_mm":"120","width_mm":null,"height_mm":60}"""),
            ]));
    }

    [Fact]
    public void A_package_without_measurements_attests_capacity_unless_the_vehicle_requires_dimensions()
    {
        var requirement = OrderTransitionEligibility.Aggregate([(500, NoMeasurements)]);

        Assert.Equal((true, true), Evaluate(requirement, requireDimensions: false));
        // The capacity rule is unchanged: a vehicle that requires dimensions refuses a package without them as a
        // capacity failure, exactly as it refuses a package whose keys are missing.
        Assert.Equal((true, false), Evaluate(requirement, requireDimensions: true));
        Assert.Equal(
            Evaluate(OrderTransitionEligibility.Aggregate([(500, "{}")]), requireDimensions: true),
            Evaluate(requirement, requireDimensions: true));
    }

    private static (bool EligibleDriver, bool CapacityAvailable) Evaluate(
        DriverCapacityRequirement requirement,
        bool requireDimensions)
    {
        var policy = new DriverEligibilityPolicyConfiguration(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["MOTORCYCLE"] = ["IDENTITY"],
            },
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, VehicleCapacityLimits>(StringComparer.Ordinal)
            {
                ["MOTORCYCLE"] = new(4, 4_000, 2_000, 500, 400, 300, requireDimensions),
            });
        var snapshot = new DriverEligibilitySnapshot(
            DriverId,
            OrganizationId,
            Guid.NewGuid(),
            CityId,
            "OWN",
            "MOTORCYCLE",
            "ACTIVE",
            "ACTIVE",
            true,
            null,
            new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal)
            {
                ["IDENTITY"] = new("IDENTITY", "VALID", "synthetic/identity", new byte[32], Now.AddDays(30)),
            },
            "dimensions-null-unit-v1");

        return OrderTransitionEligibility.Evaluate(
            "OWN",
            OrganizationId,
            DriverId,
            CityId,
            null,
            requirement,
            Now,
            snapshot,
            policy);
    }
}
