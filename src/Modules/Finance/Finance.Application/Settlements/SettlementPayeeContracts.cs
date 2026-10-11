using Paqueteria.Application.Paging;

namespace Finance.Application.Settlements;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listSettlementPayees, "Sí, con referencia DRV-"): one page of the drivers Finance
/// may choose to settle, which are exactly the drivers createSettlement accepts as payee - every driver profile of
/// the selected organization, whatever its type or status, so a suspended or retired driver can still be paid for
/// past work. <see cref="AfterDriverId"/> is the position of a cursor this operation issued; the server owns the page
/// size.
/// </summary>
public sealed record ListSettlementPayeesQuery(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied,
    Guid? AfterDriverId);

/// <summary>
/// One payee: the id createSettlement and the payee_id filter of listSettlements take (never shown), the
/// non-personal <c>DRV-</c> reference and the vehicle. Nothing else is read: no status, stop, position, name, phone,
/// email or document data.
/// </summary>
public sealed record SettlementPayeeResult(Guid DriverId, string DriverReference, string VehicleType);

public sealed record SettlementPayeePage(IReadOnlyList<SettlementPayeeResult> Items, string? NextCursor)
{
    public IReadOnlyList<SettlementPayeeResult> Items { get; } = Items.ToArray();
}

public interface ISettlementPayeeQuery
{
    /// <summary>
    /// Throws <see cref="FinanceForbiddenException"/> without the createSettlement capability,
    /// <see cref="SettlementConflictException"/> for a request outside the contract and
    /// <see cref="FinanceUnavailableException"/> when Finance is disabled, the store is unavailable or a stored value
    /// falls outside the published vocabulary.
    /// </summary>
    Task<SettlementPayeePage> ListPayeesAsync(ListSettlementPayeesQuery query, CancellationToken cancellationToken);
}

public static class SettlementPayeePolicy
{
    /// <summary>Server-owned page size; the pilot fleets fit in one page.</summary>
    public const int PageSize = 100;

    public static readonly IReadOnlyList<string> VehicleTypes = ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"];

    public static bool IsValid(ListSettlementPayeesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.ActorId != Guid.Empty &&
            query.OrganizationId != Guid.Empty &&
            query.AfterDriverId != Guid.Empty;
    }

    /// <summary>The listed payee for one profile; an empty id or a vehicle outside the vocabulary fails closed.</summary>
    public static SettlementPayeeResult ToResult(Guid driverId, string vehicleType)
    {
        if (driverId == Guid.Empty || !VehicleTypes.Contains(vehicleType, StringComparer.Ordinal))
        {
            throw new FinanceUnavailableException("A settlement payee falls outside the published vocabulary.");
        }

        return new SettlementPayeeResult(
            driverId,
            Paqueteria.Application.Privacy.DriverReference.From(driverId),
            vehicleType);
    }
}

/// <summary>
/// The opaque cursor of listSettlementPayees: the last listed driver id under its own version byte, so a cursor of
/// any other list is never accepted here.
/// </summary>
public static class SettlementPayeeCursorCodec
{
    public const byte Version = 3;

    public static string Encode(Guid afterDriverId) => IdKeysetCursorCodec.Encode(Version, afterDriverId);

    public static bool TryDecode(string? value, out Guid afterDriverId) =>
        IdKeysetCursorCodec.TryDecode(value, Version, out afterDriverId);
}
