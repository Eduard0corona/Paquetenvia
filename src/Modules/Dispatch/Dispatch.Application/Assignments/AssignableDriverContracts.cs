using System.Collections.Immutable;
using Drivers.Application.Eligibility;

namespace Dispatch.Application.Assignments;

/// <summary>
/// UI-PHASE2-DRIVER-PICKER-2026-10-05: one page of the OWN drivers of the active organization that the dispatcher
/// may pick for <see cref="OrderId"/>, each with its eligibility for that order. <see cref="Cursor"/> is a position
/// this operation issued; the server owns the page size.
/// </summary>
public sealed record ListAssignableDriversQuery(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    Guid OrderId,
    AssignableDriverCursor? Cursor);

/// <summary>Drivers are listed by id; the cursor is the last id of the previous page.</summary>
public sealed record AssignableDriverCursor(Guid AfterDriverId);

/// <summary>
/// One listed driver: its id (to send to assignDriver), its non-PII <c>DRV-</c> reference, its vehicle, whether
/// assignDriver would accept it for the order now and, when not, the stable DriverEligibilityPolicy codes, plus its
/// ACCEPTED or ACTIVE assignments. No name, phone, email, document or location is read or returned.
/// </summary>
public sealed record AssignableDriverResult(
    Guid DriverId,
    string DriverReference,
    string VehicleType,
    bool Eligible,
    IReadOnlyList<string> IneligibilityReasons,
    int ActiveAssignmentCount)
{
    public IReadOnlyList<string> IneligibilityReasons { get; } = IneligibilityReasons.ToImmutableArray();
}

public sealed record AssignableDriverPage(IReadOnlyList<AssignableDriverResult> Items, string? NextCursor)
{
    public IReadOnlyList<AssignableDriverResult> Items { get; } = Items.ToImmutableArray();
}

public interface IAssignableDriversQuery
{
    /// <summary>
    /// Throws <see cref="AssignmentForbiddenException"/> without the assignDriver capability,
    /// <see cref="AssignmentNotFoundException"/> for a missing or foreign order,
    /// <see cref="AssignmentConflictException"/> when the order does not admit an assignment now and
    /// <see cref="AssignmentInfrastructureException"/> when a stored value falls outside the published vocabulary.
    /// </summary>
    Task<AssignableDriverPage> ListAsync(ListAssignableDriversQuery query, CancellationToken cancellationToken);
}

public static class AssignableDriverPolicy
{
    /// <summary>Server-owned page size; the pilot fleets fit in one page.</summary>
    public const int PageSize = 100;

    public static readonly ImmutableArray<string> VehicleTypes = ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"];

    /// <summary>
    /// The DriverEligibilityPolicy codes an OWN driver of the active organization can receive, in the policy's
    /// stable order. DRIVER_TYPE_NOT_OWN and DRIVER_TYPE_NOT_EXTERNAL never apply (only OWN profiles are listed);
    /// any code outside this set fails closed.
    /// </summary>
    public static readonly ImmutableArray<string> ReasonCodes =
    [
        DriverEligibilityRejectionCodes.DriverUnavailable,
        DriverEligibilityRejectionCodes.DriverStatusNotActive,
        DriverEligibilityRejectionCodes.UserNotActive,
        DriverEligibilityRejectionCodes.DriverMembershipNotActive,
        DriverEligibilityRejectionCodes.HomeCityMismatch,
        DriverEligibilityRejectionCodes.ServiceAreaRequired,
        DriverEligibilityRejectionCodes.ServiceAreaNotEligible,
        DriverEligibilityRejectionCodes.DocumentPolicyUnavailable,
        DriverEligibilityRejectionCodes.RequiredDocumentMissing,
        DriverEligibilityRejectionCodes.DocumentStatusNotValid,
        DriverEligibilityRejectionCodes.DocumentExpired,
        DriverEligibilityRejectionCodes.DocumentExpiryMissing,
        DriverEligibilityRejectionCodes.DocumentHashInvalid,
        DriverEligibilityRejectionCodes.VehicleCapacityPolicyUnavailable,
        DriverEligibilityRejectionCodes.PackageRequirementInvalid,
        DriverEligibilityRejectionCodes.PackageCountExceeded,
        DriverEligibilityRejectionCodes.TotalWeightExceeded,
        DriverEligibilityRejectionCodes.SinglePackageWeightExceeded,
        DriverEligibilityRejectionCodes.PackageLengthExceeded,
        DriverEligibilityRejectionCodes.PackageWidthExceeded,
        DriverEligibilityRejectionCodes.PackageHeightExceeded,
    ];

    /// <summary>Only an order in one of these statuses, without an ACCEPTED or ACTIVE assignment, is listed.</summary>
    public static readonly ImmutableArray<string> AssignableOrderStatuses = ["READY_FOR_PICKUP", "RESCHEDULED"];

    /// <summary>
    /// The listed driver for one evaluation. A vehicle type or code outside the published vocabulary, or an
    /// evaluation for another driver, fails closed.
    /// </summary>
    public static AssignableDriverResult ToResult(
        DriverEligibilitySnapshot snapshot,
        DriverEligibilityResult eligibility,
        int activeAssignmentCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(eligibility);
        var codes = eligibility.Rejections.Select(rejection => rejection.Code).ToArray();
        if (!VehicleTypes.Contains(snapshot.VehicleType) ||
            eligibility.DriverId != snapshot.DriverId ||
            eligibility.IsEligible != (codes.Length == 0) ||
            codes.Any(code => !ReasonCodes.Contains(code)) ||
            activeAssignmentCount < 0)
        {
            throw new AssignmentInfrastructureException(
                "An assignable driver falls outside the published vocabulary.");
        }

        return new AssignableDriverResult(
            snapshot.DriverId,
            Paqueteria.Application.Privacy.DriverReference.From(snapshot.DriverId),
            snapshot.VehicleType,
            eligibility.IsEligible,
            codes,
            activeAssignmentCount);
    }
}

/// <summary>
/// The opaque cursor: Base64URL without padding of a version byte (1) and the last listed driver id in its 16
/// big-endian bytes. Anything this codec did not issue is rejected.
/// </summary>
public static class AssignableDriverCursorCodec
{
    private const byte Version = 1;

    public static string Encode(AssignableDriverCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Span<byte> bytes = stackalloc byte[17];
        bytes[0] = Version;
        cursor.AfterDriverId.TryWriteBytes(bytes[1..], bigEndian: true, out _);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out AssignableDriverCursor? cursor)
    {
        cursor = null;
        if (value is null || value.Length != 23 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != 17 || bytes[0] != Version)
        {
            return false;
        }

        var driverId = new Guid(bytes.AsSpan(1), bigEndian: true);
        var decoded = new AssignableDriverCursor(driverId);
        if (driverId == Guid.Empty || Encode(decoded) != value)
        {
            return false;
        }

        cursor = decoded;
        return true;
    }
}
