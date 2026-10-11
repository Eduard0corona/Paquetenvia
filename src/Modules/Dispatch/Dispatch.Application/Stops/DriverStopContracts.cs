namespace Dispatch.Application.Stops;

/// <summary>
/// One stop of the requesting driver's own ACCEPTED or ACTIVE assignments. UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11
/// adds what the driver needs at the door: the contact name (the full name on a PICKUP stop, the short name on
/// DELIVERY and RETURN stops, never both), the operating zone, the order's service window, the expected cash on
/// delivery of a DELIVERY stop and the navigation target. <see cref="ToString"/> never prints them, so a stop that
/// reaches a log carries no personal data.
/// </summary>
public sealed record DriverStopResult(
    Guid OrderId,
    long AggregateVersion,
    string OrderPublicId,
    string StopType,
    string Status,
    string AddressSummary,
    string? ContactShortName,
    string? ContactFullName,
    string? ZoneName,
    DriverStopServiceWindowResult? ServiceWindow,
    DriverStopCodResult? Cod,
    DriverStopNavigationResult Navigation)
{
    public override string ToString() =>
        $"DriverStopResult {{ OrderId = {OrderId:D}, AggregateVersion = {AggregateVersion}, StopType = {StopType}, " +
        $"Status = {Status}, [details redacted] }}";
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
    Task<IReadOnlyList<DriverStopResult>> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
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

/// <summary>
/// The protected address or contact name of a stop could not be read (the PII protector is unavailable or a stored
/// value was rejected). The whole response fails closed with 503; the message never carries a value.
/// </summary>
public sealed class DriverStopsPiiUnavailableException(Exception? innerException = null)
    : Exception("The protected stop details are unavailable.", innerException);
