using System.Globalization;
using System.Text.Json;
using Notifications.Application.Dispatching;

namespace Notifications.Application.RecipientNotices;

/// <summary>One <c>orders.recipient-notice-requested</c> row (AI-24 <c>recipient_notices.producer.payload</c>), ids only.</summary>
public sealed record ParsedRecipientNoticeRequest(
    Guid OwnerOrganizationId,
    Guid OrderId,
    Guid OrderEventId,
    RecipientNoticeKind Notice,
    DateTimeOffset OccurredAt);

/// <summary>
/// The C# reading of a request row before the Worker reads anything for it: the envelope (Order aggregate, the owner as
/// the only tenant) and the <c>recipient-notice-requested-v1</c> payload. <c>security.expand_recipient_notice_request</c>
/// validates the same row again, authoritatively.
/// </summary>
public static class RecipientNoticeRequestParser
{
    public const string SchemaVersion = "recipient-notice-requested-v1";

    public static ParsedRecipientNoticeRequest Parse(ClaimedNotificationOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Topic != NotificationOutboxTopics.RecipientNoticeRequested ||
            message.Id == Guid.Empty ||
            message.OwnerOrganizationId == Guid.Empty ||
            message.AggregateType != "Order" ||
            message.AggregateId == Guid.Empty ||
            message.LeaseToken == Guid.Empty)
        {
            throw Invalid();
        }

        ValidateTenant(message.TenantContextJson, message.OwnerOrganizationId);
        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            var names = root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(["notice", "occurred_at", "order_event_id", "order_id", "schema_version"], StringComparer.Ordinal) ||
                String(root, "schema_version") != SchemaVersion ||
                !Guid.TryParseExact(String(root, "order_id"), "D", out var orderId) ||
                !Guid.TryParseExact(String(root, "order_event_id"), "D", out var orderEventId) ||
                orderId != message.AggregateId || orderEventId == Guid.Empty ||
                !RecipientNoticeCatalog.TryFromNotice(String(root, "notice"), out var notice) ||
                !DateTimeOffset.TryParse(
                    String(root, "occurred_at"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var occurredAt) ||
                !String(root, "occurred_at").EndsWith('Z'))
            {
                throw Invalid();
            }

            return new(message.OwnerOrganizationId, orderId, orderEventId, notice, occurredAt);
        }
        catch (NotificationMessageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw Invalid();
        }
    }

    private static string String(JsonElement root, string name) =>
        root.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw Invalid();

    private static void ValidateTenant(string json, Guid ownerOrganizationId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().ToArray() : [];
            if (properties.Length != 1 ||
                properties[0].Name != "organization_ids" ||
                properties[0].Value.ValueKind != JsonValueKind.Array ||
                properties[0].Value.GetArrayLength() != 1 ||
                properties[0].Value[0].ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(properties[0].Value[0].GetString(), "D", out var parsed) ||
                parsed != ownerOrganizationId)
            {
                throw new NotificationMessageException(NotificationErrorCodes.TenantContextMismatch);
            }
        }
        catch (JsonException)
        {
            throw new NotificationMessageException(NotificationErrorCodes.TenantContextMismatch);
        }
    }

    private static NotificationMessageException Invalid() => new(NotificationErrorCodes.InvalidPayload);
}
