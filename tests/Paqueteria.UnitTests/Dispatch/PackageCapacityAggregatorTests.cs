using Dispatch.Application.Assignments;
using Drivers.Application.Eligibility;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class PackageCapacityAggregatorTests
{
    [Fact]
    public void Aggregates_count_total_maximum_weight_and_keeps_each_package_sides_as_captured()
    {
        var packages = new[]
        {
            new PackageCapacityItem(500, 100, null, 50),
            new PackageCapacityItem(700, 200, 300, 40),
        };

        Assert.True(PackageCapacityAggregator.TryAggregate(packages, out var result));
        Assert.NotNull(result);
        Assert.Equal(2, result.PackageCount);
        Assert.Equal(1200, result.TotalWeightGrams);
        Assert.Equal(700, result.MaximumSinglePackageWeightGrams);
        // DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20: no maximum per field across packages; the policy sorts each
        // package on its own.
        Assert.Equal(new PackageSides[] { new(100, null, 50), new(200, 300, 40) }, result.Packages);
    }

    [Fact]
    public void A_package_without_measurements_keeps_its_missing_sides()
    {
        Assert.True(PackageCapacityAggregator.TryAggregate(
            [new PackageCapacityItem(1, null, null, null)],
            out var result));
        Assert.Equal(new PackageSides[] { new(null, null, null) }, result!.Packages);
    }

    [Theory]
    [MemberData(nameof(InvalidPackages))]
    public void Invalid_packages_fail_closed(IReadOnlyList<PackageCapacityItem> packages)
    {
        Assert.False(PackageCapacityAggregator.TryAggregate(packages, out var result));
        Assert.Null(result);
    }

    public static TheoryData<IReadOnlyList<PackageCapacityItem>> InvalidPackages() => new()
    {
        Array.Empty<PackageCapacityItem>(),
        new[] { new PackageCapacityItem(0, null, null, null) },
        new[] { new PackageCapacityItem(-1, null, null, null) },
        new[] { new PackageCapacityItem(1, 0, null, null) },
        new[]
        {
            new PackageCapacityItem(long.MaxValue, null, null, null),
            new PackageCapacityItem(1, null, null, null),
        },
    };
}
