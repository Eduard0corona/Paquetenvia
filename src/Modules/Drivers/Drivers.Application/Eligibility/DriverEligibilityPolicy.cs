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
    IReadOnlyDictionary<string, DriverDocumentSnapshot> LatestDocuments,
    string PolicyVersion);

public sealed record VehicleCapacityLimits(
    int MaximumPackageCount,
    long MaximumTotalWeightGrams,
    long MaximumSinglePackageWeightGrams,
    int MaximumLengthMillimeters,
    int MaximumWidthMillimeters,
    int MaximumHeightMillimeters,
    bool RequireDimensions);

/// <summary>
/// The shared eligibility rules (documents and vehicle capacity). Their version is not part of the rules:
/// POLICY-VERSIONS-PER-ORG-2026-10-02 makes it per organization, so it arrives with the driver snapshot
/// (<see cref="DriverEligibilitySnapshot.PolicyVersion"/>, the version of the driver's organization).
/// </summary>
public sealed record DriverEligibilityPolicyConfiguration(
    IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredDocumentTypesByVehicleType,
    IReadOnlySet<string> NonExpiringDocumentTypes,
    IReadOnlyDictionary<string, VehicleCapacityLimits> VehicleCapacity);

public static class DriverEligibilityPolicy
{
    /// <summary>The code of each place of the sorted sides: largest, middle and smallest.</summary>
    private static readonly string[] SideCodes =
    [
        DriverEligibilityRejectionCodes.PackageLengthExceeded,
        DriverEligibilityRejectionCodes.PackageWidthExceeded,
        DriverEligibilityRejectionCodes.PackageHeightExceeded,
    ];

    public static DriverEligibilityResult Evaluate(
        EvaluateOwnDriverEligibilityCommand command,
        DriverEligibilitySnapshot? snapshot,
        DriverEligibilityPolicyConfiguration policy)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Evaluate(
            command.OrganizationId,
            command.DriverId,
            command.CityId,
            command.ServiceAreaId,
            command.Capacity,
            command.EvaluatedAt,
            "OWN",
            DriverEligibilityRejectionCodes.DriverTypeNotOwn,
            snapshot,
            policy);
    }

    public static DriverEligibilityResult EvaluateExternal(
        EvaluateExternalDriverEligibilityCommand command,
        DriverEligibilitySnapshot? snapshot,
        DriverEligibilityPolicyConfiguration policy)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Evaluate(
            command.OrganizationId,
            command.DriverId,
            command.CityId,
            command.ServiceAreaId,
            command.Capacity,
            command.EvaluatedAt,
            "EXTERNAL",
            DriverEligibilityRejectionCodes.DriverTypeNotExternal,
            snapshot,
            policy);
    }

    private static DriverEligibilityResult Evaluate(
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId,
        DriverCapacityRequirement capacity,
        DateTimeOffset evaluatedAt,
        string requiredDriverType,
        string driverTypeRejectionCode,
        DriverEligibilitySnapshot? snapshot,
        DriverEligibilityPolicyConfiguration policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (snapshot is null)
        {
            return Result(null, null, null, [DriverEligibilityRejectionCodes.DriverUnavailable]);
        }

        // POLICY-VERSIONS-PER-ORG-2026-10-02: the version is the one of the driver's organization. AI-06 makes
        // it NOT NULL with a format CHECK, so a missing or malformed value is a broken read: fail loudly.
        if (!OrganizationPolicyVersionFormat.IsValid(snapshot.PolicyVersion))
        {
            throw new InvalidOperationException(
                "The driver organization's eligibility policy version is missing or malformed.");
        }

        var codes = new List<string>();

        AddWhen(codes, snapshot.OrganizationId != organizationId || snapshot.DriverId != driverId,
            DriverEligibilityRejectionCodes.DriverUnavailable);
        AddWhen(codes, snapshot.DriverType != requiredDriverType, driverTypeRejectionCode);
        AddWhen(codes, snapshot.ProfileStatus != "ACTIVE", DriverEligibilityRejectionCodes.DriverStatusNotActive);
        AddWhen(codes, snapshot.UserStatus != "ACTIVE", DriverEligibilityRejectionCodes.UserNotActive);
        AddWhen(codes, !snapshot.HasActiveDriverMembership,
            DriverEligibilityRejectionCodes.DriverMembershipNotActive);
        AddWhen(codes, snapshot.HomeCityId != cityId, DriverEligibilityRejectionCodes.HomeCityMismatch);

        if (serviceAreaId is { } requiredServiceAreaId)
        {
            AddWhen(codes, requiredServiceAreaId == Guid.Empty, DriverEligibilityRejectionCodes.ServiceAreaRequired);
            AddWhen(codes, requiredServiceAreaId != Guid.Empty && snapshot.ServiceAreaEligible != true,
                DriverEligibilityRejectionCodes.ServiceAreaNotEligible);
        }

        EvaluateDocuments(evaluatedAt, snapshot, policy, codes);
        EvaluateCapacity(capacity, snapshot.VehicleType, policy, codes);

        return Result(snapshot.DriverId, snapshot.VehicleType, snapshot.PolicyVersion, codes);
    }

    private static void EvaluateDocuments(
        DateTimeOffset evaluatedAt,
        DriverEligibilitySnapshot snapshot,
        DriverEligibilityPolicyConfiguration policy,
        List<string> codes)
    {
        if (evaluatedAt == default ||
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
            else if (document.ExpiresAt <= evaluatedAt)
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
        EvaluateSides(requirement.Packages, limits, codes);
    }

    private static bool IsValid(DriverCapacityRequirement value) =>
        value is not null &&
        value.PackageCount >= 1 &&
        value.TotalWeightGrams > 0 &&
        value.MaximumSinglePackageWeightGrams > 0 &&
        value.MaximumSinglePackageWeightGrams <= value.TotalWeightGrams &&
        value.Packages.Count == value.PackageCount &&
        value.Packages.All(sides =>
            sides is not null &&
            sides.LengthMillimeters is null or > 0 &&
            sides.WidthMillimeters is null or > 0 &&
            sides.HeightMillimeters is null or > 0);

    /// <summary>
    /// DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20: a package fits when, with its measured sides and the vehicle's
    /// three limits both sorted from largest to smallest, each side is at most the limit in the same place; the order
    /// fits when every package fits. A side that was not measured is not checked, so one measured side is compared
    /// with the largest limit and two with the two largest. A side that does not fit reports the code of its place:
    /// the largest <c>PACKAGE_LENGTH_EXCEEDED</c>, the middle <c>PACKAGE_WIDTH_EXCEEDED</c> and the smallest
    /// <c>PACKAGE_HEIGHT_EXCEEDED</c>. When the vehicle requires dimensions, a side left blank fails with the code of
    /// its field (length, width or height). Codes keep their fixed order and appear once.
    /// </summary>
    private static void EvaluateSides(
        IReadOnlyList<PackageSides> packages,
        VehicleCapacityLimits limits,
        List<string> codes)
    {
        var sortedLimits = SortedDescending(
            limits.MaximumLengthMillimeters,
            limits.MaximumWidthMillimeters,
            limits.MaximumHeightMillimeters);
        var failed = new bool[SideCodes.Length];
        foreach (var package in packages)
        {
            if (limits.RequireDimensions)
            {
                failed[0] |= package.LengthMillimeters is null;
                failed[1] |= package.WidthMillimeters is null;
                failed[2] |= package.HeightMillimeters is null;
            }

            var sortedSides = SortedDescending(
                package.LengthMillimeters,
                package.WidthMillimeters,
                package.HeightMillimeters);
            for (var place = 0; place < sortedSides.Length; place++)
            {
                failed[place] |= sortedSides[place] > sortedLimits[place];
            }
        }

        for (var place = 0; place < SideCodes.Length; place++)
        {
            AddWhen(codes, failed[place], SideCodes[place]);
        }
    }

    /// <summary>The measured values, largest first; the one sort the policy applies to packages and limits.</summary>
    private static int[] SortedDescending(int? first, int? second, int? third)
    {
        var measured = new List<int>(3);
        foreach (var value in new[] { first, second, third })
        {
            if (value is { } side)
            {
                measured.Add(side);
            }
        }

        measured.Sort((left, right) => right.CompareTo(left));
        return [.. measured];
    }

    private static DriverEligibilityResult Result(
        Guid? driverId,
        string? vehicleType,
        string? policyVersion,
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
