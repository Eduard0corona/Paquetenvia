using Realtime.Application.Events;

namespace Realtime.Application.Publishing;

public interface IRealtimePublisher
{
    Task PublishOperationsOrderStatusChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<OrderStatusChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsOrderTimelineEventAddedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<OrderTimelineEventAddedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsAssignmentChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<AssignmentChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsRouteChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<RouteChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsIncidentCreatedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<IncidentCreatedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsExternalOfferChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<ExternalOfferChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsNotificationStatusChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<NotificationStatusChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishOperationsDriverLocationUpdatedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<DriverLocationUpdatedPayload> message,
        CancellationToken cancellationToken);

    Task PublishDriverAssignmentChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<AssignmentChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishDriverRouteChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<RouteChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishDriverOrderStatusChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<OrderStatusChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishDriverExternalOfferChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<ExternalOfferChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishTrackingPublicOrderStatusChangedAsync(
        TrackingAudience audience,
        PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> message,
        CancellationToken cancellationToken);

    Task PublishTrackingPublicEtaChangedAsync(
        TrackingAudience audience,
        PublicRealtimeEnvelope<PublicEtaChangedPayload> message,
        CancellationToken cancellationToken);
}

public sealed class RealtimeDisabledException : InvalidOperationException
{
    public RealtimeDisabledException()
        : base("Realtime publication is disabled.")
    {
    }
}
