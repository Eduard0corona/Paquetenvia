namespace Drivers.Application.Eligibility;

public sealed record DriverDocumentSnapshot(
    string DocumentType,
    string Status,
    string ObjectKey,
    byte[] Sha256,
    DateTimeOffset? ExpiresAt);

public sealed record DriverEligibilitySnapshot(
    Guid DriverId,
    Guid OrganizationId,
    Guid UserId,
    Guid HomeCityId,
    string DriverType,
    string VehicleType,
    string ProfileStatus,
    string? UserStatus,
    bool HasActiveDriverMembership,
    bool? ServiceAreaEligible,
    IReadOnlyDictionary<string, DriverDocumentSnapshot> LatestDocuments);

public sealed record VehicleCapacityLimits(
    int MaximumPackageCount,
    long MaximumTotalWeightGrams,
    long MaximumSinglePackageWeightGrams,
    int MaximumLengthMillimeters,
    int MaximumWidthMillimeters,
    int MaximumHeightMillimeters,
    bool RequireDimensions);

public sealed record DriverEligibilityPolicyConfiguration(
    string PolicyVersion,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredDocumentTypesByVehicleType,
    IReadOnlySet<string> NonExpiringDocumentTypes,
    IReadOnlyDictionary<string, VehicleCapacityLimits> VehicleCapacity);

public static class DriverEligibilityPolicy
{
    public static DriverEligibilityResult Evaluate(
        EvaluateOwnDriverEligibilityCommand command,
        DriverEligibilitySnapshot? snapshot,
        DriverEligibilityPolicyConfiguration policy)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(policy);

        if (snapshot is null)
        {
            return Result(null, null, policy.PolicyVersion, [DriverEligibilityRejectionCodes.DriverUnavailable]);
        }

        var codes = new List<string>();

        AddWhen(codes, snapshot.DriverType != "OWN", DriverEligibilityRejectionCodes.DriverTypeNotOwn);
        AddWhen(codes, snapshot.ProfileStatus != "ACTIVE", DriverEligibilityRejectionCodes.DriverStatusNotActive);
        AddWhen(codes, snapshot.UserStatus != "ACTIVE", DriverEligibilityRejectionCodes.UserNotActive);
        AddWhen(codes, !snapshot.HasActiveDriverMembership,
            DriverEligibilityRejectionCodes.DriverMembershipNotActive);
        AddWhen(codes, snapshot.HomeCityId != command.CityId, DriverEligibilityRejectionCodes.HomeCityMismatch);

        if (command.ServiceAreaId is { } serviceAreaId)
        {
            AddWhen(codes, serviceAreaId == Guid.Empty, DriverEligibilityRejectionCodes.ServiceAreaRequired);
            AddWhen(codes, serviceAreaId != Guid.Empty && snapshot.ServiceAreaEligible != true,
                DriverEligibilityRejectionCodes.ServiceAreaNotEligible);
        }

        EvaluateDocuments(command, snapshot, policy, codes);
        EvaluateCapacity(command.Capacity, snapshot.VehicleType, policy, codes);

        return Result(snapshot.DriverId, snapshot.VehicleType, policy.PolicyVersion, codes);
    }

    private static void EvaluateDocuments(
        EvaluateOwnDriverEligibilityCommand command,
        DriverEligibilitySnapshot snapshot,
        DriverEligibilityPolicyConfiguration policy,
        List<string> codes)
    {
        if (command.EvaluatedAt == default ||
            !policy.RequiredDocumentTypesByVehicleType.TryGetValue(snapshot.VehicleType, out var requiredTypes))
        {
            Add(codes, DriverEligibilityRejectionCodes.DocumentPolicyUnavailable);
            return;
        }

        foreach (var documentType in requiredTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!snapshot.LatestDocuments.TryGetValue(documentType, out var document))
            {
                Add(codes, DriverEligibilityRejectionCodes.RequiredDocumentMissing);
                continue;
            }

            AddWhen(codes, document.Status != "VALID", DriverEligibilityRejectionCodes.DocumentStatusNotValid);
            AddWhen(codes, string.IsNullOrWhiteSpace(document.ObjectKey) || document.Sha256.Length != 32,
                DriverEligibilityRejectionCodes.DocumentHashInvalid);

            if (policy.NonExpiringDocumentTypes.Contains(documentType))
            {
                continue;
            }

            if (document.ExpiresAt is null)
            {
                Add(codes, DriverEligibilityRejectionCodes.DocumentExpiryMissing);
            }
            else if (document.ExpiresAt <= command.EvaluatedAt)
            {
                Add(codes, DriverEligibilityRejectionCodes.DocumentExpired);
            }
        }
    }

    private static void EvaluateCapacity(
        DriverCapacityRequirement requirement,
        string vehicleType,
        DriverEligibilityPolicyConfiguration policy,
        List<string> codes)
    {
        if (!policy.VehicleCapacity.TryGetValue(vehicleType, out var limits))
        {
            Add(codes, DriverEligibilityRejectionCodes.VehicleCapacityPolicyUnavailable);
            return;
        }

        if (!IsValid(requirement))
        {
            Add(codes, DriverEligibilityRejectionCodes.PackageRequirementInvalid);
            return;
        }

        AddWhen(codes, requirement.PackageCount > limits.MaximumPackageCount,
            DriverEligibilityRejectionCodes.PackageCountExceeded);
        AddWhen(codes, requirement.TotalWeightGrams > limits.MaximumTotalWeightGrams,
            DriverEligibilityRejectionCodes.TotalWeightExceeded);
        AddWhen(codes, requirement.MaximumSinglePackageWeightGrams > limits.MaximumSinglePackageWeightGrams,
            DriverEligibilityRejectionCodes.SinglePackageWeightExceeded);
        EvaluateDimension(codes, requirement.MaximumLengthMillimeters, limits.MaximumLengthMillimeters,
            limits.RequireDimensions, DriverEligibilityRejectionCodes.PackageLengthExceeded);
        EvaluateDimension(codes, requirement.MaximumWidthMillimeters, limits.MaximumWidthMillimeters,
            limits.RequireDimensions, DriverEligibilityRejectionCodes.PackageWidthExceeded);
        EvaluateDimension(codes, requirement.MaximumHeightMillimeters, limits.MaximumHeightMillimeters,
            limits.RequireDimensions, DriverEligibilityRejectionCodes.PackageHeightExceeded);
    }

    private static bool IsValid(DriverCapacityRequirement value) =>
        value is not null &&
        value.PackageCount >= 1 &&
        value.TotalWeightGrams > 0 &&
        value.MaximumSinglePackageWeightGrams > 0 &&
        value.MaximumSinglePackageWeightGrams <= value.TotalWeightGrams &&
        value.MaximumLengthMillimeters is null or > 0 &&
        value.MaximumWidthMillimeters is null or > 0 &&
        value.MaximumHeightMillimeters is null or > 0;

    private static void EvaluateDimension(
        List<string> codes,
        int? actual,
        int limit,
        bool required,
        string code) =>
        AddWhen(codes, (required && actual is null) || actual > limit, code);

    private static DriverEligibilityResult Result(
        Guid? driverId,
        string? vehicleType,
        string policyVersion,
        IEnumerable<string> codes)
    {
        var rejections = codes
            .Distinct(StringComparer.Ordinal)
            .Select(code => new DriverEligibilityRejection(code))
            .ToArray();
        return new DriverEligibilityResult(rejections.Length == 0, driverId, vehicleType, policyVersion, rejections);
    }

    private static void AddWhen(List<string> codes, bool condition, string code)
    {
        if (condition)
        {
            Add(codes, code);
        }
    }

    private static void Add(List<string> codes, string code) => codes.Add(code);
}
