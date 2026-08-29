using System.Globalization;
using System.Text.Json;
using Paqueteria.Application;

namespace Realtime.Application.Dispatching;

public static class RealtimeOutboxParser
{
    private static readonly HashSet<string> CanonicalOrderStatuses =
    [
        "DRAFT",
        "CONFIRMED",
        "READY_FOR_PICKUP",
        "ASSIGNED",
        "AT_PICKUP",
        "PICKED_UP",
        "IN_TRANSIT",
        "DELIVERING",
        "FAILED_ATTEMPT",
        "RESCHEDULED",
        "RETURNING",
        "RETURNED",
        "DELIVERED",
        "CLOSED",
        "CLAIM_OPEN",
        "CLAIM_RESOLVED",
        "CANCELLED",
    ];

    public static ParsedBusinessOutboxEvent Parse(ClaimedBusinessOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!RealtimeOutboxTopics.IsBusinessTopic(message.Topic))
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.UnknownTopic);
        }

        ValidateBusinessColumns(message);
        ValidateTenantContext(message.TenantContextJson, message.OwnerOrganizationId);
        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);
            return message.Topic switch
            {
                RealtimeOutboxTopics.OrderStatusChanged => ParseStatus(message, document.RootElement),
                RealtimeOutboxTopics.OrderTimelineEventAdded => ParseTimeline(message, document.RootElement),
                RealtimeOutboxTopics.AssignmentChanged => ParseAssignment(message, document.RootElement),
                RealtimeOutboxTopics.ExternalOfferChanged => ParseExternalOffer(message, document.RootElement),
                RealtimeOutboxTopics.RouteChanged => ParseRoute(message, document.RootElement),
                RealtimeOutboxTopics.NotificationStatusChanged => ParseNotification(message, document.RootElement),
                _ => throw new OutboxMessageException(RealtimeOutboxErrorCodes.UnknownTopic),
            };
        }
        catch (OutboxMessageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidPayload) { Source = exception.Source };
        }
    }

    public static ParsedDriverLocationUpdated Parse(ClaimedLocationOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!RealtimeOutboxTopics.IsLocationTopic(message.Topic))
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.UnknownTopic);
        }

        if (message.Id == Guid.Empty ||
            message.OwnerOrganizationId == Guid.Empty ||
            message.DriverPositionId == Guid.Empty ||
            message.LeaseToken == Guid.Empty)
        {
            throw InvalidPayload();
        }

        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);
            var root = document.RootElement;
            RequireExactProperties(
                root,
                "schema_version",
                "driver_position_id",
                "driver_id",
                "lat",
                "lng",
                "accuracy_m",
                "captured_at");
            RequireSchema(root, "driver-location-updated-v1");
            var positionId = RequireGuid(root, "driver_position_id");
            var driverId = RequireGuid(root, "driver_id");
            var lat = RequireFiniteNumber(root, "lat");
            var lng = RequireFiniteNumber(root, "lng");
            var accuracy = RequireFiniteNumber(root, "accuracy_m");
            var capturedAt = RequireUtcTimestamp(root, "captured_at");
            if (positionId != message.DriverPositionId ||
                lat is < -90 or > 90 ||
                lng is < -180 or > 180 ||
                accuracy < 0)
            {
                throw InvalidPayload();
            }

            return new(
                message.Id,
                message.OwnerOrganizationId,
                positionId,
                driverId,
                lat,
                lng,
                accuracy,
                capturedAt,
                RealtimeLocationCursor.FromCapturedAt(capturedAt));
        }
        catch (OutboxMessageException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or ArgumentOutOfRangeException)
        {
            throw InvalidPayload();
        }
    }

    public static string TimelineSummary(string newStatus)
    {
        if (!CanonicalOrderStatuses.Contains(newStatus))
        {
            throw InvalidPayload();
        }

        return $"Order status changed to {newStatus}.";
    }

    private static ParsedOrderStatusChanged ParseStatus(
        ClaimedBusinessOutboxMessage message,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "order_event_id",
            "order_id",
            "public_order_id",
            "previous_status",
            "new_status",
            "occurred_at",
            "public_event_code",
            "authorized_driver_id",
            "assignment_id");
        RequireSchema(root, "order-status-changed-v1");
        var eventId = RequireGuid(root, "order_event_id");
        var orderId = RequireGuid(root, "order_id");
        var publicOrderId = RequireString(root, "public_order_id");
        var previous = RequireString(root, "previous_status");
        var next = RequireString(root, "new_status");
        var occurredAt = RequireUtcTimestamp(root, "occurred_at");
        var publicEventCode = OptionalString(root, "public_event_code");
        var authorizedDriverId = OptionalGuid(root, "authorized_driver_id");
        var assignmentId = OptionalGuid(root, "assignment_id");
        if (orderId != message.AggregateId ||
            !Events.RealtimePublicOrderId.IsValid(publicOrderId) ||
            !CanonicalOrderStatuses.Contains(previous) ||
            !CanonicalOrderStatuses.Contains(next) ||
            (authorizedDriverId is null) != (assignmentId is null))
        {
            throw InvalidPayload();
        }

        return new(
            message.Id,
            message.OwnerOrganizationId,
            orderId,
            message.AggregateVersion!.Value,
            occurredAt,
            eventId,
            publicOrderId,
            previous,
            next,
            publicEventCode,
            authorizedDriverId,
            assignmentId);
    }

    private static ParsedOrderTimelineEventAdded ParseTimeline(
        ClaimedBusinessOutboxMessage message,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "order_id",
            "timeline_event_id",
            "category",
            "summary",
            "occurred_at");
        RequireSchema(root, "order-timeline-event-added-v1");
        var orderId = RequireGuid(root, "order_id");
        var timelineEventId = RequireGuid(root, "timeline_event_id");
        var category = RequireString(root, "category");
        var summary = RequireString(root, "summary");
        var occurredAt = RequireUtcTimestamp(root, "occurred_at");
        if (orderId != message.AggregateId ||
            category != "ORDER_STATUS" ||
            string.IsNullOrWhiteSpace(summary) ||
            summary.Length > 80)
        {
            throw InvalidPayload();
        }

        return new(
            message.Id,
            message.OwnerOrganizationId,
            orderId,
            message.AggregateVersion!.Value,
            occurredAt,
            timelineEventId,
            category,
            summary);
    }

    private static ParsedAssignmentChanged ParseAssignment(
        ClaimedBusinessOutboxMessage message,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "order_id",
            "assignment_id",
            "driver_id",
            "assignment_status",
            "occurred_at");
        RequireSchema(root, "assignment-changed-v1");
        var orderId = RequireGuid(root, "order_id");
        var assignmentId = RequireGuid(root, "assignment_id");
        var driverId = RequireGuid(root, "driver_id");
        var status = RequireString(root, "assignment_status");
        var occurredAt = RequireUtcTimestamp(root, "occurred_at");
        if (orderId != message.AggregateId || status != "ACCEPTED")
        {
            throw InvalidPayload();
        }

        return new(
            message.Id,
            message.OwnerOrganizationId,
            orderId,
            message.AggregateVersion!.Value,
            occurredAt,
            assignmentId,
            driverId,
            status);
    }

    private static ParsedNotificationStatusChanged ParseNotification(
        ClaimedBusinessOutboxMessage message,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "notification_id",
            "channel",
            "status",
            "attempts",
            "occurred_at");
        RequireSchema(root, "notification-status-changed-v1");
        var notificationId = RequireGuid(root, "notification_id");
        var channel = RequireString(root, "channel");
        var status = RequireString(root, "status");
        var attemptsElement = root.GetProperty("attempts");
        var occurredAt = RequireUtcTimestamp(root, "occurred_at");
        if (notificationId != message.AggregateId ||
            channel != "IN_APP" ||
            status is not ("PENDING" or "SENT" or "FAILED") ||
            attemptsElement.ValueKind != JsonValueKind.Number ||
            !attemptsElement.TryGetInt32(out var attempts) ||
            attempts < 0)
        {
            throw InvalidPayload();
        }

        return new(
            message.Id,
            message.OwnerOrganizationId,
            notificationId,
            message.AggregateVersion!.Value,
            occurredAt,
            channel,
            status,
            attempts);
    }

    private static ParsedExternalOfferChanged ParseExternalOffer(
        ClaimedBusinessOutboxMessage message,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "offer_id",
            "status",
            "commission_cents",
            "expires_at",
            "audience_driver_ids");
        RequireSchema(root, "external-offer-changed-v1");
        var offerId = RequireGuid(root, "offer_id");
        var status = RequireString(root, "status");
        var commissionElement = root.GetProperty("commission_cents");
        var expiresAt = RequireUtcTimestamp(root, "expires_at");
        var audienceElement = root.GetProperty("audience_driver_ids");
        if (offerId != message.AggregateId ||
            status is not ("OPEN" or "ACCEPTED" or "EXPIRED" or "CANCELLED") ||
            commissionElement.ValueKind != JsonValueKind.Number ||
            !commissionElement.TryGetInt64(out var commissionCents) ||
            commissionCents < 0 ||
            audienceElement.ValueKind != JsonValueKind.Array ||
            audienceElement.GetArrayLength() > 500)
        {
            throw InvalidPayload();
        }

        var audience = new List<Guid>(audienceElement.GetArrayLength());
        foreach (var item in audienceElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(item.GetString(), "D", out var driverId) ||
                driverId == Guid.Empty || audience.Contains(driverId))
            {
                throw InvalidPayload();
            }
            audience.Add(driverId);
        }

        return new(
            message.Id,
            message.OwnerOrganizationId,
            offerId,
            message.AggregateVersion!.Value,
            UtcMicrosecondPrecision.Normalize(message.CreatedAt),
            status,
            commissionCents,
            expiresAt,
            audience);
    }

    private static ParsedRouteChanged ParseRoute(
        ClaimedBusinessOutboxMessage message,
        JsonElement root)
    {
        RequireExactProperties(
            root,
            "schema_version",
            "route_id",
            "route_version",
            "changed_stop_ids",
            "occurred_at");
        RequireSchema(root, "route-changed-v1");
        var routeId = RequireGuid(root, "route_id");
        var versionElement = root.GetProperty("route_version");
        var changedElement = root.GetProperty("changed_stop_ids");
        var occurredAt = RequireUtcTimestamp(root, "occurred_at");
        if (routeId != message.AggregateId ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt64(out var routeVersion) ||
            routeVersion != message.AggregateVersion ||
            routeVersion < 1 ||
            changedElement.ValueKind != JsonValueKind.Array ||
            changedElement.GetArrayLength() > 500)
        {
            throw InvalidPayload();
        }

        var changedStopIds = new List<Guid>(changedElement.GetArrayLength());
        foreach (var item in changedElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(item.GetString(), "D", out var stopId) ||
                stopId == Guid.Empty || changedStopIds.Contains(stopId))
            {
                throw InvalidPayload();
            }
            changedStopIds.Add(stopId);
        }

        return new(
            message.Id,
            message.OwnerOrganizationId,
            routeId,
            routeVersion,
            occurredAt,
            changedStopIds);
    }

    private static void ValidateBusinessColumns(ClaimedBusinessOutboxMessage message)
    {
        if (message.Id == Guid.Empty ||
            message.OwnerOrganizationId == Guid.Empty ||
            message.AggregateId == Guid.Empty ||
            message.AggregateType != (message.Topic switch
            {
                RealtimeOutboxTopics.NotificationStatusChanged => "Notification",
                RealtimeOutboxTopics.ExternalOfferChanged => "ExternalOffer",
                RealtimeOutboxTopics.RouteChanged => "Route",
                _ => "Order",
            }) ||
            message.AggregateVersion is null or < 0 ||
            message.LeaseToken == Guid.Empty)
        {
            throw InvalidPayload();
        }
    }

    private static void ValidateTenantContext(string json, Guid ownerOrganizationId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireExactProperties(root, "organization_ids");
            var ids = root.GetProperty("organization_ids");
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 1)
            {
                throw InvalidPayload();
            }

            var value = ids[0];
            if (value.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(value.GetString(), "D", out var parsed) ||
                parsed == Guid.Empty ||
                parsed != ownerOrganizationId)
            {
                throw InvalidPayload();
            }
        }
        catch (OutboxMessageException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidPayload();
        }
    }

    private static void RequireSchema(JsonElement root, string expected)
    {
        var schema = RequireString(root, "schema_version");
        if (!string.Equals(schema, expected, StringComparison.Ordinal))
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidSchema);
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidPayload();
        }

        var actual = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw InvalidPayload();
        }
    }

    private static string RequireString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidPayload();
        }

        return value.GetString() ?? throw InvalidPayload();
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
            _ => throw InvalidPayload(),
        };
    }

    private static Guid RequireGuid(JsonElement root, string name)
    {
        var value = RequireString(root, name);
        return Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw InvalidPayload();
    }

    private static Guid? OptionalGuid(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequireGuid(root, name);
    }

    private static double RequireFiniteNumber(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var parsed) ||
            !double.IsFinite(parsed))
        {
            throw InvalidPayload();
        }

        return parsed;
    }

    private static DateTimeOffset RequireUtcTimestamp(JsonElement root, string name)
    {
        var value = RequireString(root, name);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw InvalidPayload();
        }

        return UtcMicrosecondPrecision.Normalize(parsed);
    }

    private static OutboxMessageException InvalidPayload() =>
        new(RealtimeOutboxErrorCodes.InvalidPayload);
}
