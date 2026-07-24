using Drivers.Application.Eligibility;

namespace Drivers.Infrastructure;

public enum DriversProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class DriversOptions
{
    public const string SectionName = "Drivers";

    public DriversProviderKind Provider { get; set; } = DriversProviderKind.Disabled;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public DriverEligibilityOptions Eligibility { get; set; } = new();
}

public sealed class DriverEligibilityOptions
{
    public string PolicyVersion { get; set; } = "synthetic-v1";
    public Dictionary<string, List<string>> RequiredDocumentTypesByVehicleType { get; set; } =
        new(StringComparer.Ordinal);
    public List<string> NonExpiringDocumentTypes { get; set; } = [];
    public Dictionary<string, VehicleCapacityOptions> VehicleCapacity { get; set; } =
        new(StringComparer.Ordinal);

    internal DriverEligibilityPolicyConfiguration ToPolicy() => new(
        PolicyVersion,
        RequiredDocumentTypesByVehicleType.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal),
        NonExpiringDocumentTypes.ToHashSet(StringComparer.Ordinal),
        VehicleCapacity.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToLimits(),
            StringComparer.Ordinal));
}

public sealed class VehicleCapacityOptions
{
    public int MaximumPackageCount { get; set; }
    public long MaximumTotalWeightGrams { get; set; }
    public long MaximumSinglePackageWeightGrams { get; set; }
    public int MaximumLengthMillimeters { get; set; }
    public int MaximumWidthMillimeters { get; set; }
    public int MaximumHeightMillimeters { get; set; }
    public bool RequireDimensions { get; set; }

    internal VehicleCapacityLimits ToLimits() => new(
        MaximumPackageCount,
        MaximumTotalWeightGrams,
        MaximumSinglePackageWeightGrams,
        MaximumLengthMillimeters,
        MaximumWidthMillimeters,
        MaximumHeightMillimeters,
        RequireDimensions);
}
