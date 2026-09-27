using Realtime.Application.Events;

namespace Realtime.Application.Dispatching;

public static class RealtimeOutboxTopics
{
    public const string OrderStatusChanged = "orders.status-changed";
    public const string OrderTimelineEventAdded = "orders.timeline-event-added";
    public const string AssignmentChanged = "dispatch.assignment-changed";
    public const string ExternalOfferChanged = "dispatch.external-offer-changed";
    public const string RouteChanged = "routes.route-changed";
    public const string NotificationStatusChanged = "notifications.status-changed";
    public const string DriverLocationUpdated = "drivers.location-updated";

    public static bool IsBusinessTopic(string value) =>
        value is OrderStatusChanged or OrderTimelineEventAdded or AssignmentChanged or
            ExternalOfferChanged or RouteChanged or NotificationStatusChanged;

    public static bool IsLocationTopic(string value) => value == DriverLocationUpdated;
}

public static class RealtimeOutboxErrorCodes
{
    public const string UnknownTopic = "UNKNOWN_TOPIC";
    public const string InvalidSchema = "INVALID_SCHEMA";
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string InvalidAudienceEvidence = "INVALID_AUDIENCE_EVIDENCE";
    public const string SignalRTransient = "SIGNALR_TRANSIENT";
    public const string DatabaseTransient = "DATABASE_TRANSIENT";
    public const string PublishTimeout = "PUBLISH_TIMEOUT";
    public const string LeaseLost = "LEASE_LOST";
}

public sealed record ClaimedBusinessOutboxMessage(
    Guid Id,
    Guid OwnerOrganizationId,
    string TenantContextJson,
    string Topic,
    string AggregateType,
    Guid AggregateId,
    long? AggregateVersion,
    string PayloadJson,
    int Attempts,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset AvailableAt);

public sealed record ClaimedLocationOutboxMessage(
    Guid Id,
    Guid OwnerOrganizationId,
    Guid DriverPositionId,
    string Topic,
    string PayloadJson,
    int Attempts,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset AvailableAt);

public abstract record ParsedBusinessOutboxEvent(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    long AggregateVersion,
    DateTimeOffset OccurredAt);

public sealed record ParsedOrderStatusChanged(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    long AggregateVersion,
    DateTimeOffset OccurredAt,
    Guid OrderEventId,
    string PublicOrderId,
    string PreviousStatus,
    string NewStatus,
    string? PublicEventCode,
    Guid? AuthorizedDriverId,
    Guid? AssignmentId)
    : ParsedBusinessOutboxEvent(
        EventId,
        OwnerOrganizationId,
        OrderId,
        AggregateVersion,
        OccurredAt);

public sealed record ParsedOrderTimelineEventAdded(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    long AggregateVersion,
    DateTimeOffset OccurredAt,
    Guid TimelineEventId,
    string Category,
    string Summary)
    : ParsedBusinessOutboxEvent(
        EventId,
        OwnerOrganizationId,
        OrderId,
        AggregateVersion,
        OccurredAt);

public sealed record ParsedAssignmentChanged(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    long AggregateVersion,
    DateTimeOffset OccurredAt,
    Guid AssignmentId,
    Guid DriverId,
    string AssignmentStatus)
    : ParsedBusinessOutboxEvent(
        EventId,
        OwnerOrganizationId,
        OrderId,
        AggregateVersion,
        OccurredAt);

public sealed record ParsedExternalOfferChanged(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid OfferId,
    long AggregateVersion,
    DateTimeOffset OccurredAt,
    string Status,
    long CommissionCents,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<Guid> AudienceDriverIds)
    : ParsedBusinessOutboxEvent(
        EventId,
        OwnerOrganizationId,
        OfferId,
        AggregateVersion,
        OccurredAt)
{
    public IReadOnlyList<Guid> AudienceDriverIds { get; } = AudienceDriverIds.ToArray();
}

public sealed record ParsedRouteChanged(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid RouteId,
    long AggregateVersion,
    DateTimeOffset OccurredAt,
    IReadOnlyList<Guid> ChangedStopIds)
    : ParsedBusinessOutboxEvent(
        EventId,
        OwnerOrganizationId,
        RouteId,
        AggregateVersion,
        OccurredAt)
{
    public IReadOnlyList<Guid> ChangedStopIds { get; } = ChangedStopIds.ToArray();
}

public sealed record ParsedNotificationStatusChanged(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid NotificationId,
    long AggregateVersion,
    DateTimeOffset OccurredAt,
    string Channel,
    string Status,
    int Attempts)
    : ParsedBusinessOutboxEvent(
        EventId,
        OwnerOrganizationId,
        NotificationId,
        AggregateVersion,
        OccurredAt);

public sealed record ParsedDriverLocationUpdated(
    Guid EventId,
    Guid OwnerOrganizationId,
    Guid DriverPositionId,
    Guid DriverId,
    double Lat,
    double Lng,
    double AccuracyM,
    DateTimeOffset CapturedAt,
    long AggregateVersion);

public sealed record OrderEventEvidence(
    Guid OrderEventId,
    Guid OrderId,
    Guid OwnerOrganizationId,
    long AggregateVersion,
    string EventType,
    string PreviousStatus,
    string NewStatus,
    string? PublicEventCode,
    DateTimeOffset OccurredAt,
    string PublicOrderId);

public sealed record AssignmentEvidence(
    Guid AssignmentId,
    Guid OrderId,
    Guid DriverId,
    Guid OwnerOrganizationId,
    Guid? OperatorOrganizationId,
    string Status,
    long OrderVersion,
    Guid OrderEventId,
    DateTimeOffset OccurredAt,
    bool DriverAudienceAuthorized);

public sealed record DriverPositionEvidence(
    Guid DriverPositionId,
    Guid OwnerOrganizationId,
    Guid DriverId,
    double Lat,
    double Lng,
    double AccuracyM,
    DateTimeOffset CapturedAt,
    bool PublishRealtime);

public sealed record ExternalOfferEvidence(
    Guid OfferId,
    Guid OwnerOrganizationId,
    string Status,
    long CommissionCents,
    DateTimeOffset ExpiresAt,
    int Version,
    Guid? AcceptedByDriverId,
    IReadOnlyList<Guid> AuthorizedAudienceDriverIds)
{
    public IReadOnlyList<Guid> AuthorizedAudienceDriverIds { get; } = AuthorizedAudienceDriverIds.ToArray();
}

public sealed record RouteEvidence(
    Guid RouteId,
    Guid OwnerOrganizationId,
    Guid DriverId,
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Guid> StopIds)
{
    public IReadOnlyList<Guid> StopIds { get; } = StopIds.ToArray();
}

public interface IRealtimeOutboxEvidenceReader
{
    Task<OrderEventEvidence?> ReadOrderEventAsync(
        Guid ownerOrganizationId,
        Guid orderEventId,
        CancellationToken cancellationToken);

    Task<AssignmentEvidence?> ReadAssignmentAsync(
        Guid ownerOrganizationId,
        Guid assignmentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// AI12-ASSIGNMENT-TERMINAL-STATES: evidence for an assignment Dispatch closed (COMPLETED or
    /// CANCELLED) in reaction to the committed order transition recorded at <paramref name="orderVersion"/>.
    /// The driver audience requires an active driver membership, not an active assignment.
    /// </summary>
    Task<AssignmentEvidence?> ReadClosedAssignmentAsync(
        Guid ownerOrganizationId,
        Guid assignmentId,
        long orderVersion,
        CancellationToken cancellationToken);

    Task<bool> IsDriverAudienceAuthorizedAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid assignmentId,
        Guid driverId,
        CancellationToken cancellationToken);

    Task<DriverPositionEvidence?> ReadDriverPositionAsync(
        Guid ownerOrganizationId,
        Guid driverPositionId,
        CancellationToken cancellationToken);

    Task<ExternalOfferEvidence?> ReadExternalOfferAsync(
        Guid ownerOrganizationId,
        Guid offerId,
        IReadOnlyList<Guid> requestedAudienceDriverIds,
        CancellationToken cancellationToken);

    Task<RouteEvidence?> ReadRouteAsync(
        Guid ownerOrganizationId,
        Guid routeId,
        CancellationToken cancellationToken);
}

public enum RealtimeOutboxLane
{
    Business,
    Location,
}

public enum RealtimeOutboxCheckpoint
{
    AfterAllAudiencesPublishedBeforeSettle,
}

public interface IRealtimeOutboxFailureInjector
{
    ValueTask OnCheckpointAsync(
        RealtimeOutboxLane lane,
        Guid outboxId,
        RealtimeOutboxCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public sealed class RealtimeOutboxInjectedFailureException()
    : Exception("A controlled realtime outbox test checkpoint interrupted processing.");

public sealed class OutboxMessageException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public static class RealtimeOutboxRetryPolicy
{
    public static TimeSpan CalculateDelay(
        int attempts,
        int baseSeconds,
        int maximumSeconds,
        double jitterFraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSeconds, baseSeconds);
        if (jitterFraction is < -0.2 or > 0.2)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction));
        }

        var exponent = Math.Min(attempts - 1, 30);
        var unbounded = baseSeconds * Math.Pow(2, exponent);
        var bounded = Math.Min(maximumSeconds, unbounded);
        return TimeSpan.FromSeconds(Math.Clamp(
            bounded * (1 + jitterFraction),
            baseSeconds * 0.8,
            maximumSeconds));
    }
}

public static class RealtimeLocationCursor
{
    public const long JavaScriptMaximumSafeInteger = 9_007_199_254_740_991;

    public static long FromCapturedAt(DateTimeOffset capturedAt)
    {
        if (capturedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The captured timestamp must be UTC.", nameof(capturedAt));
        }

        var value = capturedAt.UtcTicks / TimeSpan.TicksPerMillisecond;
        if (value is < 0 or > JavaScriptMaximumSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(capturedAt));
        }

        return value;
    }
}

public static class RealtimeOutboxEnvelopeFactory
{
    public static RealtimeEnvelope<OrderStatusChangedPayload> Status(ParsedOrderStatusChanged value) =>
        new(
            value.EventId,
            RealtimeEventTypes.OrderStatusChanged,
            value.OccurredAt,
            value.OrderId,
            value.AggregateVersion,
            null,
            new(
                value.OrderId,
                value.PreviousStatus,
                value.NewStatus,
                value.OccurredAt));

    public static RealtimeEnvelope<OrderTimelineEventAddedPayload> Timeline(ParsedOrderTimelineEventAdded value) =>
        new(
            value.EventId,
            RealtimeEventTypes.OrderTimelineEventAdded,
            value.OccurredAt,
            value.OrderId,
            value.AggregateVersion,
            null,
            new(
                value.OrderId,
                value.TimelineEventId,
                value.Category,
                value.Summary,
                value.OccurredAt));

    public static RealtimeEnvelope<AssignmentChangedPayload> Assignment(ParsedAssignmentChanged value) =>
        new(
            value.EventId,
            RealtimeEventTypes.AssignmentChanged,
            value.OccurredAt,
            value.OrderId,
            value.AggregateVersion,
            null,
            new(
                value.OrderId,
                value.AssignmentId,
                value.DriverId,
                value.AssignmentStatus,
                value.OccurredAt));

    public static RealtimeEnvelope<ExternalOfferChangedPayload> ExternalOffer(
        ParsedExternalOfferChanged value) =>
        new(
            value.EventId,
            RealtimeEventTypes.ExternalOfferChanged,
            value.OccurredAt,
            value.OfferId,
            value.AggregateVersion,
            null,
            new(
                value.OfferId,
                value.Status,
                value.CommissionCents,
                value.ExpiresAt));

    public static RealtimeEnvelope<RouteChangedPayload> Route(ParsedRouteChanged value) =>
        new(
            value.EventId,
            RealtimeEventTypes.RouteChanged,
            value.OccurredAt,
            value.RouteId,
            value.AggregateVersion,
            null,
            new(
                value.RouteId,
                value.AggregateVersion,
                value.ChangedStopIds,
                value.OccurredAt));

    public static RealtimeEnvelope<NotificationStatusChangedPayload> Notification(
        ParsedNotificationStatusChanged value) =>
        new(
            value.EventId,
            RealtimeEventTypes.NotificationStatusChanged,
            value.OccurredAt,
            value.NotificationId,
            value.AggregateVersion,
            null,
            new(
                value.NotificationId,
                value.Channel,
                value.Status,
                value.Attempts,
                value.OccurredAt));

    public static RealtimeEnvelope<DriverLocationUpdatedPayload> Location(ParsedDriverLocationUpdated value) =>
        new(
            value.EventId,
            RealtimeEventTypes.DriverLocationUpdated,
            value.CapturedAt,
            value.DriverId,
            value.AggregateVersion,
            null,
            new(
                value.DriverId,
                value.Lat,
                value.Lng,
                value.AccuracyM,
                value.CapturedAt));
}
