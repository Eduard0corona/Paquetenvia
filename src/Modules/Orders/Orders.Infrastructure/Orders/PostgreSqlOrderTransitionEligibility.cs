using System.Text.Json;
using Drivers.Application.Eligibility;

namespace Orders.Infrastructure.Orders;

/// <summary>
/// The DSP-002 driver eligibility policy as ORD-002 evaluates it towards <c>ASSIGNED</c> and on
/// re-delivery. It binds the same <c>Drivers:Eligibility</c> section DSP-002 and RTE-001 bind, so
/// the three decisions never drift apart; an empty section fails closed.
/// </summary>
public sealed class OrderTransitionDriverEligibilityOptions
{
    public const string SectionName = "Drivers:Eligibility";

    public string PolicyVersion { get; set; } = "synthetic-v1";
    public Dictionary<string, List<string>> RequiredDocumentTypesByVehicleType { get; set; } =
        new(StringComparer.Ordinal);
    public List<string> NonExpiringDocumentTypes { get; set; } = [];
    public Dictionary<string, OrderTransitionVehicleCapacityOptions> VehicleCapacity { get; set; } =
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

public sealed class OrderTransitionVehicleCapacityOptions
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

/// <summary>
/// Splits a DSP-002 verdict into the two AI-04 guards: <c>capacity_available</c> owns the vehicle
/// capacity rejections and <c>eligible_driver</c> owns every other rejection.
/// </summary>
internal static class OrderTransitionEligibility
{
    private static readonly HashSet<string> CapacityCodes = new(StringComparer.Ordinal)
    {
        DriverEligibilityRejectionCodes.PackageRequirementInvalid,
        DriverEligibilityRejectionCodes.PackageCountExceeded,
        DriverEligibilityRejectionCodes.TotalWeightExceeded,
        DriverEligibilityRejectionCodes.SinglePackageWeightExceeded,
        DriverEligibilityRejectionCodes.PackageLengthExceeded,
        DriverEligibilityRejectionCodes.PackageWidthExceeded,
        DriverEligibilityRejectionCodes.PackageHeightExceeded,
        // A vehicle type without a configured capacity cannot attest capacity: it is a capacity
        // failure, not a driver-eligibility one.
        DriverEligibilityRejectionCodes.VehicleCapacityPolicyUnavailable,
    };

    /// <summary>An invalid requirement the policy always rejects as a capacity failure.</summary>
    internal static readonly DriverCapacityRequirement InvalidRequirement = new(0, 0, 0, null, null, null);

    internal static (bool EligibleDriver, bool CapacityAvailable) Evaluate(
        string assignmentType,
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId,
        DriverCapacityRequirement capacity,
        DateTimeOffset evaluatedAt,
        DriverEligibilitySnapshot? snapshot,
        DriverEligibilityPolicyConfiguration policy)
    {
        DriverEligibilityResult result;
        switch (assignmentType)
        {
            case "OWN":
                result = DriverEligibilityPolicy.Evaluate(
                    new EvaluateOwnDriverEligibilityCommand(
                        Guid.Empty, organizationId, driverId, cityId, serviceAreaId, capacity, evaluatedAt),
                    snapshot,
                    policy);
                break;
            case "EXTERNAL":
                result = DriverEligibilityPolicy.EvaluateExternal(
                    new EvaluateExternalDriverEligibilityCommand(
                        Guid.Empty, organizationId, driverId, cityId, serviceAreaId, capacity, evaluatedAt),
                    snapshot,
                    policy);
                break;
            default:
                // ALLY_CAPACITY has no approved eligibility policy yet: fail closed.
                return (false, false);
        }

        var codes = result.Rejections.Select(rejection => rejection.Code).ToArray();
        return (
            codes.All(CapacityCodes.Contains),
            !codes.Any(CapacityCodes.Contains));
    }

    /// <summary>
    /// Aggregates <c>orders.package_items</c> exactly as DSP-002 does. Any unreadable package makes
    /// the whole requirement invalid instead of being skipped.
    /// </summary>
    internal static DriverCapacityRequirement Aggregate(
        IReadOnlyList<(long WeightGrams, string DimensionsJson)> packages)
    {
        if (packages.Count == 0)
        {
            return InvalidRequirement;
        }

        try
        {
            long totalWeight = 0;
            long maximumWeight = 0;
            int? maximumLength = null;
            int? maximumWidth = null;
            int? maximumHeight = null;
            foreach (var (weight, dimensionsJson) in packages)
            {
                if (weight <= 0 ||
                    !TryReadDimensions(dimensionsJson, out var length, out var width, out var height))
                {
                    return InvalidRequirement;
                }

                totalWeight = checked(totalWeight + weight);
                maximumWeight = Math.Max(maximumWeight, weight);
                maximumLength = Max(maximumLength, length);
                maximumWidth = Max(maximumWidth, width);
                maximumHeight = Max(maximumHeight, height);
            }

            return new DriverCapacityRequirement(
                packages.Count,
                totalWeight,
                maximumWeight,
                maximumLength,
                maximumWidth,
                maximumHeight);
        }
        catch (OverflowException)
        {
            return InvalidRequirement;
        }
    }

    private static int? Max(int? current, int? value) =>
        value is null ? current : current is null ? value : Math.Max(current.Value, value.Value);

    private static bool TryReadDimensions(string json, out int? length, out int? width, out int? height)
    {
        length = null;
        width = null;
        height = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                TryReadDimension(document.RootElement, "length_mm", out length) &&
                TryReadDimension(document.RootElement, "width_mm", out width) &&
                TryReadDimension(document.RootElement, "height_mm", out height);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDimension(JsonElement element, string name, out int? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var parsed) ||
            parsed <= 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
