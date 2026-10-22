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

/// <param name="OrderId">NTF-RECIPIENT-NOTICES-2026-10-20 (v2): the order of a recipient notice; absent for IN_APP.</param>
/// <param name="Notice">NTF-RECIPIENT-NOTICES-2026-10-20 (v2): TRACKING_LINK, IN_TRANSIT, DELIVERY_FAILED or DELIVERED for a
/// recipient notice; absent for IN_APP. Never a recipient, a template parameter, a link, a token or a result code.</param>
public sealed record NotificationStatusChangedPayload(
    Guid NotificationId,
    string Channel,
    string Status,
    int Attempts,
    DateTimeOffset OccurredAt,
    Guid? OrderId = null,
    string? Notice = null);

public sealed record PublicEtaChangedPayload(
    string PublicOrderId,
    DateTimeOffset EtaFrom,
    DateTimeOffset EtaTo,
    DateTimeOffset UpdatedAt);
