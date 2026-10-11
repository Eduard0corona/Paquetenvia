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
    public DriverLocationTelemetryOptions LocationTelemetry { get; set; } = new();

    /// <summary>VOICE-001-MASKED-CALLS-2026-10-11: rate limits and idempotency of recipient calls.</summary>
    public RecipientCallOptions RecipientCalls { get; set; } = new();
}

/// <summary>
/// <c>Drivers:RecipientCalls</c>. Only requests that may have placed a call count (REQUESTED, PLACED, UNCONFIRMED).
/// </summary>
public sealed class RecipientCallOptions
{
    /// <summary>Calls one driver may request for one order inside <see cref="OrderWindowMinutes"/>.</summary>
    public int MaximumPerOrder { get; set; } = 3;

    public int OrderWindowMinutes { get; set; } = 15;

    /// <summary>Calls one driver may request in any hour, every order together (bounds the cost of one driver).</summary>
    public int MaximumPerDriverPerHour { get; set; } = 20;

    public int IdempotencyLifetimeMinutes { get; set; } = 1440;

    public bool IsValid() =>
        MaximumPerOrder is >= 1 and <= 20 &&
        OrderWindowMinutes is >= 1 and <= 60 &&
        MaximumPerDriverPerHour is >= 1 and <= 200 &&
        MaximumPerDriverPerHour >= MaximumPerOrder &&
        IdempotencyLifetimeMinutes is >= 60 and <= 10_080;
}

public sealed class DriverLocationTelemetryOptions
{
    public int MinimumPublishIntervalSeconds { get; set; } = 10;
    public double MinimumPublishDistanceMeters { get; set; } = 25d;
    public int MaximumSilenceSeconds { get; set; } = 60;

    internal Drivers.Domain.Location.DriverLocationPublicationOptions ToPolicy() => new(
        TimeSpan.FromSeconds(MinimumPublishIntervalSeconds),
        MinimumPublishDistanceMeters,
        TimeSpan.FromSeconds(MaximumSilenceSeconds));

    public bool IsValid() =>
        MinimumPublishIntervalSeconds is >= 1 and <= 300 &&
        double.IsFinite(MinimumPublishDistanceMeters) &&
        MinimumPublishDistanceMeters is > 0d and <= 1000d &&
        MaximumSilenceSeconds >= MinimumPublishIntervalSeconds &&
        MaximumSilenceSeconds <= 3600;
}

public sealed class DriverEligibilityOptions
{
    // POLICY-VERSIONS-PER-ORG-2026-10-02: there is no global eligibility policy version any more; each
    // organization versions its own policy (organizations.organizations.driver_eligibility_policy_version)
    // and it arrives with the driver snapshot.
    public Dictionary<string, List<string>> RequiredDocumentTypesByVehicleType { get; set; } =
        new(StringComparer.Ordinal);
    public List<string> NonExpiringDocumentTypes { get; set; } = [];
    public Dictionary<string, VehicleCapacityOptions> VehicleCapacity { get; set; } =
        new(StringComparer.Ordinal);

    internal DriverEligibilityPolicyConfiguration ToPolicy() => new(
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
