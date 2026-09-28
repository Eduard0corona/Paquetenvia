using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Audience;
using Notifications.Application.Dispatching;
using Notifications.Infrastructure.Delivery;
using Npgsql;
using Paqueteria.Infrastructure.Observability;

namespace Notifications.Infrastructure.Dispatching;

internal sealed class NotificationsOutboxProcessor(
    INotificationsStore store,
    NotificationAudienceResolver audience,
    ISyntheticInAppProvider provider,
    IOptions<NotificationsOptions> options,
    OutboxLaneMonitor lanes,
    ILogger<NotificationsOutboxProcessor> logger)
{
    public async Task ProcessOwnedAsync(
        ClaimedNotificationOutboxMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (message.Topic)
            {
                case NotificationOutboxTopics.OrdersCreated:
                    await ProcessSourceAsync(message, cancellationToken);
                    return;
                case NotificationOutboxTopics.SendRequested:
                    await ProcessSendRequestAsync(message, cancellationToken);
                    return;
                default:
                    await SettleAsync(message, "DEAD", NotificationErrorCodes.UnknownTopic, null, cancellationToken);
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            logger.LogWarning("Notifications processing rolled back with outcome {Outcome}.", "DATABASE_TRANSIENT");
        }
    }

    public Task ProcessUnownedAsync(
        ClaimedNotificationOutboxMessage message,
        CancellationToken cancellationToken) =>
        SettleAsync(message, "DEAD", NotificationErrorCodes.UnknownTopic, null, cancellationToken);

    public async Task ProcessRecoveredAsync(
        ClaimedNotificationOutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Topic != NotificationOutboxTopics.SendRequested ||
            !TryParseSendRequest(message, out var notificationId))
        {
            await SettleAsync(message, "DEAD", NotificationErrorCodes.InvalidPayload, null, cancellationToken);
            return;
        }

        var delivery = await store.ReadDeliveryAsync(
            notificationId,
            message.OwnerOrganizationId,
            cancellationToken);
        if (delivery is null || delivery.Status != "PENDING" ||
            !await store.FinalizeMaximumAttemptsAsync(
                message,
                delivery,
                DateTimeOffset.UtcNow,
                cancellationToken))
        {
            throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        }

        LogOutcome("MAX_ATTEMPTS", NotificationErrorCodes.MaxAttemptsExhausted);
    }

    private async Task ProcessSourceAsync(
        ClaimedNotificationOutboxMessage message,
        CancellationToken cancellationToken)
    {
        ParsedOrderCreated parsed;
        try
        {
            parsed = OrderCreatedParser.Parse(message);
        }
        catch (NotificationMessageException exception)
        {
            await SettleAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
            return;
        }

        IReadOnlyList<Guid> recipients;
        try
        {
            recipients = await audience.ResolveAsync(parsed.OwnerOrganizationId, cancellationToken);
        }
        catch (NotificationAudienceException exception)
        {
            await SettleAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
            return;
        }

        var code = await store.ExpandSourceAsync(message, parsed, recipients, cancellationToken);
        if (code == NotificationErrorCodes.LeaseLost)
        {
            throw new NotificationMessageException(code);
        }

        LogOutcome("SOURCE", code);
    }

    private async Task ProcessSendRequestAsync(
        ClaimedNotificationOutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (!TryParseSendRequest(message, out var notificationId))
        {
            await SettleAsync(message, "DEAD", NotificationErrorCodes.InvalidPayload, null, cancellationToken);
            return;
        }

        var delivery = await store.ReadDeliveryAsync(notificationId, message.OwnerOrganizationId, cancellationToken);
        if (delivery is null || delivery.Status != "PENDING" || delivery.Channel != NotificationTemplate.Channel)
        {
            await SettleAsync(message, "DEAD", NotificationErrorCodes.InvalidPayload, null, cancellationToken);
            return;
        }

        if (message.Attempts > options.Value.MaximumAttempts)
        {
            if (!await store.FinalizeMaximumAttemptsAsync(
                    message,
                    delivery,
                    DateTimeOffset.UtcNow,
                    cancellationToken))
            {
                throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
            }

            LogOutcome("MAX_ATTEMPTS", NotificationErrorCodes.MaxAttemptsExhausted);
            return;
        }

        IReadOnlyDictionary<string, string> variables;
        try
        {
            variables = ParseVariables(delivery.VariablesSnapshot);
        }
        catch (NotificationMessageException exception)
        {
            await SettleAsync(message, "DEAD", exception.ErrorCode, null, cancellationToken);
            return;
        }

        var idempotencyKey = NotificationIdempotencyKey.Create(
            delivery.NotificationId,
            delivery.TemplateKey,
            delivery.TemplateVersion,
            delivery.Channel);
        var result = await provider.SendAsync(
            new(
                delivery.NotificationId,
                delivery.TemplateKey,
                delivery.TemplateVersion,
                delivery.Channel,
                delivery.TemplateBody,
                variables,
                idempotencyKey),
            cancellationToken);
        var next = NotificationDeliveryOutcome.IsRetried(result.Outcome)
            ? DateTimeOffset.UtcNow + NotificationRetryPolicy.CalculateDelay(
                message.Attempts,
                options.Value.RetryBaseSeconds,
                options.Value.RetryMaximumSeconds)
            : (DateTimeOffset?)null;
        if (!await store.ApplyOutcomeAsync(
                message,
                delivery,
                result.Outcome,
                result.Code,
                DateTimeOffset.UtcNow,
                next,
                cancellationToken))
        {
            throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        }

        LogOutcome(result.Outcome, result.Code);
    }

    private static bool TryParseSendRequest(
        ClaimedNotificationOutboxMessage message,
        out Guid notificationId)
    {
        notificationId = Guid.Empty;
        if (message.AggregateType != "Notification" ||
            message.AggregateId == Guid.Empty ||
            message.AggregateVersion is null or < 1)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("schema_version", out var schema) ||
                schema.GetString() != "notification-send-requested-v1" ||
                !root.TryGetProperty("notification_id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(id.GetString(), "D", out notificationId) ||
                notificationId != message.AggregateId)
            {
                notificationId = Guid.Empty;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseVariables(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new NotificationMessageException(NotificationErrorCodes.TemplateVariableUnknown);
            }

            var result = document.RootElement.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : throw new NotificationMessageException(NotificationErrorCodes.TemplateVariableUnknown),
                StringComparer.Ordinal);
            if (!result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(NotificationTemplate.AllowedVariables))
            {
                throw new NotificationMessageException(NotificationErrorCodes.TemplateVariableUnknown);
            }

            return result;
        }
        catch (JsonException)
        {
            throw new NotificationMessageException(NotificationErrorCodes.TemplateVariableUnknown);
        }
    }

    private async Task SettleAsync(
        ClaimedNotificationOutboxMessage message,
        string status,
        string code,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken)
    {
        if (!await store.SettleAsync(message.Id, message.LeaseToken, status, code, availableAt, cancellationToken))
        {
            throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        }

        LogOutcome(status, code);
    }

    private void LogOutcome(string outcome, string code)
    {
        if (Settlement(outcome, code) is { } settlement)
        {
            lanes.Settled(OutboxLanes.Notifications, settlement);
        }

        logger.LogInformation(
            "Notifications dispatcher completed with owner {Owner}, channel {Channel}, outcome {Outcome}, code {Code}.",
            "NOTIFICATIONS",
            "IN_APP",
            outcome,
            code);
    }

    /// <summary>
    /// OBS-002: the outbox status each logged outcome leaves behind (AI-06 settle, apply and expand
    /// functions): a source row is PROCESSED once expanded or without recipients and DEAD otherwise.
    /// </summary>
    internal static OutboxSettlement? Settlement(string outcome, string code) => outcome switch
    {
        "DEAD" or "MAX_ATTEMPTS" or NotificationDeliveryOutcome.Permanent => OutboxSettlement.Dead,
        NotificationDeliveryOutcome.Success => OutboxSettlement.Processed,
        NotificationDeliveryOutcome.Transient or NotificationDeliveryOutcome.Ambiguous => OutboxSettlement.Retry,
        "SOURCE" => code is NotificationErrorCodes.SourceExpanded or NotificationErrorCodes.NoEligibleRecipient
            ? OutboxSettlement.Processed
            : OutboxSettlement.Dead,
        _ => null,
    };
}
