using System.Collections.Immutable;
using Dispatch.Application.Assignments;
using Paqueteria.Application.Paging;

namespace Dispatch.Application.Fleet;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listOrganizationDrivers, "Lista de consulta"): one page of the OWN drivers of the
/// active organization that are not INACTIVE, the same drivers the assignment picker lists, for the dispatcher's
/// read-only "Repartidores" screen. <see cref="AfterDriverId"/> is the position of a cursor this operation issued;
/// the server owns the page size.
/// </summary>
public sealed record ListOrganizationDriversQuery(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    Guid? AfterDriverId);

/// <summary>
/// One listed driver: its id (never shown), its non-personal <c>DRV-</c> reference, its vehicle, its profile status,
/// the stops it holds now (its ACCEPTED or ACTIVE assignments whose order is in a <c>DriverStopPolicy</c> stop
/// status, the stops listMyStops shows the driver) and the capture time of its latest reported position, without
/// the position itself. No name, phone, email or document data is read or returned.
/// </summary>
public sealed record OrganizationDriverResult(
    Guid DriverId,
    string DriverReference,
    string VehicleType,
    string Status,
    int ActiveStopCount,
    DateTimeOffset? LastPositionAt);

public sealed record OrganizationDriverPage(IReadOnlyList<OrganizationDriverResult> Items, string? NextCursor)
{
    public IReadOnlyList<OrganizationDriverResult> Items { get; } = Items.ToImmutableArray();
}

public interface IOrganizationDriversQuery
{
    /// <summary>
    /// Throws <see cref="AssignmentForbiddenException"/> without the listOrganizationDrivers capability and
    /// <see cref="AssignmentInfrastructureException"/> when the store is unavailable or a stored value falls outside
    /// the published vocabulary.
    /// </summary>
    Task<OrganizationDriverPage> ListAsync(ListOrganizationDriversQuery query, CancellationToken cancellationToken);
}

public static class OrganizationDriverPolicy
{
    /// <summary>Server-owned page size, the one of the assignment picker; the pilot fleets fit in one page.</summary>
    public const int PageSize = 100;

    /// <summary>The profile statuses a listed driver can have: every status but INACTIVE (a retired driver).</summary>
    public static readonly ImmutableArray<string> Statuses = ["PENDING", "ACTIVE", "SUSPENDED"];

    /// <summary>
    /// The listed driver for one row. A vehicle type or status outside the published vocabulary, an empty id or a
    /// negative count fails closed.
    /// </summary>
    public static OrganizationDriverResult ToResult(
        Guid driverId,
        string vehicleType,
        string status,
        int activeStopCount,
        DateTimeOffset? lastPositionAt)
    {
        if (driverId == Guid.Empty ||
            !AssignableDriverPolicy.VehicleTypes.Contains(vehicleType) ||
            !Statuses.Contains(status) ||
            activeStopCount < 0)
        {
            throw new AssignmentInfrastructureException(
                "An organization driver falls outside the published vocabulary.");
        }

        return new OrganizationDriverResult(
            driverId,
            Paqueteria.Application.Privacy.DriverReference.From(driverId),
            vehicleType,
            status,
            activeStopCount,
            lastPositionAt?.ToUniversalTime());
    }
}

/// <summary>
/// The opaque cursor of listOrganizationDrivers: the last listed driver id under its own version byte, so a cursor
/// of the assignment picker (version 1) or of any other list is never accepted here.
/// </summary>
public static class OrganizationDriverCursorCodec
{
    public const byte Version = 2;

    public static string Encode(Guid afterDriverId) => IdKeysetCursorCodec.Encode(Version, afterDriverId);

    public static bool TryDecode(string? value, out Guid afterDriverId) =>
        IdKeysetCursorCodec.TryDecode(value, Version, out afterDriverId);
}
