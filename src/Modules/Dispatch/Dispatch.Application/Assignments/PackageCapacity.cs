using Drivers.Application.Eligibility;

namespace Dispatch.Application.Assignments;

public sealed record PackageCapacityItem(
    long WeightGrams,
    int? LengthMillimeters,
    int? WidthMillimeters,
    int? HeightMillimeters);

public static class PackageCapacityAggregator
{
    public static bool TryAggregate(
        IReadOnlyList<PackageCapacityItem> packages,
        out DriverCapacityRequirement? requirement)
    {
        requirement = null;
        if (packages.Count == 0)
        {
            return false;
        }

        try
        {
            long totalWeight = 0;
            long maximumWeight = 0;
            var sides = new List<PackageSides>(packages.Count);

            foreach (var package in packages)
            {
                if (package.WeightGrams <= 0 ||
                    package.LengthMillimeters is <= 0 ||
                    package.WidthMillimeters is <= 0 ||
                    package.HeightMillimeters is <= 0)
                {
                    return false;
                }

                totalWeight = checked(totalWeight + package.WeightGrams);
                maximumWeight = Math.Max(maximumWeight, package.WeightGrams);
                // Each package keeps its own sides, unsorted: DriverEligibilityPolicy sorts them
                // (DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20).
                sides.Add(new PackageSides(
                    package.LengthMillimeters,
                    package.WidthMillimeters,
                    package.HeightMillimeters));
            }

            requirement = new DriverCapacityRequirement(
                packages.Count,
                totalWeight,
                maximumWeight,
                sides);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
