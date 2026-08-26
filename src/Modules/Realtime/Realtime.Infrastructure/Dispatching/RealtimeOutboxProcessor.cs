using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Tracking;
using Paqueteria.Application;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;
using Realtime.Application.Events;
using Realtime.Application.Publishing;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class RealtimeOutboxProcessor(
    IRealtimeOutboxStore store,
    IRealtimeOutboxEvidenceReader evidence,
    IRealtimePublisher publisher,
    IRealtimeOutboxFailureInjector failureInjector,
    PublicOrderStatusPolicy publicStatusPolicy,
    IOptions<OutboxDispatcherOptions> options,
    RealtimeOutboxTelemetry telemetry)
{
    public async Task ProcessBusinessAsync(
        ClaimedBusinessOutboxMessage message,
        CancellationToken cancellationToken)
    {
        const string lane = "business";
        telemetry.BeginMessage(lane);
        try
        {
            ParsedBusinessOutboxEvent parsed;
            try
            {
                parsed = RealtimeOutboxParser.Parse(message);
            }
            catch (OutboxMessageException exception)
            {
                telemetry.MappingFailed(lane, exception.ErrorCode);
                await SettleBusinessAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds));
                switch (parsed)
                {
                    case ParsedOrderStatusChanged status:
                        await PublishStatusAsync(status, timeout.Token);
                        break;
                    case ParsedOrderTimelineEventAdded timeline:
                        await PublishTimelineAsync(timeline, timeout.Token);
                        break;
                    case ParsedAssignmentChanged assignment:
                        await PublishAssignmentAsync(assignment, timeout.Token);
                        break;
                    case ParsedNotificationStatusChanged notification:
                        await PublishNotificationAsync(notification, timeout.Token);
                        break;
                    default:
                        throw new OutboxMessageException(RealtimeOutboxErrorCodes.UnknownTopic);
                }

                telemetry.Published(lane, EventType(parsed));
                await failureInjector.OnCheckpointAsync(
                    RealtimeOutboxLane.Business,
                    message.Id,
                    RealtimeOutboxCheckpoint.AfterAllAudiencesPublishedBeforeSettle,
                    cancellationToken);
                await SettleBusinessAsync(message, "PROCESSED", null, null, cancellationToken);
            }
            catch (RealtimeOutboxInjectedFailureException)
            {
                throw;
            }
            catch (OutboxMessageException exception)
            {
                telemetry.MappingFailed(lane, exception.ErrorCode);
                await SettleBusinessAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await RetryBusinessAsync(message, RealtimeOutboxErrorCodes.PublishTimeout, cancellationToken);
            }
            catch (NpgsqlException exception) when (exception.IsTransient)
            {
                await RetryBusinessAsync(message, RealtimeOutboxErrorCodes.DatabaseTransient, cancellationToken);
            }
            catch (TimeoutException)
            {
                await RetryBusinessAsync(message, RealtimeOutboxErrorCodes.PublishTimeout, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await RetryBusinessAsync(message, RealtimeOutboxErrorCodes.SignalRTransient, cancellationToken);
            }
        }
        finally
        {
            telemetry.EndMessage(lane);
        }
    }

    public async Task ProcessLocationAsync(
        ClaimedLocationOutboxMessage message,
        CancellationToken cancellationToken)
    {
        const string lane = "location";
        telemetry.BeginMessage(lane);
        try
        {
            ParsedDriverLocationUpdated parsed;
            try
            {
                parsed = RealtimeOutboxParser.Parse(message);
            }
            catch (OutboxMessageException exception)
            {
                telemetry.MappingFailed(lane, exception.ErrorCode);
                await SettleLocationAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
                return;
            }

            try
            {
                var persisted = await evidence.ReadDriverPositionAsync(
                    parsed.OwnerOrganizationId,
                    parsed.DriverPositionId,
                    cancellationToken);
                if (persisted is null ||
                    !persisted.PublishRealtime ||
                    persisted.DriverPositionId != parsed.DriverPositionId ||
                    persisted.OwnerOrganizationId != parsed.OwnerOrganizationId ||
                    persisted.DriverId != parsed.DriverId ||
                    !UtcMicrosecondPrecision.AreEqual(
                        persisted.CapturedAt,
                        parsed.CapturedAt) ||
                    !SameCoordinate(persisted.Lat, parsed.Lat) ||
                    !SameCoordinate(persisted.Lng, parsed.Lng) ||
                    !SameCoordinate(persisted.AccuracyM, parsed.AccuracyM))
                {
                    throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidPayload);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds));
                var eventType = RealtimeEventTypes.DriverLocationUpdated;
                using (telemetry.MeasurePublish(lane, eventType))
                {
                    await publisher.PublishOperationsDriverLocationUpdatedAsync(
                        OperationsAudience.ForOrganization(parsed.OwnerOrganizationId),
                        RealtimeOutboxEnvelopeFactory.Location(parsed),
                        timeout.Token);
                }

                telemetry.AudienceDelivered(lane, eventType, "operations", "published");
                telemetry.Published(lane, eventType);
                await failureInjector.OnCheckpointAsync(
                    RealtimeOutboxLane.Location,
                    message.Id,
                    RealtimeOutboxCheckpoint.AfterAllAudiencesPublishedBeforeSettle,
                    cancellationToken);
                await SettleLocationAsync(message, "PROCESSED", null, null, cancellationToken);
            }
            catch (RealtimeOutboxInjectedFailureException)
            {
                throw;
            }
            catch (OutboxMessageException exception)
            {
                telemetry.MappingFailed(lane, exception.ErrorCode);
                await SettleLocationAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await RetryLocationAsync(message, RealtimeOutboxErrorCodes.PublishTimeout, cancellationToken);
            }
            catch (NpgsqlException exception) when (exception.IsTransient)
            {
                await RetryLocationAsync(message, RealtimeOutboxErrorCodes.DatabaseTransient, cancellationToken);
            }
            catch (TimeoutException)
            {
                await RetryLocationAsync(message, RealtimeOutboxErrorCodes.PublishTimeout, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await RetryLocationAsync(message, RealtimeOutboxErrorCodes.SignalRTransient, cancellationToken);
            }
        }
        finally
        {
            telemetry.EndMessage(lane);
        }
    }

    private async Task PublishStatusAsync(
        ParsedOrderStatusChanged value,
        CancellationToken cancellationToken)
    {
        var persisted = await evidence.ReadOrderEventAsync(
            value.OwnerOrganizationId,
            value.OrderEventId,
            cancellationToken);
        ValidateOrderEvidence(
            persisted,
            value.OrderEventId,
            value.OrderId,
            value.OwnerOrganizationId,
            value.AggregateVersion,
            value.PreviousStatus,
            value.NewStatus,
            value.PublicEventCode,
            value.OccurredAt,
            value.PublicOrderId);
        ValidatePublicEventCode(value.NewStatus, value.PublicEventCode);

        const string lane = "business";
        var eventType = RealtimeEventTypes.OrderStatusChanged;
        var message = RealtimeOutboxEnvelopeFactory.Status(value);
        using (telemetry.MeasurePublish(lane, eventType))
        {
            await publisher.PublishOperationsOrderStatusChangedAsync(
                OperationsAudience.ForOrganization(value.OwnerOrganizationId),
                message,
                cancellationToken);
        }

        telemetry.AudienceDelivered(lane, eventType, "operations", "published");
        if (value.AuthorizedDriverId is { } driverId &&
            value.AssignmentId is { } assignmentId)
        {
            var authorized = await evidence.IsDriverAudienceAuthorizedAsync(
                value.OwnerOrganizationId,
                value.OrderId,
                assignmentId,
                driverId,
                cancellationToken);
            if (authorized)
            {
                using (telemetry.MeasurePublish(lane, eventType))
                {
                    await publisher.PublishDriverOrderStatusChangedAsync(
                        DriverAudience.ForDriver(driverId),
                        message,
                        cancellationToken);
                }

                telemetry.AudienceDelivered(lane, eventType, "driver", "published");
            }
            else
            {
                telemetry.AudienceDelivered(lane, eventType, "driver", "driver_audience_skipped");
            }
        }

        if (value.PublicEventCode is not null)
        {
            var publicStatus = PublicOrderStatusPolicy.ToContractValue(
                publicStatusPolicy.Map(value.NewStatus));
            var publicMessage = new PublicRealtimeEnvelope<PublicOrderStatusChangedPayload>(
                value.EventId,
                RealtimeEventTypes.PublicOrderStatusChanged,
                value.OccurredAt,
                value.PublicOrderId,
                value.AggregateVersion,
                null,
                new(value.PublicOrderId, publicStatus, value.OccurredAt));
            using (telemetry.MeasurePublish(lane, RealtimeEventTypes.PublicOrderStatusChanged))
            {
                await publisher.PublishTrackingPublicOrderStatusChangedAsync(
                    TrackingAudience.ForPublicOrder(value.PublicOrderId),
                    publicMessage,
                    cancellationToken);
            }

            telemetry.AudienceDelivered(
                lane,
                RealtimeEventTypes.PublicOrderStatusChanged,
                "tracking",
                "published");
        }
    }

    private async Task PublishTimelineAsync(
        ParsedOrderTimelineEventAdded value,
        CancellationToken cancellationToken)
    {
        var persisted = await evidence.ReadOrderEventAsync(
            value.OwnerOrganizationId,
            value.TimelineEventId,
            cancellationToken);
        if (persisted is null ||
            persisted.OrderEventId != value.TimelineEventId ||
            persisted.OrderId != value.OrderId ||
            persisted.OwnerOrganizationId != value.OwnerOrganizationId ||
            persisted.AggregateVersion != value.AggregateVersion ||
            persisted.EventType != "ORDER_STATUS_CHANGED" ||
            !UtcMicrosecondPrecision.AreEqual(
                persisted.OccurredAt,
                value.OccurredAt) ||
            value.Summary != RealtimeOutboxParser.TimelineSummary(persisted.NewStatus))
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidPayload);
        }

        const string lane = "business";
        var eventType = RealtimeEventTypes.OrderTimelineEventAdded;
        using (telemetry.MeasurePublish(lane, eventType))
        {
            await publisher.PublishOperationsOrderTimelineEventAddedAsync(
                OperationsAudience.ForOrganization(value.OwnerOrganizationId),
                RealtimeOutboxEnvelopeFactory.Timeline(value),
                cancellationToken);
        }

        telemetry.AudienceDelivered(lane, eventType, "operations", "published");
    }

    private async Task PublishAssignmentAsync(
        ParsedAssignmentChanged value,
        CancellationToken cancellationToken)
    {
        var persisted = await evidence.ReadAssignmentAsync(
            value.OwnerOrganizationId,
            value.AssignmentId,
            cancellationToken);
        if (persisted is null ||
            persisted.AssignmentId != value.AssignmentId ||
            persisted.OrderId != value.OrderId ||
            persisted.DriverId != value.DriverId ||
            persisted.OwnerOrganizationId != value.OwnerOrganizationId ||
            persisted.OrderVersion != value.AggregateVersion ||
            !UtcMicrosecondPrecision.AreEqual(
                persisted.OccurredAt,
                value.OccurredAt))
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidPayload);
        }

        const string lane = "business";
        var eventType = RealtimeEventTypes.AssignmentChanged;
        var message = RealtimeOutboxEnvelopeFactory.Assignment(value);
        using (telemetry.MeasurePublish(lane, eventType))
        {
            await publisher.PublishOperationsAssignmentChangedAsync(
                OperationsAudience.ForOrganization(value.OwnerOrganizationId),
                message,
                cancellationToken);
        }

        telemetry.AudienceDelivered(lane, eventType, "operations", "published");
        if (persisted.DriverAudienceAuthorized)
        {
            using (telemetry.MeasurePublish(lane, eventType))
            {
                await publisher.PublishDriverAssignmentChangedAsync(
                    DriverAudience.ForDriver(value.DriverId),
                    message,
                    cancellationToken);
            }

            telemetry.AudienceDelivered(lane, eventType, "driver", "published");
        }
        else
        {
            telemetry.AudienceDelivered(lane, eventType, "driver", "driver_audience_skipped");
        }
    }

    private async Task PublishNotificationAsync(
        ParsedNotificationStatusChanged value,
        CancellationToken cancellationToken)
    {
        const string lane = "business";
        var eventType = RealtimeEventTypes.NotificationStatusChanged;
        using (telemetry.MeasurePublish(lane, eventType))
        {
            await publisher.PublishOperationsNotificationStatusChangedAsync(
                OperationsAudience.ForOrganization(value.OwnerOrganizationId),
                RealtimeOutboxEnvelopeFactory.Notification(value),
                cancellationToken);
        }

        telemetry.AudienceDelivered(lane, eventType, "operations", "published");
    }

    private static void ValidateOrderEvidence(
        OrderEventEvidence? persisted,
        Guid orderEventId,
        Guid orderId,
        Guid ownerOrganizationId,
        long aggregateVersion,
        string previousStatus,
        string newStatus,
        string? publicEventCode,
        DateTimeOffset occurredAt,
        string publicOrderId)
    {
        if (persisted is null ||
            persisted.OrderEventId != orderEventId ||
            persisted.OrderId != orderId ||
            persisted.OwnerOrganizationId != ownerOrganizationId ||
            persisted.AggregateVersion != aggregateVersion ||
            persisted.EventType != "ORDER_STATUS_CHANGED" ||
            persisted.PreviousStatus != previousStatus ||
            persisted.NewStatus != newStatus ||
            persisted.PublicEventCode != publicEventCode ||
            !UtcMicrosecondPrecision.AreEqual(
                persisted.OccurredAt,
                occurredAt) ||
            persisted.PublicOrderId != publicOrderId)
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidPayload);
        }
    }

    private static void ValidatePublicEventCode(string newStatus, string? actual)
    {
        var expected = newStatus switch
        {
            "READY_FOR_PICKUP" => "PICKUP_SCHEDULED",
            "PICKED_UP" => "PICKED_UP",
            "IN_TRANSIT" => "IN_TRANSIT",
            "DELIVERING" => "OUT_FOR_DELIVERY",
            "FAILED_ATTEMPT" => "DELIVERY_ATTEMPTED",
            "RESCHEDULED" => "RESCHEDULED",
            "DELIVERED" => "DELIVERED",
            "RETURNING" => "RETURNING",
            "RETURNED" => "RETURNED",
            "CANCELLED" => "CANCELLED",
            _ => null,
        };
        if (expected != actual)
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidPayload);
        }
    }

    private async Task RetryBusinessAsync(
        ClaimedBusinessOutboxMessage message,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (message.Attempts >= options.Value.Business.MaximumAttempts)
        {
            await SettleBusinessAsync(message, "DEAD", errorCode, null, cancellationToken);
            return;
        }

        await SettleBusinessAsync(
            message,
            "RETRY",
            errorCode,
            DateTimeOffset.UtcNow + RetryDelay(message.Attempts),
            cancellationToken);
    }

    private async Task RetryLocationAsync(
        ClaimedLocationOutboxMessage message,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (message.Attempts >= options.Value.Location.MaximumAttempts)
        {
            await SettleLocationAsync(message, "DEAD", errorCode, null, cancellationToken);
            return;
        }

        await SettleLocationAsync(
            message,
            "RETRY",
            errorCode,
            DateTimeOffset.UtcNow + RetryDelay(message.Attempts),
            cancellationToken);
    }

    private TimeSpan RetryDelay(int attempts) =>
        RealtimeOutboxRetryPolicy.CalculateDelay(
            attempts,
            options.Value.RetryBaseSeconds,
            options.Value.RetryMaximumSeconds,
            Random.Shared.NextDouble() * 0.4 - 0.2);

    private async Task SettleBusinessAsync(
        ClaimedBusinessOutboxMessage message,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        var settled = await store.SettleBusinessAsync(
            message.Id,
            message.LeaseToken,
            status,
            errorCode,
            availableAt,
            cancellationToken);
        RecordSettlement("business", status, errorCode, settled);
    }

    private async Task SettleLocationAsync(
        ClaimedLocationOutboxMessage message,
        string status,
        string? errorCode,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        var settled = await store.SettleLocationAsync(
            message.Id,
            message.LeaseToken,
            status,
            errorCode,
            availableAt,
            cancellationToken);
        RecordSettlement("location", status, errorCode, settled);
    }

    private void RecordSettlement(string lane, string status, string? errorCode, bool settled)
    {
        if (!settled)
        {
            telemetry.LeaseLost(lane);
            return;
        }

        telemetry.Settled(lane, status.ToLowerInvariant(), errorCode);
    }

    private static string EventType(ParsedBusinessOutboxEvent value) => value switch
    {
        ParsedOrderStatusChanged => RealtimeEventTypes.OrderStatusChanged,
        ParsedOrderTimelineEventAdded => RealtimeEventTypes.OrderTimelineEventAdded,
        ParsedAssignmentChanged => RealtimeEventTypes.AssignmentChanged,
        ParsedNotificationStatusChanged => RealtimeEventTypes.NotificationStatusChanged,
        _ => throw new OutboxMessageException(RealtimeOutboxErrorCodes.UnknownTopic),
    };

    private static bool SameCoordinate(double left, double right) =>
        Math.Abs(left - right) <= 0.000_001;
}
