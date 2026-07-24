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
            int? maximumLength = null;
            int? maximumWidth = null;
            int? maximumHeight = null;

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
                maximumLength = Max(maximumLength, package.LengthMillimeters);
                maximumWidth = Max(maximumWidth, package.WidthMillimeters);
                maximumHeight = Max(maximumHeight, package.HeightMillimeters);
            }

            requirement = new DriverCapacityRequirement(
                packages.Count,
                totalWeight,
                maximumWeight,
                maximumLength,
                maximumWidth,
                maximumHeight);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static int? Max(int? current, int? value) =>
        value is null ? current : current is null ? value : Math.Max(current.Value, value.Value);
}
