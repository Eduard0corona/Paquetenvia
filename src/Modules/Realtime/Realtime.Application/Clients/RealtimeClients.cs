using Realtime.Application.Events;

namespace Realtime.Application.Clients;

public interface IOperationsClient
{
    Task OrderStatusChanged(RealtimeEnvelope<OrderStatusChangedPayload> message);
    Task OrderTimelineEventAdded(RealtimeEnvelope<OrderTimelineEventAddedPayload> message);
    Task AssignmentChanged(RealtimeEnvelope<AssignmentChangedPayload> message);
    Task RouteChanged(RealtimeEnvelope<RouteChangedPayload> message);
    Task IncidentCreated(RealtimeEnvelope<IncidentCreatedPayload> message);
    Task ExternalOfferChanged(RealtimeEnvelope<ExternalOfferChangedPayload> message);
    Task NotificationStatusChanged(RealtimeEnvelope<NotificationStatusChangedPayload> message);
    Task DriverLocationUpdated(RealtimeEnvelope<DriverLocationUpdatedPayload> message);
}

public interface IDriverClient
{
    Task AssignmentChanged(RealtimeEnvelope<AssignmentChangedPayload> message);
    Task RouteChanged(RealtimeEnvelope<RouteChangedPayload> message);
    Task OrderStatusChanged(RealtimeEnvelope<OrderStatusChangedPayload> message);
    Task ExternalOfferChanged(RealtimeEnvelope<ExternalOfferChangedPayload> message);
}

public interface ITrackingClient
{
    Task PublicOrderStatusChanged(PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> message);
    Task PublicEtaChanged(PublicRealtimeEnvelope<PublicEtaChangedPayload> message);
}
