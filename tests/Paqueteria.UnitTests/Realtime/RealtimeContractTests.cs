using System.Text.Json;
using System.Text.Json.Serialization;
using Paqueteria.Domain.Tenancy;
using Realtime.Application.Authorization;
using Realtime.Application.Clients;
using Realtime.Application.Events;
using Realtime.Application.Publishing;

namespace Paqueteria.UnitTests.Realtime;

public sealed class RealtimeContractTests
{
    private static readonly Guid EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AggregateId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_types_are_exact_and_versioned()
    {
        Assert.Equal(
            [
                "OrderStatusChanged.v1",
                "OrderTimelineEventAdded.v1",
                "AssignmentChanged.v1",
                "RouteChanged.v1",
                "IncidentCreated.v1",
                "ExternalOfferChanged.v1",
                "NotificationStatusChanged.v1",
                "DriverLocationUpdated.v1",
                "PublicOrderStatusChanged.v1",
                "PublicEtaChanged.v1",
            ],
            typeof(RealtimeEventTypes)
                .GetFields(System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
                .ToArray());
    }

    [Fact]
    public void Envelope_requires_non_empty_identifiers_utc_and_non_negative_version()
    {
        var payload = Payload();
        Assert.Throws<ArgumentException>(() => new RealtimeEnvelope<OrderStatusChangedPayload>(
            Guid.Empty,
            RealtimeEventTypes.OrderStatusChanged,
            OccurredAt,
            AggregateId,
            1,
            null,
            payload));
        Assert.Throws<ArgumentException>(() => new RealtimeEnvelope<OrderStatusChangedPayload>(
            EventId,
            RealtimeEventTypes.OrderStatusChanged,
            OccurredAt.ToOffset(TimeSpan.FromHours(-7)),
            AggregateId,
            1,
            null,
            payload));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RealtimeEnvelope<OrderStatusChangedPayload>(
                EventId,
                RealtimeEventTypes.OrderStatusChanged,
                OccurredAt,
                AggregateId,
                -1,
                null,
                payload));
        Assert.Throws<ArgumentException>(() => new PublicRealtimeEnvelope<PublicEtaChangedPayload>(
            EventId,
            RealtimeEventTypes.PublicEtaChanged,
            OccurredAt,
            "not-public",
            1,
            null,
            new PublicEtaChangedPayload("not-public", OccurredAt, OccurredAt, OccurredAt)));
    }

    [Fact]
    public void Envelope_and_payload_serialize_to_exact_snake_case_contract()
    {
        var envelope = new RealtimeEnvelope<OrderStatusChangedPayload>(
            EventId,
            RealtimeEventTypes.OrderStatusChanged,
            OccurredAt,
            AggregateId,
            3,
            null,
            Payload());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope, SerializerOptions()));
        Assert.Equal(
            ["aggregate_id", "aggregate_version", "event_id", "event_type", "occurred_at", "payload"],
            document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["new_status", "occurred_at", "order_id", "previous_status"],
            document.RootElement.GetProperty("payload").EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.False(document.RootElement.TryGetProperty("correlation_id", out _));
    }

    [Fact]
    public void Group_names_are_canonical_and_reject_empty_or_invalid_ids()
    {
        Assert.Equal($"org:{AggregateId:D}", OperationsAudience.ForOrganization(AggregateId).GroupName);
        Assert.Equal($"order:{AggregateId:D}", OperationsAudience.ForOrder(AggregateId).GroupName);
        Assert.Equal($"driver:{AggregateId:D}", DriverAudience.ForDriver(AggregateId).GroupName);
        Assert.Equal($"assignment:{AggregateId:D}", DriverAudience.ForAssignment(AggregateId).GroupName);
        Assert.Equal(
            "tracking:ORD_abcdefghijklmnopqrstuv",
            TrackingAudience.ForPublicOrder("ORD_abcdefghijklmnopqrstuv").GroupName);
        Assert.Throws<ArgumentException>(() => OperationsAudience.ForOrganization(Guid.Empty));
        Assert.Throws<ArgumentException>(() => TrackingAudience.ForPublicOrder("invalid"));
    }

    [Theory]
    [InlineData(OrganizationRole.PlatformAdmin, true, true)]
    [InlineData(OrganizationRole.PlatformAdmin, false, false)]
    [InlineData(OrganizationRole.Dispatcher, false, true)]
    [InlineData(OrganizationRole.Viewer, true, false)]
    [InlineData(OrganizationRole.Driver, true, false)]
    public void Operations_role_allowlist_fails_closed(
        OrganizationRole role,
        bool mfaSatisfied,
        bool expected) =>
        Assert.Equal(expected, RealtimeOperationsRolePolicy.IsAllowed(role, mfaSatisfied));

    [Fact]
    public void Client_interfaces_match_the_AI_12_event_surface()
    {
        Assert.Equal(
            [
                "AssignmentChanged",
                "DriverLocationUpdated",
                "ExternalOfferChanged",
                "IncidentCreated",
                "NotificationStatusChanged",
                "OrderStatusChanged",
                "OrderTimelineEventAdded",
                "RouteChanged",
            ],
            MethodNames<IOperationsClient>());
        Assert.Equal(
            ["AssignmentChanged", "ExternalOfferChanged", "OrderStatusChanged", "RouteChanged"],
            MethodNames<IDriverClient>());
        Assert.Equal(
            ["PublicEtaChanged", "PublicOrderStatusChanged"],
            MethodNames<ITrackingClient>());
    }

    [Fact]
    public void Publisher_exposes_no_arbitrary_group_method_or_payload()
    {
        var parameters = typeof(IRealtimePublisher)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(string), parameters);
        Assert.DoesNotContain(typeof(object), parameters);
        Assert.DoesNotContain(typeof(JsonElement), parameters);
        Assert.All(
            typeof(IRealtimePublisher).GetMethods(),
            method => Assert.StartsWith("Publish", method.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Payload_types_have_only_AI_12_properties_and_no_forbidden_PII_names()
    {
        AssertProperties<OrderStatusChangedPayload>("OrderId", "PreviousStatus", "NewStatus", "OccurredAt");
        AssertProperties<OrderTimelineEventAddedPayload>(
            "OrderId", "TimelineEventId", "Category", "Summary", "OccurredAt");
        AssertProperties<AssignmentChangedPayload>(
            "OrderId", "AssignmentId", "DriverId", "AssignmentStatus", "OccurredAt");
        AssertProperties<RouteChangedPayload>("RouteId", "RouteVersion", "ChangedStopIds", "OccurredAt");
        AssertProperties<IncidentCreatedPayload>(
            "IncidentId", "OrderId", "Type", "Severity", "Status", "OccurredAt");
        AssertProperties<ExternalOfferChangedPayload>(
            "OfferId", "Status", "CommissionCents", "ExpiresAt");
        AssertProperties<NotificationStatusChangedPayload>(
            "NotificationId", "Channel", "Status", "Attempts", "OccurredAt");
        AssertProperties<DriverLocationUpdatedPayload>(
            "DriverId", "Lat", "Lng", "AccuracyM", "CapturedAt");
        AssertProperties<PublicOrderStatusChangedPayload>(
            "PublicOrderId", "PublicStatus", "OccurredAt");
        AssertProperties<PublicEtaChangedPayload>(
            "PublicOrderId", "EtaFrom", "EtaTo", "UpdatedAt");

        var forbidden = new[] { "Phone", "Address", "Document", "ObjectKey", "Hash", "Token", "Notes" };
        Assert.DoesNotContain(
            PayloadTypes().SelectMany(type => type.GetProperties()),
            property => forbidden.Any(value =>
                property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static OrderStatusChangedPayload Payload() =>
        new(AggregateId, "READY_FOR_PICKUP", "ASSIGNED", OccurredAt);

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string[] MethodNames<T>() =>
        typeof(T).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal).ToArray();

    private static void AssertProperties<T>(params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            typeof(T).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static Type[] PayloadTypes() =>
    [
        typeof(OrderStatusChangedPayload),
        typeof(OrderTimelineEventAddedPayload),
        typeof(AssignmentChangedPayload),
        typeof(RouteChangedPayload),
        typeof(IncidentCreatedPayload),
        typeof(ExternalOfferChangedPayload),
        typeof(NotificationStatusChangedPayload),
        typeof(DriverLocationUpdatedPayload),
        typeof(PublicOrderStatusChangedPayload),
        typeof(PublicEtaChangedPayload),
    ];
}
