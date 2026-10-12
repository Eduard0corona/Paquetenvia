namespace Dispatch.Application.Stops;

/// <summary>
/// One stop of the requesting driver's own ACCEPTED or ACTIVE assignments. UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11
/// adds what the driver needs at the door: the contact name (the full name on PICKUP and RETURN stops, the short name
/// on DELIVERY stops, never both), the operating zone, the order's service window, the expected cash on delivery of a
/// DELIVERY stop and the navigation target. <see cref="DetailsAvailable"/> is <see langword="false"/> when the
/// protected details were not requested or could not be read: the contact names and the navigation target are then
/// <see langword="null"/>, never partial. <see cref="ToString"/> never prints the details, so a stop that reaches a
/// log carries no personal data.
/// </summary>
public sealed record DriverStopResult(
    Guid OrderId,
    long AggregateVersion,
    string OrderPublicId,
    string StopType,
    string Status,
    string AddressSummary,
    bool DetailsAvailable,
    string? ContactShortName,
    string? ContactFullName,
    string? ZoneName,
    DriverStopServiceWindowResult? ServiceWindow,
    DriverStopCodResult? Cod,
    DriverStopNavigationResult? Navigation)
{
    public override string ToString() =>
        $"DriverStopResult {{ OrderId = {OrderId:D}, AggregateVersion = {AggregateVersion}, StopType = {StopType}, " +
        $"Status = {Status}, DetailsAvailable = {DetailsAvailable}, [details redacted] }}";
}

/// <summary>
/// The requesting driver's stops and, UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 (owner answer "Solo el número"), how
/// many of the driver's stops were completed today in America/Mazatlan time: a count only, with no data about those
/// stops (see <see cref="DriverStopsCompletedToday"/>).
/// </summary>
public sealed record DriverStopListResult(IReadOnlyList<DriverStopResult> Stops, int CompletedToday);

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a stop is completed when the order leaves the stop the driver was heading
/// to - PICKED_UP completes a pickup, DELIVERED a delivery and RETURNED a return - by a status change the requesting
/// driver made (the event actor) on an order of the selected organization the driver is or was assigned to.
/// "Today" is the calendar day in America/Mazatlan that contains the request time. A failed attempt, a reschedule or a
/// change made by someone else is not a completed stop of this driver.
/// </summary>
public static class DriverStopsCompletedToday
{
    public const string TimeZone = "America/Mazatlan";

    public static IReadOnlyList<string> CompletingStatuses { get; } = ["PICKED_UP", "DELIVERED", "RETURNED"];
}

/// <summary>The order's own delivery window as UTC instants (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02).</summary>
public sealed record DriverStopServiceWindowResult(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// The cash a DELIVERY stop collects: the order's expected COD in MXN integer cents and whether a collection of
/// exactly that amount is already RECORDED or RECONCILED (the condition the DELIVERED guard checks).
/// </summary>
public sealed record DriverStopCodResult(long ExpectedCents, bool Recorded);

/// <summary>
/// Where the stop is: the exact point and the decrypted address text with its references. It exists only in
/// memory for the response; <see cref="ToString"/> never prints it.
/// </summary>
public sealed record DriverStopNavigationResult(
    double Latitude,
    double Longitude,
    string AddressText,
    string? References)
{
    public override string ToString() => "DriverStopNavigationResult { [redacted] }";
}

public interface IDriverStopsQuery
{
    /// <summary>
    /// The requesting driver's current stops. With <paramref name="includeDetails"/> <see langword="false"/> the
    /// protected details are not read at all (no key service call): the driver app confirms queued actions with it.
    /// </summary>
    Task<DriverStopListResult> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
        bool includeDetails,
        CancellationToken cancellationToken);
}

public sealed class DriverStopsForbiddenException : Exception
{
    public DriverStopsForbiddenException()
        : base("The actor has no visible active own-driver profile.")
    {
    }
}

public sealed class DriverStopsInfrastructureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
