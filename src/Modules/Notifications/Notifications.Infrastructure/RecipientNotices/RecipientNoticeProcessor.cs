using System.Text.Json;
using System.Text.RegularExpressions;
using Paqueteria.Application.RecipientNotices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Dispatching;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Dispatching;
using Npgsql;
using Orders.Application.RecipientNotices;
using Orders.Application.Tracking;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Observability;

namespace Notifications.Infrastructure.RecipientNotices;

/// <summary>The recipient notice half of the Notifications lane (AI-08 NTF-005).</summary>
internal interface IRecipientNoticeLane
{
    Task ExpandAsync(ClaimedNotificationOutboxMessage message, CancellationToken cancellationToken);

    Task SendAsync(ClaimedNotificationOutboxMessage message, NotificationDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices</c>; AI-08 NTF-005) and NTF-RECIPIENT-NOTICE-CAPS-2026-10-23:
/// the Notifications lane's consumer of <c>orders.recipient-notice-requested</c> and sender of WhatsApp and e-mail
/// recipient notices.
/// <list type="bullet">
/// <item>Expansion: with the switch off the source ends PROCESSED <c>RECIPIENT_NOTICES_DISABLED</c> and nothing is read;
/// otherwise the order (public id and destination), its WhatsApp consent and whether its destination holds notice copies
/// are read under FORCE RLS - no contact value - and <c>security.expand_recipient_notice_request</c> creates one
/// Notification per eligible channel (WhatsApp with the consent and a phone copy, e-mail with an e-mail copy, each only
/// when its provider is not Disabled), holding it to the quiet-hours window.</item>
/// <item>Send, in AI-24 order and before any decryption or provider call: switches, quiet hours (a send outside the window
/// is put back to the next opening), the stale bound, validity at send, the caps
/// (<c>security.read_recipient_notice_counts</c>); then the contact in memory, the opt-out check, the link in memory and
/// one provider call with no database transaction open; the outcome is settled under the lease with
/// <c>apply_notification_outcome</c> v2.</item>
/// </list>
/// Nothing personal reaches a log, a row, an event or a trace: log lines carry the owner pseudonym (scope), the lane,
/// channel, notice, outcome and code only.
/// </summary>
internal sealed partial class RecipientNoticeProcessor(
    INotificationsStore lane,
    IRecipientNoticeStore store,
    IRecipientNoticeOrderReader orders,
    IRecipientNoticeConsentReader consents,
    IRecipientNoticeContactReader contacts,
    IRecipientNoticeTrackingLinks links,
    RecipientNoticeContactResolver resolver,
    IRecipientContactLookup contactLookup,
    IRecipientNoticeUnsubscribeTokens unsubscribeTokens,
    IMessagingProvider provider,
    PublicOrderStatusPolicy statusPolicy,
    IOptions<RecipientNoticesOptions> options,
    IOptions<MessagingOptions> messaging,
    IOptions<NotificationsOptions> notifications,
    IOptions<PublicTrackingOptions> tracking,
    OutboxLaneMonitor lanes,
    ILogger<RecipientNoticeProcessor> logger) : IRecipientNoticeLane
{
    /// <summary>The unsubscribe page of the web app (AI-07 <c>/avisos/baja/:token</c>), served by AI-08 NTF-006.</summary>
    public const string UnsubscribePagePath = "/avisos/baja/";

    /// <summary>The one-click endpoint (AI-05 <c>unsubscribeRecipientNotices</c>), served by AI-08 NTF-006.</summary>
    public const string OneClickUnsubscribePath = "/api/v1/recipient-notices/unsubscribe/";

    private const string Lane = "NOTIFICATIONS";

    public async Task ExpandAsync(ClaimedNotificationOutboxMessage message, CancellationToken cancellationToken)
    {
        ParsedRecipientNoticeRequest request;
        try
        {
            request = RecipientNoticeRequestParser.Parse(message);
        }
        catch (NotificationMessageException exception)
        {
            await SettleAsync(message, "DEAD", exception.ErrorCode, null, "-", "-", cancellationToken);
            return;
        }

        var settings = options.Value;
        var notice = RecipientNoticeCatalog.Notice(request.Notice);
        var window = settings.QuietHours.ToWindow();
        string code;
        try
        {
            if (!settings.Enabled)
            {
                // AI-24 recipient_notices.switches.enabled: nothing is read, created or decrypted.
                code = await store.ExpandRecipientNoticeAsync(
                    message, false, settings.MaximumNoticeDelay, window.Start, window.End, null, [], cancellationToken);
            }
            else
            {
                var order = await orders.ReadAsync(request.OwnerOrganizationId, request.OrderId, cancellationToken);
                var channels = new List<string>(2);
                if (order is not null)
                {
                    var copies = await contacts.ReadAsync(
                        request.OwnerOrganizationId, order.DestinationLocationId, cancellationToken);
                    // E4-P1: WhatsApp only with the recorded consent (and a phone copy); E4-P11: both when both apply,
                    // WhatsApp first; a Disabled provider is left out so it never piles up FAILED rows.
                    if (copies?.HasPhone == true &&
                        !RecipientNoticeProviders.IsDisabled(messaging.Value, MessagingChannel.WhatsApp) &&
                        await consents.HasWhatsAppConsentAsync(request.OwnerOrganizationId, request.OrderId, cancellationToken))
                    {
                        channels.Add("WHATSAPP");
                    }

                    if (copies?.HasEmail == true &&
                        !RecipientNoticeProviders.IsDisabled(messaging.Value, MessagingChannel.Email))
                    {
                        channels.Add("EMAIL");
                    }
                }

                code = await store.ExpandRecipientNoticeAsync(
                    message,
                    true,
                    settings.MaximumNoticeDelay,
                    window.Start,
                    window.End,
                    order?.PublicId,
                    channels,
                    cancellationToken);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            // The SQL validation of the leased row (INVALID_PAYLOAD) rolled back; the row ends DEAD.
            await SettleAsync(message, "DEAD", NotificationErrorCodes.InvalidPayload, null, "-", notice, cancellationToken);
            return;
        }

        if (code == NotificationErrorCodes.LeaseLost)
        {
            throw new NotificationMessageException(code);
        }

        Log(RecipientNoticeCodes.IsProcessedSourceCode(code) ? "PROCESSED" : "DEAD", code, "-", notice);
    }

    public async Task SendAsync(
        ClaimedNotificationOutboxMessage message,
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (!TryReadNotice(delivery, out var kind, out var channel, out var orderId, out var occurredAt, out var publicId))
        {
            await SettleAsync(message, "DEAD", NotificationErrorCodes.InvalidPayload, null, "-", "-", cancellationToken);
            return;
        }

        var channelName = delivery.Channel;
        var notice = RecipientNoticeCatalog.Notice(kind);
        var settings = options.Value;

        // AI-24 recipient_notices.rollback: switched off, or the provider of the channel Disabled, ends FAILED with
        // MESSAGING_CHANNEL_DISABLED before anything is read or decrypted (L4 choice 4).
        if (!settings.Enabled || RecipientNoticeProviders.IsDisabled(messaging.Value, channel))
        {
            await ApplyAsync(message, delivery, NotificationDeliveryOutcome.Permanent, MessagingResultCodes.ChannelDisabled,
                null, null, channelName, notice, cancellationToken);
            return;
        }

        var window = settings.QuietHours.ToWindow();
        PreparedSend? prepared;
        try
        {
            prepared = await PrepareAsync(
                message, delivery, kind, channel, orderId, occurredAt, publicId, window, cancellationToken);
        }
        catch (NpgsqlException exception) when (exception.IsTransient)
        {
            // L4 choice 3: a transient database failure before the provider call is retried with backoff, with no
            // attempt counted. A failure after the call is left to the lease rules of the lane (never sent twice).
            await RetryAsync(message, window, channelName, notice, cancellationToken);
            return;
        }

        if (prepared is null)
        {
            return;
        }

        // No database transaction is open here: every read above committed on its own.
        var result = await provider.SendAsync(prepared.Request, cancellationToken);
        var outcome = NotificationDeliveryOutcome.From(result.Outcome, channel);
        var next = NotificationDeliveryOutcome.IsRetried(outcome)
            ? await WithinWindowAsync(
                window,
                prepared.NowUtc + NotificationRetryPolicy.CalculateDelay(
                    message.Attempts,
                    notifications.Value.RetryBaseSeconds,
                    notifications.Value.RetryMaximumSeconds,
                    result.RetryAfter),
                cancellationToken)
            : (DateTimeOffset?)null;
        var reference = outcome == NotificationDeliveryOutcome.Success && result.ProviderReference is { } value &&
            ProviderReferencePattern().IsMatch(value)
                ? value
                : null;
        await ApplyAsync(message, delivery, outcome, NotificationDeliveryOutcome.CodeFor(outcome, result.Code),
            reference, next, channelName, notice, cancellationToken);
    }

    /// <summary>
    /// Every check before the provider call, in AI-24 order. Returns the request to send, or <see langword="null"/> when
    /// the send request was already settled (deferred, cancelled, failed or retried).
    /// </summary>
    private async Task<PreparedSend?> PrepareAsync(
        ClaimedNotificationOutboxMessage message,
        NotificationDelivery delivery,
        RecipientNoticeKind kind,
        MessagingChannel channel,
        Guid orderId,
        DateTimeOffset occurredAt,
        string publicId,
        RecipientNoticeQuietHours window,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var channelName = delivery.Channel;
        var notice = RecipientNoticeCatalog.Notice(kind);

        // AI-24 recipient_notices.quiet_hours: nothing leaves outside the window; the request waits for the next
        // opening with no attempt counted and no code (L4 choice 2).
        var clock = await store.ReadRecipientNoticeClockAsync(occurredAt, cancellationToken);
        if (!window.IsOpen(clock.NowLocal))
        {
            var opening = await store.FromMazatlanLocalAsync(window.NextOpening(clock.NowLocal), cancellationToken);
            await SettleAsync(message, "RETRY", null, opening, channelName, notice, cancellationToken, "DEFERRED");
            return null;
        }

        // AI-24 recipient_notices.staleness.send: past the bound, counted without the quiet-hours wait.
        if (window.AgeWithoutWait(clock.OccurredLocal, clock.NowLocal) > settings.MaximumNoticeDelay)
        {
            await CancelAsync(message, delivery, RecipientNoticeCodes.Stale, channelName, notice, cancellationToken);
            return null;
        }

        // AI-24 recipient_notices.validity: the current order status, read under RLS; an unmapped status throws.
        var order = await orders.ReadAsync(delivery.OwnerOrganizationId, orderId, cancellationToken);
        if (order is null || !RecipientNoticeValidity.IsValidAtSend(kind, order.Status, statusPolicy))
        {
            await CancelAsync(message, delivery, RecipientNoticeCodes.Superseded, channelName, notice, cancellationToken);
            return null;
        }

        // NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (L1, L2): the two counts, only under this request's lease.
        var counts = await store.ReadRecipientNoticeCountsAsync(message, cancellationToken)
            ?? throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        if (counts.OrderChannelCount >= settings.MaximumPerOrderAndChannel ||
            counts.DayChannelCount >= settings.DailyLimitPerChannel)
        {
            logger.LogWarning(
                "Recipient notice cap reached with lane {Lane}, channel {Channel}, notice {Notice}, code {Code}.",
                Lane,
                channelName,
                notice,
                RecipientNoticeCodes.LimitReached);
            await CancelAsync(message, delivery, RecipientNoticeCodes.LimitReached, channelName, notice, cancellationToken);
            return null;
        }

        // AI-24 recipient_notices.contact: the copies are read in their own committed transaction, then opened in
        // memory with no transaction open (synthetic providers use the placeholders and never decrypt).
        var contact = await resolver.ResolveAsync(
            delivery.OwnerOrganizationId, order.DestinationLocationId, channel, cancellationToken);
        if (contact.Status == RecipientNoticeContactStatus.Unavailable ||
            !contactLookup.IsAvailable || !unsubscribeTokens.IsAvailable)
        {
            await RetryAsync(message, window, channelName, notice, cancellationToken);
            return null;
        }

        if (contact.Status == RecipientNoticeContactStatus.Unusable)
        {
            // NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L3 "Destinatario inválido (Recommended)".
            await ApplyAsync(message, delivery, NotificationDeliveryOutcome.Permanent,
                MessagingResultCodes.RecipientInvalid, null, null, channelName, notice, cancellationToken);
            return null;
        }

        // AI-24 recipient_notices.opt_out.check: every configured key version; an opted-out contact is cancelled.
        var contactChannel = channel == MessagingChannel.WhatsApp ? RecipientContactChannel.WhatsApp : RecipientContactChannel.Email;
        var hashes = contactLookup.HashForLookup(contactChannel, contact.Normalized!)
            .Select(static hash => hash.Hash)
            .ToArray();
        if (await store.IsRecipientOptedOutAsync(channelName, hashes, cancellationToken))
        {
            await CancelAsync(message, delivery, RecipientNoticeCodes.OptedOut, channelName, notice, cancellationToken);
            return null;
        }

        // AI-24 recipient_notices.tracking_link: derived in memory and checked against the stored hash.
        var link = order.ActiveLink is null ? null : links.TryBuild(orderId, order.ActiveLink);
        if (link is null)
        {
            await ApplyAsync(message, delivery, NotificationDeliveryOutcome.Permanent,
                RecipientNoticeCodes.TrackingLinkUnavailable, null, null, channelName, notice, cancellationToken);
            return null;
        }

        var request = channel == MessagingChannel.WhatsApp
            ? new MessagingRequest(delivery.NotificationId, channel, contact.Recipient!, delivery.TemplateKey, [publicId])
            {
                UrlButtonSuffix = link.Token,
                QuickReplyPayload = RecipientNoticeTexts.WhatsAppQuickReplyPayload,
            }
            : EmailRequest(delivery, contact.Recipient!, publicId, link);
        return new PreparedSend(request, clock.NowUtc);
    }

    /// <summary>The request to send and the database time it was prepared at; <see cref="ToString"/> prints neither.</summary>
    private sealed class PreparedSend(MessagingRequest request, DateTimeOffset nowUtc)
    {
        public MessagingRequest Request { get; } = request;

        public DateTimeOffset NowUtc { get; } = nowUtc;

        public override string ToString() => "PreparedSend";
    }

    private MessagingRequest EmailRequest(
        NotificationDelivery delivery,
        MessagingRecipient recipient,
        string publicId,
        RecipientNoticeTrackingLink link)
    {
        // L4 choice 7: the page link in the body and the one-click URL in List-Unsubscribe carry the same signed token.
        var token = unsubscribeTokens.Issue(delivery.OwnerOrganizationId, delivery.NotificationId);
        var origin = tracking.Value.PublicBaseUrl!.TrimEnd('/');
        return new MessagingRequest(
            delivery.NotificationId,
            MessagingChannel.Email,
            recipient,
            delivery.TemplateKey,
            [publicId, link.Url, origin + UnsubscribePagePath + token])
        {
            OneClickUnsubscribeUrl = origin + OneClickUnsubscribePath + token,
        };
    }

    private async Task RetryAsync(
        ClaimedNotificationOutboxMessage message,
        RecipientNoticeQuietHours window,
        string channel,
        string notice,
        CancellationToken cancellationToken)
    {
        var next = await WithinWindowAsync(
            window,
            DateTimeOffset.UtcNow + NotificationRetryPolicy.CalculateDelay(
                message.Attempts,
                notifications.Value.RetryBaseSeconds,
                notifications.Value.RetryMaximumSeconds),
            cancellationToken);
        await SettleAsync(message, "RETRY", null, next, channel, notice, cancellationToken, NotificationDeliveryOutcome.Transient);
    }

    /// <summary>A retry that falls outside the window waits for its next opening (AI-24 quiet_hours.outside_window).</summary>
    private async Task<DateTimeOffset> WithinWindowAsync(
        RecipientNoticeQuietHours window,
        DateTimeOffset candidate,
        CancellationToken cancellationToken)
    {
        var local = await store.ToMazatlanLocalAsync(candidate, cancellationToken);
        return window.IsOpen(local)
            ? candidate
            : await store.FromMazatlanLocalAsync(window.NextOpening(local), cancellationToken);
    }

    private async Task CancelAsync(
        ClaimedNotificationOutboxMessage message,
        NotificationDelivery delivery,
        string code,
        string channel,
        string notice,
        CancellationToken cancellationToken)
    {
        if (!await store.CancelNotificationAsync(message, delivery, code, DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        }

        Log("CANCELLED", code, channel, notice);
    }

    private async Task ApplyAsync(
        ClaimedNotificationOutboxMessage message,
        NotificationDelivery delivery,
        string outcome,
        string code,
        string? providerReference,
        DateTimeOffset? availableAt,
        string channel,
        string notice,
        CancellationToken cancellationToken)
    {
        if (!await store.ApplyRecipientOutcomeAsync(
                message, delivery, outcome, code, providerReference, DateTimeOffset.UtcNow, availableAt, cancellationToken))
        {
            throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        }

        Log(outcome, code, channel, notice);
    }

    private async Task SettleAsync(
        ClaimedNotificationOutboxMessage message,
        string status,
        string? code,
        DateTimeOffset? availableAt,
        string channel,
        string notice,
        CancellationToken cancellationToken,
        string? outcome = null)
    {
        if (!await lane.SettleAsync(message.Id, message.LeaseToken, status, code, availableAt, cancellationToken))
        {
            throw new NotificationMessageException(NotificationErrorCodes.LeaseLost);
        }

        Log(outcome ?? status, code ?? "-", channel, notice);
    }

    private void Log(string outcome, string code, string channel, string notice)
    {
        if (Settlement(outcome) is { } settlement)
        {
            lanes.Settled(OutboxLanes.Notifications, settlement);
        }

        logger.LogInformation(
            "Recipient notice processing completed with lane {Lane}, channel {Channel}, notice {Notice}, outcome {Outcome}, code {Code}.",
            Lane,
            channel,
            notice,
            outcome,
            code);
    }

    /// <summary>OBS-002: the outbox status each logged outcome leaves behind.</summary>
    internal static OutboxSettlement? Settlement(string outcome) => outcome switch
    {
        "PROCESSED" or "CANCELLED" or NotificationDeliveryOutcome.Success => OutboxSettlement.Processed,
        "DEAD" or NotificationDeliveryOutcome.Permanent or NotificationDeliveryOutcome.AmbiguousFailed => OutboxSettlement.Dead,
        "RETRY" or "DEFERRED" or NotificationDeliveryOutcome.Transient or NotificationDeliveryOutcome.Ambiguous =>
            OutboxSettlement.Retry,
        _ => null,
    };

    /// <summary>
    /// A recipient notice row: an ORDER_DESTINATION WhatsApp or e-mail Notification of a known template, with its order,
    /// the time of its order event and the order's public id as the only variable.
    /// </summary>
    private static bool TryReadNotice(
        NotificationDelivery delivery,
        out RecipientNoticeKind kind,
        out MessagingChannel channel,
        out Guid orderId,
        out DateTimeOffset occurredAt,
        out string publicId)
    {
        channel = default;
        orderId = Guid.Empty;
        occurredAt = default;
        publicId = string.Empty;
        if (!RecipientNoticeCatalog.TryFromTemplateKey(delivery.TemplateKey, out kind) ||
            delivery.TemplateVersion != RecipientNoticeCatalog.TemplateVersion ||
            delivery.OrderId is not { } order || order == Guid.Empty ||
            delivery.CreatedAt is not { } created)
        {
            return false;
        }

        switch (delivery.Channel)
        {
            case "WHATSAPP":
                channel = MessagingChannel.WhatsApp;
                break;
            case "EMAIL":
                channel = MessagingChannel.Email;
                break;
            default:
                return false;
        }

        try
        {
            using var document = JsonDocument.Parse(delivery.VariablesSnapshot);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("order_public_id", out var value) ||
                value.ValueKind != JsonValueKind.String ||
                value.GetString() is not { Length: > 0 and <= 64 } text)
            {
                return false;
            }

            publicId = text;
        }
        catch (JsonException)
        {
            return false;
        }

        orderId = order;
        occurredAt = created;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9._:=+/-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderReferencePattern();
}
