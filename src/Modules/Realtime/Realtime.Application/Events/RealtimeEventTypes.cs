namespace Realtime.Application.Events;

public static class RealtimeEventTypes
{
    public const string OrderStatusChanged = "OrderStatusChanged.v1";
    public const string OrderTimelineEventAdded = "OrderTimelineEventAdded.v1";
    public const string AssignmentChanged = "AssignmentChanged.v1";
    public const string RouteChanged = "RouteChanged.v1";
    public const string IncidentCreated = "IncidentCreated.v1";
    public const string ExternalOfferChanged = "ExternalOfferChanged.v1";
    public const string NotificationStatusChanged = "NotificationStatusChanged.v1";
    public const string DriverLocationUpdated = "DriverLocationUpdated.v1";
    public const string PublicOrderStatusChanged = "PublicOrderStatusChanged.v1";
    public const string PublicEtaChanged = "PublicEtaChanged.v1";

    private static readonly HashSet<string> KnownValues =
    [
        OrderStatusChanged,
        OrderTimelineEventAdded,
        AssignmentChanged,
        RouteChanged,
        IncidentCreated,
        ExternalOfferChanged,
        NotificationStatusChanged,
        DriverLocationUpdated,
        PublicOrderStatusChanged,
        PublicEtaChanged,
    ];

    public static bool IsKnown(string? value) =>
        value is not null && KnownValues.Contains(value);
}
