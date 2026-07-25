using System.Text.Json;
using System.Text.Json.Serialization;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;
using Realtime.Application.Events;

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
}
