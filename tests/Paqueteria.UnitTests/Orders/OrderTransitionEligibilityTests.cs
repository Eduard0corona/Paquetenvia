using Drivers.Application.Eligibility;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

public sealed class OrderTransitionEligibilityTests
{
    private static readonly Guid OrganizationId = Guid.Parse("0b3a0000-0000-4000-8000-000000000001");
    private static readonly Guid DriverId = Guid.Parse("0b3a0000-0000-4000-8000-000000000002");
    private static readonly Guid CityId = Guid.Parse("0b3a0000-0000-4000-8000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_vehicle_without_a_configured_capacity_fails_capacity_and_not_driver_eligibility()
    {
        var (eligibleDriver, capacityAvailable) = Evaluate(vehicleCapacityConfigured: false);

        Assert.True(eligibleDriver);
        Assert.False(capacityAvailable);
    }

    [Fact]
    public void A_vehicle_with_a_configured_capacity_passes_both_guards()
    {
        var (eligibleDriver, capacityAvailable) = Evaluate(vehicleCapacityConfigured: true);

        Assert.True(eligibleDriver);
        Assert.True(capacityAvailable);
    }

    private static (bool EligibleDriver, bool CapacityAvailable) Evaluate(bool vehicleCapacityConfigured)
    {
        var capacity = new Dictionary<string, VehicleCapacityLimits>(StringComparer.Ordinal);
        if (vehicleCapacityConfigured)
        {
            capacity["MOTORCYCLE"] = new VehicleCapacityLimits(4, 4_000, 2_000, 500, 400, 300, true);
        }

        var policy = new DriverEligibilityPolicyConfiguration(
            "ord-002-unit-v1",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["MOTORCYCLE"] = ["IDENTITY"],
            },
            new HashSet<string>(StringComparer.Ordinal),
            capacity);
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
            });

        return OrderTransitionEligibility.Evaluate(
            "OWN",
            OrganizationId,
            DriverId,
            CityId,
            null,
            new DriverCapacityRequirement(1, 500, 500, 100, 80, 60),
            Now,
            snapshot,
            policy);
    }
}
