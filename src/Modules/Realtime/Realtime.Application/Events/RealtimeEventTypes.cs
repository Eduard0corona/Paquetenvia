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

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-12 <c>NotificationStatusChanged</c> decided amendment, AI-08 NTF-003): the
    /// envelope of a <c>notification-status-changed-v2</c> row, whose payload may also carry the order and the notice of
    /// a recipient notice. A <c>notification-status-changed-v1</c> row keeps <see cref="NotificationStatusChanged"/>.
    /// </summary>
    public const string NotificationStatusChangedV2 = "NotificationStatusChanged.v2";
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
        NotificationStatusChangedV2,
        DriverLocationUpdated,
        PublicOrderStatusChanged,
        PublicEtaChanged,
    ];

    /// <summary>Whether <paramref name="value"/> is a <c>NotificationStatusChanged</c> envelope of either version.</summary>
    public static bool IsNotificationStatusChanged(string? value) =>
        value is NotificationStatusChanged or NotificationStatusChangedV2;

    public static bool IsKnown(string? value) =>
        value is not null && KnownValues.Contains(value);
}
