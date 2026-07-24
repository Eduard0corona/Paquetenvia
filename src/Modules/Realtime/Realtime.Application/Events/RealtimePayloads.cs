namespace Realtime.Application.Events;

public sealed record OrderStatusChangedPayload(
    Guid OrderId,
    string PreviousStatus,
    string NewStatus,
    DateTimeOffset OccurredAt);

public sealed record PublicOrderStatusChangedPayload(
    string PublicOrderId,
    string PublicStatus,
    DateTimeOffset OccurredAt);

public sealed record OrderTimelineEventAddedPayload(
    Guid OrderId,
    Guid TimelineEventId,
    string Category,
    string Summary,
    DateTimeOffset OccurredAt);

public sealed record AssignmentChangedPayload(
    Guid OrderId,
    Guid AssignmentId,
    Guid DriverId,
    string AssignmentStatus,
    DateTimeOffset OccurredAt);

public sealed record RouteChangedPayload(
    Guid RouteId,
    long RouteVersion,
    IReadOnlyList<Guid> ChangedStopIds,
    DateTimeOffset OccurredAt);

public sealed record DriverLocationUpdatedPayload(
    Guid DriverId,
    double Lat,
    double Lng,
    double AccuracyM,
    DateTimeOffset CapturedAt);

public sealed record IncidentCreatedPayload(
    Guid IncidentId,
    Guid OrderId,
    string Type,
    string Severity,
    string Status,
    DateTimeOffset OccurredAt);

public sealed record ExternalOfferChangedPayload(
    Guid OfferId,
    string Status,
    long CommissionCents,
    DateTimeOffset ExpiresAt);

public sealed record NotificationStatusChangedPayload(
    Guid NotificationId,
    string Channel,
    string Status,
    int Attempts,
    DateTimeOffset OccurredAt);

public sealed record PublicEtaChangedPayload(
    string PublicOrderId,
    DateTimeOffset EtaFrom,
    DateTimeOffset EtaTo,
    DateTimeOffset UpdatedAt);
