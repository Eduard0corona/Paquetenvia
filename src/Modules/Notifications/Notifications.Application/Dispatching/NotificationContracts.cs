using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Paqueteria.Application;

namespace Notifications.Application.Dispatching;

public enum OutboxConsumer
{
    Realtime,
    Notifications,
    Unrouted,
}

public static class NotificationOutboxTopics
{
    public const string OrdersCreated = "orders.created";
    public const string SendRequested = "notifications.send-requested";
    public const string StatusChanged = "notifications.status-changed";

    private static readonly HashSet<string> RealtimeTopics =
    [
        "orders.status-changed",
        "orders.timeline-event-added",
        "dispatch.assignment-changed",
        StatusChanged,
    ];

    public static OutboxConsumer Resolve(string topic) => topic switch
    {
        OrdersCreated or SendRequested => OutboxConsumer.Notifications,
        _ when RealtimeTopics.Contains(topic) => OutboxConsumer.Realtime,
        _ => OutboxConsumer.Unrouted,
    };

    public static IReadOnlySet<string> Realtime => RealtimeTopics;
}

public static class NotificationErrorCodes
{
    public const string SourceExpanded = "SOURCE_EXPANDED";
    public const string NoEligibleRecipient = "NO_ELIGIBLE_RECIPIENT";
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string TenantContextMismatch = "TENANT_CONTEXT_MISMATCH";
    public const string TemplateNotFound = "TEMPLATE_NOT_FOUND";
    public const string TemplateVersionUnknown = "TEMPLATE_VERSION_UNKNOWN";
    public const string TemplateVariableUnknown = "TEMPLATE_VARIABLE_UNKNOWN";
    public const string AudienceLimitExceeded = "AUDIENCE_LIMIT_EXCEEDED";
    public const string AudienceContractViolation = "AUDIENCE_CONTRACT_VIOLATION";
    public const string UnknownTopic = "UNKNOWN_TOPIC";
    public const string MaxAttemptsExhausted = "MAX_ATTEMPTS_EXHAUSTED";
    public const string ProviderAccepted = "SYNTHETIC_ACCEPTED";
    public const string ProviderTransient = "SYNTHETIC_TRANSIENT";
    public const string ProviderPermanent = "SYNTHETIC_PERMANENT";
    public const string ProviderAmbiguous = "SYNTHETIC_AMBIGUOUS_TIMEOUT";
    public const string LeaseLost = "LEASE_LOST";
}

public sealed record ClaimedNotificationOutboxMessage(
    Guid Id,
    Guid OwnerOrganizationId,
    string TenantContextJson,
    string Topic,
    string AggregateType,
    Guid AggregateId,
    int? AggregateVersion,
    string PayloadJson,
    int Attempts,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset AvailableAt);

public sealed record ParsedOrderCreated(
    Guid SourceEventId,
    Guid OwnerOrganizationId,
    Guid OrderId,
    string OrderPublicId,
    string OrderStatus,
    DateTimeOffset OccurredAt);

public static class OrderCreatedParser
{
    public static ParsedOrderCreated Parse(ClaimedNotificationOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Topic != NotificationOutboxTopics.OrdersCreated ||
            message.Id == Guid.Empty ||
            message.OwnerOrganizationId == Guid.Empty ||
            message.AggregateType != "Order" ||
            message.AggregateId == Guid.Empty ||
            message.AggregateVersion != 1 ||
            message.LeaseToken == Guid.Empty)
        {
            throw new NotificationMessageException(NotificationErrorCodes.InvalidPayload);
        }

        ValidateTenant(message.TenantContextJson, message.OwnerOrganizationId);
        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);
            var root = document.RootElement;
            var properties = root.EnumerateObject().Select(static value => value.Name).ToArray();
            var expected = new[] { "order_id", "public_id", "status" };
            if (root.ValueKind != JsonValueKind.Object ||
                properties.Length != expected.Length ||
                expected.Any(name => !properties.Contains(name, StringComparer.Ordinal)))
            {
                throw Invalid();
            }

            var orderId = RequireGuid(root, "order_id");
            var publicId = RequireString(root, "public_id");
            var status = RequireString(root, "status");
            if (orderId != message.AggregateId ||
                !publicId.StartsWith("ORD_", StringComparison.Ordinal) ||
                publicId.Length != 26 ||
                status != "DRAFT" ||
                message.CreatedAt.Offset != TimeSpan.Zero)
            {
                throw Invalid();
            }

            return new(
                message.Id,
                message.OwnerOrganizationId,
                orderId,
                publicId,
                status,
                UtcMicrosecondPrecision.Normalize(message.CreatedAt));
        }
        catch (NotificationMessageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    private static void ValidateTenant(string json, Guid ownerOrganizationId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var properties = root.EnumerateObject().ToArray();
            if (root.ValueKind != JsonValueKind.Object ||
                properties.Length != 1 ||
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
        catch (NotificationMessageException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new NotificationMessageException(NotificationErrorCodes.TenantContextMismatch);
        }
    }

    private static Guid RequireGuid(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String &&
        Guid.TryParseExact(root.GetProperty(name).GetString(), "D", out var value) && value != Guid.Empty
            ? value
            : throw Invalid();

    private static string RequireString(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(root.GetProperty(name).GetString())
            ? root.GetProperty(name).GetString()!
            : throw Invalid();

    private static NotificationMessageException Invalid() =>
        new(NotificationErrorCodes.InvalidPayload);
}

public sealed class NotificationMessageException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public static class NotificationTemplate
{
    public const string Key = "orders.created.operations";
    public const int Version = 1;
    public const string Channel = "IN_APP";
    public static readonly IReadOnlySet<string> AllowedVariables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "order_public_id",
            "order_status",
            "occurred_at",
        };
}

public static class NotificationTemplateRenderer
{
    public static string Render(string body, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (!variables.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(NotificationTemplate.AllowedVariables))
        {
            throw new NotificationMessageException(NotificationErrorCodes.TemplateVariableUnknown);
        }

        var rendered = body;
        foreach (var variable in variables.OrderBy(static value => value.Key, StringComparer.Ordinal))
        {
            rendered = rendered.Replace(
                "{{" + variable.Key + "}}",
                variable.Value,
                StringComparison.Ordinal);
        }

        if (rendered.Contains("{{", StringComparison.Ordinal) || rendered.Contains("}}", StringComparison.Ordinal))
        {
            throw new NotificationMessageException(NotificationErrorCodes.TemplateVariableUnknown);
        }

        return rendered;
    }
}

public static class NotificationIdempotencyKey
{
    public static string Create(Guid notificationId, string templateKey, int templateVersion, string channel)
    {
        if (notificationId == Guid.Empty || string.IsNullOrWhiteSpace(templateKey) ||
            templateVersion < 1 || channel != NotificationTemplate.Channel)
        {
            throw new ArgumentException("Stable notification identity is invalid.");
        }

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{notificationId:D}|{templateKey}|{templateVersion}|{channel}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public static class NotificationRetryPolicy
{
    public static TimeSpan CalculateDelay(int attempts, int baseSeconds, int maximumSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSeconds, baseSeconds);
        var exponent = Math.Min(attempts - 1, 30);
        return TimeSpan.FromSeconds(Math.Min(maximumSeconds, baseSeconds * Math.Pow(2, exponent)));
    }
}
