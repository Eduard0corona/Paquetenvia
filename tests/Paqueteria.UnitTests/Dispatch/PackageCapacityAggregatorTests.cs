using Dispatch.Application.Assignments;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class PackageCapacityAggregatorTests
{
    [Fact]
    public void Aggregates_count_total_maximum_weight_and_dimensions()
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
        Assert.Equal(200, result.MaximumLengthMillimeters);
        Assert.Equal(300, result.MaximumWidthMillimeters);
        Assert.Equal(50, result.MaximumHeightMillimeters);
    }

    [Fact]
    public void Missing_dimension_remains_null_when_no_package_supplies_it()
    {
        Assert.True(PackageCapacityAggregator.TryAggregate(
            [new PackageCapacityItem(1, null, null, null)],
            out var result));
        Assert.Null(result!.MaximumLengthMillimeters);
        Assert.Null(result.MaximumWidthMillimeters);
        Assert.Null(result.MaximumHeightMillimeters);
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
