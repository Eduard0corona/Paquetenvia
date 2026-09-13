using Drivers.Application.Eligibility;

namespace Routing.Infrastructure;

public enum RoutingProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class RoutingOptions
{
    public const string SectionName = "Routing";

    public RoutingProviderKind Provider { get; set; } = RoutingProviderKind.Disabled;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public int IdempotencyLifetimeMinutes { get; set; } = 1440;
}

public sealed class RoutingDriverEligibilityOptions
{
    public string PolicyVersion { get; set; } = "synthetic-v1";
    public Dictionary<string, List<string>> RequiredDocumentTypesByVehicleType { get; set; } =
        new(StringComparer.Ordinal);
    public List<string> NonExpiringDocumentTypes { get; set; } = [];
    public Dictionary<string, RoutingVehicleCapacityOptions> VehicleCapacity { get; set; } =
        new(StringComparer.Ordinal);

    public DriverEligibilityPolicyConfiguration ToPolicy() => new(
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

public sealed class RoutingVehicleCapacityOptions
{
    public int MaximumPackageCount { get; set; }
    public long MaximumTotalWeightGrams { get; set; }
    public long MaximumSinglePackageWeightGrams { get; set; }
    public int MaximumLengthMillimeters { get; set; }
    public int MaximumWidthMillimeters { get; set; }
    public int MaximumHeightMillimeters { get; set; }
    public bool RequireDimensions { get; set; }

    public VehicleCapacityLimits ToLimits() => new(
        MaximumPackageCount,
        MaximumTotalWeightGrams,
        MaximumSinglePackageWeightGrams,
        MaximumLengthMillimeters,
        MaximumWidthMillimeters,
        MaximumHeightMillimeters,
        RequireDimensions);
}
