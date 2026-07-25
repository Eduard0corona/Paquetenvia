using Realtime.Application.Events;

namespace Realtime.Application.Dispatching;

public static class RealtimeOutboxTopics
{
    public const string OrderStatusChanged = "orders.status-changed";
    public const string OrderTimelineEventAdded = "orders.timeline-event-added";
    public const string AssignmentChanged = "dispatch.assignment-changed";
    public const string DriverLocationUpdated = "drivers.location-updated";

    public static bool IsBusinessTopic(string value) =>
        value is OrderStatusChanged or OrderTimelineEventAdded or AssignmentChanged;

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
}

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
