using System.Globalization;
using System.Text.Json;
using Orders.Application.Tracking;

namespace Orders.Infrastructure.Tracking;

public static class PublicTrackingJsonParser
{
    public static PublicTrackingProjection Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireObjectWithProperties(root, "public_id", "public_status", "estimated_window", "timeline");

            var publicIdElement = root.GetProperty("public_id");
            if (publicIdElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(publicIdElement.GetString()))
            {
                throw InvalidContract("public_id must be a non-empty string.");
            }

            var publicStatus = ParsePublicStatus(root.GetProperty("public_status"));
            var estimatedWindow = ParseEstimatedWindow(root.GetProperty("estimated_window"));
            var timelineElement = root.GetProperty("timeline");
            if (timelineElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidContract("timeline must be an array.");
            }

            var timeline = new List<PublicTrackingTimelineItem>();
            foreach (var item in timelineElement.EnumerateArray())
            {
                RequireObjectWithProperties(item, "code", "occurred_at");
                var code = ParseEventCode(item.GetProperty("code"));
                var occurredAtElement = item.GetProperty("occurred_at");
                if (occurredAtElement.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(
                        occurredAtElement.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var occurredAt))
                {
                    throw InvalidContract("occurred_at must be a valid timestamp.");
                }

                timeline.Add(new PublicTrackingTimelineItem(code, occurredAt));
            }

            return new PublicTrackingProjection(publicIdElement.GetString()!, publicStatus, estimatedWindow, timeline);
        }
        catch (PublicTrackingInfrastructureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw InvalidContract("Public tracking JSON does not match the expected contract.", exception);
        }
    }

    private static IReadOnlyDictionary<string, string?>? ParseEstimatedWindow(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidContract("estimated_window must be an object or null.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!values.TryAdd(property.Name, property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw InvalidContract("estimated_window values must be strings or null."),
            }))
            {
                throw InvalidContract("estimated_window contains duplicate properties.");
            }
        }

        return values;
    }

    private static PublicOrderStatus ParsePublicStatus(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() switch
        {
            "CREATED" => PublicOrderStatus.Created,
            "SCHEDULED" => PublicOrderStatus.Scheduled,
            "IN_TRANSIT" => PublicOrderStatus.InTransit,
            "OUT_FOR_DELIVERY" => PublicOrderStatus.OutForDelivery,
            "DELIVERY_EXCEPTION" => PublicOrderStatus.DeliveryException,
            "DELIVERED" => PublicOrderStatus.Delivered,
            "RETURNING" => PublicOrderStatus.Returning,
            "RETURNED" => PublicOrderStatus.Returned,
            "CANCELLED" => PublicOrderStatus.Cancelled,
            _ => throw InvalidContract("Unknown public status."),
        } : throw InvalidContract("public_status must be a string.");

    private static PublicTimelineEventCode ParseEventCode(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() switch
        {
            "ORDER_CREATED" => PublicTimelineEventCode.OrderCreated,
            "PICKUP_SCHEDULED" => PublicTimelineEventCode.PickupScheduled,
            "PICKED_UP" => PublicTimelineEventCode.PickedUp,
            "IN_TRANSIT" => PublicTimelineEventCode.InTransit,
            "OUT_FOR_DELIVERY" => PublicTimelineEventCode.OutForDelivery,
            "DELIVERY_ATTEMPTED" => PublicTimelineEventCode.DeliveryAttempted,
            "RESCHEDULED" => PublicTimelineEventCode.Rescheduled,
            "DELIVERED" => PublicTimelineEventCode.Delivered,
            "RETURNING" => PublicTimelineEventCode.Returning,
            "RETURNED" => PublicTimelineEventCode.Returned,
            "CANCELLED" => PublicTimelineEventCode.Cancelled,
            _ => throw InvalidContract("Unknown public timeline event code."),
        } : throw InvalidContract("timeline code must be a string.");

    private static void RequireObjectWithProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidContract("Expected a JSON object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw InvalidContract("JSON object properties do not match the expected contract.");
        }
    }

    private static PublicTrackingInfrastructureException InvalidContract(
        string message,
        Exception? innerException = null) => new(message, innerException);
}
