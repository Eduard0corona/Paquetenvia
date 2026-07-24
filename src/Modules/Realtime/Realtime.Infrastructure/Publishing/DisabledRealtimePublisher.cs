using Realtime.Application.Events;
using Realtime.Application.Publishing;

namespace Realtime.Infrastructure.Publishing;

internal sealed class DisabledRealtimePublisher : IRealtimePublisher
{
    public Task PublishOperationsOrderStatusChangedAsync(OperationsAudience audience, RealtimeEnvelope<OrderStatusChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsOrderTimelineEventAddedAsync(OperationsAudience audience, RealtimeEnvelope<OrderTimelineEventAddedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsAssignmentChangedAsync(OperationsAudience audience, RealtimeEnvelope<AssignmentChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsRouteChangedAsync(OperationsAudience audience, RealtimeEnvelope<RouteChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsIncidentCreatedAsync(OperationsAudience audience, RealtimeEnvelope<IncidentCreatedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsExternalOfferChangedAsync(OperationsAudience audience, RealtimeEnvelope<ExternalOfferChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsNotificationStatusChangedAsync(OperationsAudience audience, RealtimeEnvelope<NotificationStatusChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishOperationsDriverLocationUpdatedAsync(OperationsAudience audience, RealtimeEnvelope<DriverLocationUpdatedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishDriverAssignmentChangedAsync(DriverAudience audience, RealtimeEnvelope<AssignmentChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishDriverRouteChangedAsync(DriverAudience audience, RealtimeEnvelope<RouteChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishDriverOrderStatusChangedAsync(DriverAudience audience, RealtimeEnvelope<OrderStatusChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishDriverExternalOfferChangedAsync(DriverAudience audience, RealtimeEnvelope<ExternalOfferChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishTrackingPublicOrderStatusChangedAsync(TrackingAudience audience, PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> message, CancellationToken cancellationToken) => Fail();
    public Task PublishTrackingPublicEtaChangedAsync(TrackingAudience audience, PublicRealtimeEnvelope<PublicEtaChangedPayload> message, CancellationToken cancellationToken) => Fail();

    private static Task Fail() => Task.FromException(new RealtimeDisabledException());
}
