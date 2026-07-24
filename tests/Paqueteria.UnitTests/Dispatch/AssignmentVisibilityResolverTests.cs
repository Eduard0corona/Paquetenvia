using System.Data.Common;
using Dispatch.Application.Assignments;
using Drivers.Application.Eligibility;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class AssignmentVisibilityResolverTests
{
    [Theory]
    [InlineData("missing_order", false, true)]
    [InlineData("cross_tenant_order", false, true)]
    [InlineData("missing_driver", true, false)]
    [InlineData("cross_tenant_driver", true, false)]
    public async Task Every_not_found_case_executes_the_same_structural_plan(
        string _,
        bool orderVisible,
        bool driverVisible)
    {
        var reader = new RecordingVisibilityDataReader(orderVisible, driverVisible);
        var resolver = new DispatchAssignmentVisibilityResolver(reader);

        var result = await resolver.ResolveAsync(
            null!,
            null!,
            Command(),
            default);

        Assert.False(result.IsVisible);
        Assert.Equal(
            DispatchAssignmentVisibilityResolver.StructuralPlan,
            reader.Calls);
        Assert.Equal(orderVisible ? RecordingVisibilityDataReader.CityId : Guid.Empty, reader.ObservedCityId);
    }

    [Fact]
    public async Task Visible_resources_use_the_same_plan_and_return_both_snapshots()
    {
        var reader = new RecordingVisibilityDataReader(orderVisible: true, driverVisible: true);
        var resolver = new DispatchAssignmentVisibilityResolver(reader);

        var result = await resolver.ResolveAsync(null!, null!, Command(), default);

        Assert.True(result.IsVisible);
        Assert.NotNull(result.Order);
        Assert.NotNull(result.Driver);
        Assert.Single(result.Packages);
        Assert.Equal(DispatchAssignmentVisibilityResolver.StructuralPlan, reader.Calls);
    }

    private static CreateOwnDriverAssignmentCommand Command() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "dsp-002-visibility-unit",
        Guid.NewGuid(),
        Guid.NewGuid(),
        "OWN",
        0,
        null,
        false,
        null);

    private sealed class RecordingVisibilityDataReader(
        bool orderVisible,
        bool driverVisible) : IAssignmentVisibilityDataReader
    {
        public static readonly Guid CityId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        public List<string> Calls { get; } = [];
        public Guid? ObservedCityId { get; private set; }

        public Task<AssignmentOrderVisibilityData> ReadOrderAndPackagesAsync(
            DbConnection connection,
            DbTransaction transaction,
            Guid organizationId,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            Calls.Add("order_packages");
            AssignmentVisibilityOrder? order = orderVisible
                ? new(orderId, organizationId, null, CityId, null, "READY_FOR_PICKUP", 1)
                : null;
            return Task.FromResult(new AssignmentOrderVisibilityData(
                order,
                [new AssignmentVisibilityPackage(500, """{"length_mm":100}""")]));
        }

        public Task<DriverEligibilitySnapshot?> ReadDriverProfileAndDocumentsAsync(
            DbConnection connection,
            DbTransaction transaction,
            Guid organizationId,
            Guid driverId,
            Guid cityId,
            Guid? serviceAreaId,
            CancellationToken cancellationToken)
        {
            Calls.Add("driver_profile_documents");
            ObservedCityId = cityId;
            DriverEligibilitySnapshot? driver = driverVisible
                ? new(
                    driverId,
                    organizationId,
                    Guid.NewGuid(),
                    CityId,
                    "OWN",
                    "MOTORCYCLE",
                    "ACTIVE",
                    "ACTIVE",
                    true,
                    null,
                    new Dictionary<string, DriverDocumentSnapshot>())
                : null;
            return Task.FromResult(driver);
        }
    }
}
