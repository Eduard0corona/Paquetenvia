using System.Collections.Immutable;

namespace Drivers.Application.Eligibility;

/// <summary>
/// The sides of one package in millimetres, as captured. They may be typed in any order: the policy sorts them, and
/// the vehicle's limits, from largest to smallest before comparing (DRV-ELIGIBILITY-SORTED-DIMENSIONS-2026-10-20).
/// <c>null</c> is a side that was not measured.
/// </summary>
public sealed record PackageSides(int? LengthMillimeters, int? WidthMillimeters, int? HeightMillimeters);

/// <summary>
/// What an order asks of a vehicle: how many packages, their total weight, the heaviest one, and the sides of each
/// package (one entry per package, in package order). The sides stay per package because a package turns on its own:
/// the largest length, width and height across packages would pair sides of different packages.
/// </summary>
public sealed record DriverCapacityRequirement(
    int PackageCount,
    long TotalWeightGrams,
    long MaximumSinglePackageWeightGrams,
    IReadOnlyList<PackageSides> Packages)
{
    public IReadOnlyList<PackageSides> Packages { get; } =
        Packages is null ? ImmutableArray<PackageSides>.Empty : Packages.ToImmutableArray();

    public bool Equals(DriverCapacityRequirement? other) =>
        other is not null &&
        PackageCount == other.PackageCount &&
        TotalWeightGrams == other.TotalWeightGrams &&
        MaximumSinglePackageWeightGrams == other.MaximumSinglePackageWeightGrams &&
        Packages.SequenceEqual(other.Packages);

    public override int GetHashCode() =>
        HashCode.Combine(PackageCount, TotalWeightGrams, MaximumSinglePackageWeightGrams, Packages.Count);
}

public sealed record EvaluateOwnDriverEligibilityCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid DriverId,
    Guid CityId,
    Guid? ServiceAreaId,
    DriverCapacityRequirement Capacity,
    DateTimeOffset EvaluatedAt);

public sealed record EvaluateExternalDriverEligibilityCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid DriverId,
    Guid CityId,
    Guid? ServiceAreaId,
    DriverCapacityRequirement Capacity,
    DateTimeOffset EvaluatedAt);

public sealed record DriverEligibilityRejection(string Code);

public sealed record DriverEligibilityResult(
    bool IsEligible,
    Guid? DriverId,
    string? VehicleType,
    string? PolicyVersion,
    IReadOnlyList<DriverEligibilityRejection> Rejections)
{
    public IReadOnlyList<DriverEligibilityRejection> Rejections { get; } =
        Rejections.ToImmutableArray();
}

public interface IDriverEligibilityService
{
    Task<DriverEligibilityResult> EvaluateAsync(
        EvaluateOwnDriverEligibilityCommand command,
        CancellationToken cancellationToken);
}

public static class DriverEligibilityRejectionCodes
{
    public const string DriverUnavailable = "DRIVER_UNAVAILABLE";
    public const string DriverTypeNotOwn = "DRIVER_TYPE_NOT_OWN";
    public const string DriverTypeNotExternal = "DRIVER_TYPE_NOT_EXTERNAL";
    public const string DriverStatusNotActive = "DRIVER_STATUS_NOT_ACTIVE";
    public const string UserNotActive = "USER_NOT_ACTIVE";
    public const string DriverMembershipNotActive = "DRIVER_MEMBERSHIP_NOT_ACTIVE";
    public const string HomeCityMismatch = "HOME_CITY_MISMATCH";
    public const string ServiceAreaRequired = "SERVICE_AREA_REQUIRED";
    public const string ServiceAreaNotEligible = "SERVICE_AREA_NOT_ELIGIBLE";
    public const string DocumentPolicyUnavailable = "DOCUMENT_POLICY_UNAVAILABLE";
    public const string RequiredDocumentMissing = "REQUIRED_DOCUMENT_MISSING";
    public const string DocumentStatusNotValid = "DOCUMENT_STATUS_NOT_VALID";
    public const string DocumentExpired = "DOCUMENT_EXPIRED";
    public const string DocumentExpiryMissing = "DOCUMENT_EXPIRY_MISSING";
    public const string DocumentHashInvalid = "DOCUMENT_HASH_INVALID";
    public const string VehicleCapacityPolicyUnavailable = "VEHICLE_CAPACITY_POLICY_UNAVAILABLE";
    public const string PackageRequirementInvalid = "PACKAGE_REQUIREMENT_INVALID";
    public const string PackageCountExceeded = "PACKAGE_COUNT_EXCEEDED";
    public const string TotalWeightExceeded = "TOTAL_WEIGHT_EXCEEDED";
    public const string SinglePackageWeightExceeded = "SINGLE_PACKAGE_WEIGHT_EXCEEDED";
    public const string PackageLengthExceeded = "PACKAGE_LENGTH_EXCEEDED";
    public const string PackageWidthExceeded = "PACKAGE_WIDTH_EXCEEDED";
    public const string PackageHeightExceeded = "PACKAGE_HEIGHT_EXCEEDED";
}
