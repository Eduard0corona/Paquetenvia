using Microsoft.AspNetCore.SignalR;
using Realtime.Application.Clients;
using Realtime.Application.Events;
using Realtime.Application.Observability;
using Realtime.Application.Publishing;
using Realtime.Endpoints.Hubs;

namespace Realtime.Infrastructure.Publishing;

internal sealed class SignalRRealtimePublisher(
    IHubContext<OperationsHub, IOperationsClient> operations,
    IHubContext<DriverHub, IDriverClient> drivers,
    IHubContext<TrackingHub, ITrackingClient> tracking,
    IRealtimeTelemetry telemetry) : IRealtimePublisher
{
    public Task PublishOperationsOrderStatusChangedAsync(OperationsAudience audience, RealtimeEnvelope<OrderStatusChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.OrderStatusChanged, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).OrderStatusChanged(message), cancellationToken);

    public Task PublishOperationsOrderTimelineEventAddedAsync(OperationsAudience audience, RealtimeEnvelope<OrderTimelineEventAddedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.OrderTimelineEventAdded, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).OrderTimelineEventAdded(message), cancellationToken);

    public Task PublishOperationsAssignmentChangedAsync(OperationsAudience audience, RealtimeEnvelope<AssignmentChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.AssignmentChanged, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).AssignmentChanged(message), cancellationToken);

    public Task PublishOperationsRouteChangedAsync(OperationsAudience audience, RealtimeEnvelope<RouteChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.RouteChanged, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).RouteChanged(message), cancellationToken);

    public Task PublishOperationsIncidentCreatedAsync(OperationsAudience audience, RealtimeEnvelope<IncidentCreatedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.IncidentCreated, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).IncidentCreated(message), cancellationToken);

    public Task PublishOperationsExternalOfferChangedAsync(OperationsAudience audience, RealtimeEnvelope<ExternalOfferChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.ExternalOfferChanged, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).ExternalOfferChanged(message), cancellationToken);

    public Task PublishOperationsNotificationStatusChangedAsync(OperationsAudience audience, RealtimeEnvelope<NotificationStatusChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.NotificationStatusChanged, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).NotificationStatusChanged(message), cancellationToken);

    public Task PublishOperationsDriverLocationUpdatedAsync(OperationsAudience audience, RealtimeEnvelope<DriverLocationUpdatedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.DriverLocationUpdated, message.EventType, () => operations.Clients.Group(RequireGroup(audience.GroupName)).DriverLocationUpdated(message), cancellationToken);

    public Task PublishDriverAssignmentChangedAsync(DriverAudience audience, RealtimeEnvelope<AssignmentChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.AssignmentChanged, message.EventType, () => drivers.Clients.Group(RequireGroup(audience.GroupName)).AssignmentChanged(message), cancellationToken);

    public Task PublishDriverRouteChangedAsync(DriverAudience audience, RealtimeEnvelope<RouteChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.RouteChanged, message.EventType, () => drivers.Clients.Group(RequireGroup(audience.GroupName)).RouteChanged(message), cancellationToken);

    public Task PublishDriverOrderStatusChangedAsync(DriverAudience audience, RealtimeEnvelope<OrderStatusChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.OrderStatusChanged, message.EventType, () => drivers.Clients.Group(RequireGroup(audience.GroupName)).OrderStatusChanged(message), cancellationToken);

    public Task PublishDriverExternalOfferChangedAsync(DriverAudience audience, RealtimeEnvelope<ExternalOfferChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.ExternalOfferChanged, message.EventType, () => drivers.Clients.Group(RequireGroup(audience.GroupName)).ExternalOfferChanged(message), cancellationToken);

    public Task PublishTrackingPublicOrderStatusChangedAsync(TrackingAudience audience, PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.PublicOrderStatusChanged, message.EventType, () => tracking.Clients.Group(RequireGroup(audience.GroupName)).PublicOrderStatusChanged(message), cancellationToken);

    public Task PublishTrackingPublicEtaChangedAsync(TrackingAudience audience, PublicRealtimeEnvelope<PublicEtaChangedPayload> message, CancellationToken cancellationToken) =>
        PublishAsync(audience.GroupName, RealtimeEventTypes.PublicEtaChanged, message.EventType, () => tracking.Clients.Group(RequireGroup(audience.GroupName)).PublicEtaChanged(message), cancellationToken);

    private async Task PublishAsync(
        string? group,
        string expectedEventType,
        string actualEventType,
        Func<Task> send,
        CancellationToken cancellationToken)
    {
        _ = RequireGroup(group);
        if (!string.Equals(expectedEventType, actualEventType, StringComparison.Ordinal))
        {
            throw new ArgumentException("The envelope event type does not match the publisher operation.", nameof(actualEventType));
        }

        using var measurement = telemetry.MeasurePublication(expectedEventType);
        try
        {
            await send().WaitAsync(cancellationToken);
            telemetry.PublicationSucceeded(expectedEventType);
        }
        catch
        {
            telemetry.PublicationFailed(expectedEventType);
            throw;
        }
    }

    private static string RequireGroup(string? group) =>
        !string.IsNullOrEmpty(group)
            ? group
            : throw new ArgumentException("A typed audience is required.", nameof(group));
}
