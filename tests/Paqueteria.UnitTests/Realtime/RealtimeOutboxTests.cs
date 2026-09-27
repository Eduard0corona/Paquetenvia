using System.Text.Json;
using System.Text.Json.Serialization;
using Paqueteria.Application;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;
using Realtime.Application.Events;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.UnitTests.Realtime;

public sealed class RealtimeOutboxTests
{
    private static readonly Guid EventId = Guid.Parse("00000000-0000-0000-0000-000000000801");
    private static readonly Guid OrganizationId = Guid.Parse("00000000-0000-0000-0000-000000000802");
    private static readonly Guid OrderId = Guid.Parse("00000000-0000-0000-0000-000000000803");
    private static readonly Guid OrderEventId = Guid.Parse("00000000-0000-0000-0000-000000000804");
    private static readonly Guid AssignmentId = Guid.Parse("00000000-0000-0000-0000-000000000805");
    private static readonly Guid DriverId = Guid.Parse("00000000-0000-0000-0000-000000000806");
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 25, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RealtimeOutboxTopics.OrderStatusChanged)]
    [InlineData(RealtimeOutboxTopics.OrderTimelineEventAdded)]
    [InlineData(RealtimeOutboxTopics.AssignmentChanged)]
    [InlineData(RealtimeOutboxTopics.ExternalOfferChanged)]
    [InlineData(RealtimeOutboxTopics.RouteChanged)]
    [InlineData(RealtimeOutboxTopics.NotificationStatusChanged)]
    public void Business_topic_allowlist_is_exact(string topic)
    {
        Assert.True(RealtimeOutboxTopics.IsBusinessTopic(topic));
    }

    [Fact]
    public void Unknown_topic_is_permanent()
    {
        var exception = Assert.Throws<OutboxMessageException>(() =>
            RealtimeOutboxParser.Parse(Business("orders.unknown", "{}")));

        Assert.Equal(RealtimeOutboxErrorCodes.UnknownTopic, exception.ErrorCode);
    }

    [Fact]
    public void Status_payload_maps_stable_outbox_id_and_exact_metadata()
    {
        var parsed = Assert.IsType<ParsedOrderStatusChanged>(
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.OrderStatusChanged,
                JsonSerializer.Serialize(new
                {
                    schema_version = "order-status-changed-v1",
                    order_event_id = OrderEventId,
                    order_id = OrderId,
                    public_order_id = "ORD_abcdefghijklmnopqrstuv",
                    previous_status = "IN_TRANSIT",
                    new_status = "DELIVERING",
                    occurred_at = OccurredAt,
                    public_event_code = "OUT_FOR_DELIVERY",
                    authorized_driver_id = DriverId,
                    assignment_id = AssignmentId,
                }))));

        Assert.Equal(EventId, parsed.EventId);
        Assert.Equal(12, parsed.AggregateVersion);
        Assert.Equal(OrderEventId, parsed.OrderEventId);
        var envelope = RealtimeOutboxEnvelopeFactory.Status(parsed);
        Assert.Equal(EventId, envelope.EventId);
        Assert.Null(envelope.CorrelationId);
        Assert.Equal(RealtimeEventTypes.OrderStatusChanged, envelope.EventType);
    }

    [Fact]
    public void Timeline_and_assignment_parsers_are_explicit()
    {
        var timeline = RealtimeOutboxParser.Parse(Business(
            RealtimeOutboxTopics.OrderTimelineEventAdded,
            JsonSerializer.Serialize(new
            {
                schema_version = "order-timeline-event-added-v1",
                order_id = OrderId,
                timeline_event_id = OrderEventId,
                category = "ORDER_STATUS",
                summary = "Order status changed to ASSIGNED.",
                occurred_at = OccurredAt,
            })));
        var assignment = RealtimeOutboxParser.Parse(Business(
            RealtimeOutboxTopics.AssignmentChanged,
            JsonSerializer.Serialize(new
            {
                schema_version = "assignment-changed-v1",
                order_id = OrderId,
                assignment_id = AssignmentId,
                driver_id = DriverId,
                assignment_status = "ACCEPTED",
                occurred_at = OccurredAt,
            })));

        Assert.IsType<ParsedOrderTimelineEventAdded>(timeline);
        Assert.IsType<ParsedAssignmentChanged>(assignment);
    }

    [Fact]
    public void External_offer_parser_keeps_internal_audience_out_of_the_public_envelope()
    {
        var offerId = Guid.Parse("00000000-0000-0000-0000-000000000809");
        var parsed = Assert.IsType<ParsedExternalOfferChanged>(
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.ExternalOfferChanged,
                JsonSerializer.Serialize(new
                {
                    schema_version = "external-offer-changed-v1",
                    offer_id = offerId,
                    status = "OPEN",
                    commission_cents = 12_345,
                    expires_at = OccurredAt.AddHours(2),
                    audience_driver_ids = new[] { DriverId },
                })) with
                {
                    AggregateType = "ExternalOffer",
                    AggregateId = offerId,
                }));

        var envelope = RealtimeOutboxEnvelopeFactory.ExternalOffer(parsed);
        Assert.Equal([DriverId], parsed.AudienceDriverIds);
        Assert.Equal(12_345, envelope.Payload.CommissionCents);
        Assert.DoesNotContain("audience", JsonSerializer.Serialize(envelope), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Route_parser_maps_exact_non_PII_payload_and_version()
    {
        var routeId = Guid.Parse("00000000-0000-0000-0000-000000000810");
        var stopIds = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-000000000811"),
            Guid.Parse("00000000-0000-0000-0000-000000000812"),
        };
        var parsed = Assert.IsType<ParsedRouteChanged>(
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.RouteChanged,
                JsonSerializer.Serialize(new
                {
                    schema_version = "route-changed-v1",
                    route_id = routeId,
                    route_version = 12,
                    changed_stop_ids = stopIds,
                    occurred_at = OccurredAt,
                })) with
                {
                    AggregateType = "Route",
                    AggregateId = routeId,
                }));

        var envelope = RealtimeOutboxEnvelopeFactory.Route(parsed);
        Assert.Equal(routeId, envelope.Payload.RouteId);
        Assert.Equal(12, envelope.Payload.RouteVersion);
        Assert.Equal(stopIds, envelope.Payload.ChangedStopIds);
        Assert.Equal(RealtimeEventTypes.RouteChanged, envelope.EventType);
        Assert.DoesNotContain("driver", JsonSerializer.Serialize(envelope), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Notification_status_parser_reuses_the_operations_contract_and_aggregate_version()
    {
        var notificationId = Guid.Parse("00000000-0000-0000-0000-000000000808");
        var claimed = Business(
            RealtimeOutboxTopics.NotificationStatusChanged,
            JsonSerializer.Serialize(new
            {
                schema_version = "notification-status-changed-v1",
                notification_id = notificationId,
                channel = "IN_APP",
                status = "SENT",
                attempts = 1,
                occurred_at = OccurredAt,
            })) with
        {
            AggregateType = "Notification",
            AggregateId = notificationId,
            AggregateVersion = 2,
        };

        var parsed = Assert.IsType<ParsedNotificationStatusChanged>(RealtimeOutboxParser.Parse(claimed));
        var envelope = RealtimeOutboxEnvelopeFactory.Notification(parsed);

        Assert.Equal(RealtimeEventTypes.NotificationStatusChanged, envelope.EventType);
        Assert.Equal(notificationId, envelope.AggregateId);
        Assert.Equal(2, envelope.AggregateVersion);
        Assert.Equal(new NotificationStatusChangedPayload(notificationId, "IN_APP", "SENT", 1, OccurredAt), envelope.Payload);
    }

    [Fact]
    public void Parsers_and_envelopes_canonicalize_all_shared_timestamps_to_microseconds()
    {
        var raw = OccurredAt.AddTicks(7);
        var canonical = UtcMicrosecondPrecision.Normalize(raw);
        var status = Assert.IsType<ParsedOrderStatusChanged>(
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.OrderStatusChanged,
                JsonSerializer.Serialize(new
                {
                    schema_version = "order-status-changed-v1",
                    order_event_id = OrderEventId,
                    order_id = OrderId,
                    public_order_id = "ORD_abcdefghijklmnopqrstuv",
                    previous_status = "IN_TRANSIT",
                    new_status = "DELIVERING",
                    occurred_at = raw,
                    public_event_code = "OUT_FOR_DELIVERY",
                    authorized_driver_id = DriverId,
                    assignment_id = AssignmentId,
                }))));
        var timeline = Assert.IsType<ParsedOrderTimelineEventAdded>(
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.OrderTimelineEventAdded,
                JsonSerializer.Serialize(new
                {
                    schema_version = "order-timeline-event-added-v1",
                    order_id = OrderId,
                    timeline_event_id = OrderEventId,
                    category = "ORDER_STATUS",
                    summary = "Order status changed to DELIVERING.",
                    occurred_at = raw,
                }))));
        var assignment = Assert.IsType<ParsedAssignmentChanged>(
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.AssignmentChanged,
                JsonSerializer.Serialize(new
                {
                    schema_version = "assignment-changed-v1",
                    order_id = OrderId,
                    assignment_id = AssignmentId,
                    driver_id = DriverId,
                    assignment_status = "ACCEPTED",
                    occurred_at = raw,
                }))));
        var location = RealtimeOutboxParser.Parse(Location(
            JsonSerializer.Serialize(new
            {
                schema_version = "driver-location-updated-v1",
                driver_position_id = OrderEventId,
                driver_id = DriverId,
                lat = 24.809064,
                lng = -107.394011,
                accuracy_m = 7.5,
                captured_at = raw,
            })));

        Assert.Equal(canonical, status.OccurredAt);
        Assert.Equal(canonical, timeline.OccurredAt);
        Assert.Equal(canonical, assignment.OccurredAt);
        Assert.Equal(canonical, location.CapturedAt);
        Assert.Equal(0, canonical.UtcTicks % 10);

        var statusEnvelope = RealtimeOutboxEnvelopeFactory.Status(status);
        var timelineEnvelope = RealtimeOutboxEnvelopeFactory.Timeline(timeline);
        var assignmentEnvelope = RealtimeOutboxEnvelopeFactory.Assignment(assignment);
        var locationEnvelope = RealtimeOutboxEnvelopeFactory.Location(location);
        Assert.Equal(statusEnvelope.OccurredAt, statusEnvelope.Payload.OccurredAt);
        Assert.Equal(timelineEnvelope.OccurredAt, timelineEnvelope.Payload.OccurredAt);
        Assert.Equal(assignmentEnvelope.OccurredAt, assignmentEnvelope.Payload.OccurredAt);
        Assert.Equal(locationEnvelope.OccurredAt, locationEnvelope.Payload.CapturedAt);
    }

    [Fact]
    public void Parser_rejects_non_utc_timestamp_instead_of_converting_it()
    {
        var exception = Assert.Throws<OutboxMessageException>(() =>
            RealtimeOutboxParser.Parse(Business(
                RealtimeOutboxTopics.OrderTimelineEventAdded,
                JsonSerializer.Serialize(new
                {
                    schema_version = "order-timeline-event-added-v1",
                    order_id = OrderId,
                    timeline_event_id = OrderEventId,
                    category = "ORDER_STATUS",
                    summary = "Order status changed to DELIVERING.",
                    occurred_at = OccurredAt.ToOffset(TimeSpan.FromHours(-7)),
                }))));

        Assert.Equal(RealtimeOutboxErrorCodes.InvalidPayload, exception.ErrorCode);
    }

    [Theory]
    [InlineData("order-status-changed-v2", RealtimeOutboxErrorCodes.InvalidSchema)]
    [InlineData("order-status-changed-v1", RealtimeOutboxErrorCodes.InvalidPayload)]
    public void Status_rejects_unknown_schema_and_properties(string schema, string code)
    {
        var payload = JsonSerializer.Serialize(
            new
            {
                schema_version = schema,
                order_event_id = OrderEventId,
                order_id = OrderId,
                public_order_id = "ORD_abcdefghijklmnopqrstuv",
                previous_status = "IN_TRANSIT",
                new_status = "DELIVERING",
                occurred_at = OccurredAt,
                public_event_code = "OUT_FOR_DELIVERY",
                authorized_driver_id = DriverId,
                assignment_id = AssignmentId,
                unexpected = schema == "order-status-changed-v1" ? true : (bool?)null,
            },
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });

        var exception = Assert.Throws<OutboxMessageException>(() =>
            RealtimeOutboxParser.Parse(Business(RealtimeOutboxTopics.OrderStatusChanged, payload)));
        Assert.Equal(code, exception.ErrorCode);
    }

    [Fact]
    public void Location_cursor_is_deterministic_safe_and_monotonic()
    {
        var first = RealtimeLocationCursor.FromCapturedAt(OccurredAt);
        var second = RealtimeLocationCursor.FromCapturedAt(OccurredAt.AddMilliseconds(1));

        Assert.Equal(first + 1, second);
        Assert.InRange(first, 0, RealtimeLocationCursor.JavaScriptMaximumSafeInteger);
        Assert.Equal(first, RealtimeLocationCursor.FromCapturedAt(OccurredAt));
    }

    [Fact]
    public void Retry_backoff_is_bounded_with_allowlisted_jitter()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.6), RealtimeOutboxRetryPolicy.CalculateDelay(1, 2, 60, -0.2));
        Assert.Equal(TimeSpan.FromSeconds(60), RealtimeOutboxRetryPolicy.CalculateDelay(20, 2, 60, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RealtimeOutboxRetryPolicy.CalculateDelay(1, 2, 60, 0.21));
    }

    [Fact]
    public void Dispatcher_options_validate_all_bounds()
    {
        var valid = new OutboxDispatcherOptions();
        Assert.True(OutboxDispatcherOptionsValidator.IsValid(valid));

        valid.Business.BatchSize = 0;
        Assert.False(OutboxDispatcherOptionsValidator.IsValid(valid));
    }

    [Theory]
    [InlineData(5, 8, 5)]
    [InlineData(100, 1, 1)]
    [InlineData(100, 8, 8)]
    [InlineData(8, 8, 8)]
    public void Effective_claim_size_never_exceeds_immediate_slots(
        int batchSize,
        int maximumConcurrency,
        int expected)
    {
        var lane = new OutboxLaneOptions
        {
            BatchSize = batchSize,
            MaximumConcurrency = maximumConcurrency,
        };

        Assert.Equal(expected, OutboxDispatcherPolicy.EffectiveClaimSize(lane));
    }

    [Theory]
    [InlineData(true, 10, 15, true)]
    [InlineData(true, 10, 14, false)]
    [InlineData(false, 10, 15, true)]
    [InlineData(false, 10, 14, false)]
    [InlineData(true, 60, 60, false)]
    [InlineData(true, 60, 65, true)]
    [InlineData(false, 60, 60, false)]
    [InlineData(false, 60, 65, true)]
    public void Lease_validation_is_independent_and_reserves_settlement_margin(
        bool businessLane,
        int publishTimeoutSeconds,
        int leaseSeconds,
        bool expected)
    {
        var options = new OutboxDispatcherOptions
        {
            PublishTimeoutSeconds = publishTimeoutSeconds,
        };
        options.Business.LeaseSeconds = publishTimeoutSeconds +
            OutboxDispatcherOptionsValidator.SettlementSafetyMarginSeconds;
        options.Location.LeaseSeconds = publishTimeoutSeconds +
            OutboxDispatcherOptionsValidator.SettlementSafetyMarginSeconds;
        if (businessLane)
        {
            options.Business.LeaseSeconds = leaseSeconds;
        }
        else
        {
            options.Location.LeaseSeconds = leaseSeconds;
        }

        Assert.Equal(expected, OutboxDispatcherOptionsValidator.IsValid(options));
    }

    [Theory]
    [InlineData(100, 1, 1)]
    [InlineData(100, 8, 8)]
    public async Task Claiming_passes_only_the_effective_size_to_each_lane(
        int batchSize,
        int maximumConcurrency,
        int expected)
    {
        var store = new RecordingOutboxStore();
        var lane = new OutboxLaneOptions
        {
            BatchSize = batchSize,
            MaximumConcurrency = maximumConcurrency,
            LeaseSeconds = 65,
        };

        await RealtimeOutboxClaiming.ClaimBusinessAsync(store, "worker", lane, default);
        await RealtimeOutboxClaiming.ClaimLocationAsync(store, "worker", lane, default);

        Assert.Equal(expected, store.BusinessClaimSize);
        Assert.Equal(expected, store.LocationClaimSize);
    }

    [Fact]
    public async Task Shutdown_stops_scheduling_but_allows_started_work_to_drain()
    {
        using var stopping = new CancellationTokenSource();
        var started = new List<int>();
        var firstCanFinish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // The drain window must outlast thread-pool latency on a saturated CI runner: completing
        // firstCanFinish resumes the started work through an asynchronous continuation. Cancelling
        // after the window is covered by Shutdown_cancels_incomplete_started_work_after_the_drain_window.
        var processing = RealtimeOutboxBatchDrain.ProcessAsync(
            [1, 2],
            stopping.Token,
            TimeSpan.FromSeconds(30),
            (message, token) =>
            {
                started.Add(message);
                if (message == 1)
                {
                    stopping.Cancel();
                    return firstCanFinish.Task.WaitAsync(token);
                }

                return Task.CompletedTask;
            });

        Assert.Equal([1], started);
        firstCanFinish.SetResult();
        await processing;
        Assert.Equal([1], started);
    }

    [Fact]
    public async Task Shutdown_cancels_incomplete_started_work_after_the_drain_window()
    {
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = RealtimeOutboxBatchDrain.ProcessAsync(
            [1],
            stopping.Token,
            TimeSpan.FromMilliseconds(50),
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
    }

    [Fact]
    public void Envelope_does_not_serialize_an_audience()
    {
        var envelope = new RealtimeEnvelope<OrderStatusChangedPayload>(
            EventId,
            RealtimeEventTypes.OrderStatusChanged,
            OccurredAt,
            OrderId,
            12,
            null,
            new(OrderId, "IN_TRANSIT", "DELIVERING", OccurredAt));

        var properties = JsonDocument.Parse(
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        Assert.DoesNotContain("audience", properties);
    }

    private static ClaimedBusinessOutboxMessage Business(string topic, string payload) =>
        new(
            EventId,
            OrganizationId,
            $$"""{"organization_ids":["{{OrganizationId:D}}"]}""",
            topic,
            "Order",
            OrderId,
            12,
            payload,
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000807"),
            OccurredAt.AddMinutes(2),
            OccurredAt,
            OccurredAt);

    private static ClaimedLocationOutboxMessage Location(string payload) =>
        new(
            EventId,
            OrganizationId,
            OrderEventId,
            RealtimeOutboxTopics.DriverLocationUpdated,
            payload,
            1,
            Guid.Parse("00000000-0000-0000-0000-000000000807"),
            OccurredAt.AddMinutes(2),
            OccurredAt,
            OccurredAt);

    private sealed class RecordingOutboxStore : IRealtimeOutboxStore
    {
        public int BusinessClaimSize { get; private set; }
        public int LocationClaimSize { get; private set; }

        public Task<IReadOnlyList<ClaimedBusinessOutboxMessage>> ClaimBusinessAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            BusinessClaimSize = batchSize;
            return Task.FromResult<IReadOnlyList<ClaimedBusinessOutboxMessage>>([]);
        }

        public Task<IReadOnlyList<ClaimedLocationOutboxMessage>> ClaimLocationAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            LocationClaimSize = batchSize;
            return Task.FromResult<IReadOnlyList<ClaimedLocationOutboxMessage>>([]);
        }

        public Task<bool> SettleBusinessAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> SettleLocationAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<int> RequeueStaleBusinessAsync(
            int batchSize,
            int maximumAttempts,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<int> RequeueStaleLocationAsync(
            int batchSize,
            int maximumAttempts,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
