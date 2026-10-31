using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;

namespace Notifications.Infrastructure.RecipientNotices.Webhooks;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-24 <c>recipient_notices.receipts</c>, <c>opt_out</c> and
/// <c>auto_reply</c>): turns a verified delivery into receipts, opt-outs and automatic-reply requests. The webhook never
/// calls a provider. A sender whose number is not a Mexican +52 number (the only numbers notices go to) is skipped:
/// it can be neither hashed nor answered. The message time is clamped to now. Log lines carry the lane, the channel and
/// the outcome only, never a number, a text or a reference.
/// </summary>
internal sealed class RecipientNoticeWebhookService(
    IRecipientNoticeReceiptStore store,
    IRecipientContactLookup contactLookup,
    IRecipientAutoReplyNumberSealer sealer,
    TimeProvider time,
    ILogger<RecipientNoticeWebhookService> logger) : IRecipientNoticeWebhookService
{
    public async Task ReceiveWhatsAppAsync(MetaWhatsAppWebhookBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await RecordReceiptsAsync(batch.Receipts, cancellationToken);
        foreach (var message in batch.Messages)
        {
            if (!RecipientContactValue.TryNormalize(RecipientContactChannel.WhatsApp, message.From, out var number))
            {
                Log("WHATSAPP", "INBOUND", "SENDER_NOT_SUPPORTED");
                continue;
            }

            IReadOnlyList<ContactLookupHash> hashes;
            try
            {
                hashes = contactLookup.HashForLookup(RecipientContactChannel.WhatsApp, number);
            }
            catch (ContactLookupUnavailableException exception)
            {
                throw new RecipientNoticeStoreUnavailableException(exception);
            }

            var requestId = Guid.NewGuid();
            var now = time.GetUtcNow();
            var receivedAt = message.SentAt is { } sentAt && sentAt < now ? sentAt : now;
            var sealedNumber = await sealer.SealAsync(requestId, number, cancellationToken);
            var result = await store.RecordWhatsAppInboundAsync(
                new RecipientInboundRecord(
                    requestId,
                    message.IsOptOut,
                    hashes,
                    sealedNumber.Ciphertext,
                    sealedNumber.KeyVersion,
                    receivedAt),
                cancellationToken);
            Log("WHATSAPP", message.IsOptOut ? "OPT_OUT" : "INBOUND", Outcome(result));
        }
    }

    public Task ReceiveEmailEventsAsync(IReadOnlyList<MessagingReceipt> receipts, CancellationToken cancellationToken) =>
        RecordReceiptsAsync(receipts, cancellationToken);

    private async Task RecordReceiptsAsync(IReadOnlyList<MessagingReceipt> receipts, CancellationToken cancellationToken)
    {
        foreach (var receipt in receipts)
        {
            var result = await store.RecordReceiptAsync(receipt, cancellationToken);
            Log(receipt.Channel, "RECEIPT", result switch
            {
                MessagingReceiptResult.Recorded => "RECORDED",
                MessagingReceiptResult.Unchanged => "UNCHANGED",
                _ => "UNKNOWN_REFERENCE",
            });
        }
    }

    private static string Outcome(RecipientInboundResult result) => result switch
    {
        RecipientInboundResult.OptOutRecorded => "OPT_OUT_RECORDED",
        RecipientInboundResult.OptOutAlreadyRecorded => "OPT_OUT_ALREADY_RECORDED",
        RecipientInboundResult.AutoReplyRequested => "AUTO_REPLY_REQUESTED",
        RecipientInboundResult.AutoReplyLimited => "AUTO_REPLY_LIMITED",
        _ => "STALE",
    };

    // The lane names the webhook part (RECEIPT, INBOUND or OPT_OUT); never a number, an address or a reference.
    private void Log(string channel, string lane, string outcome) =>
        logger.LogInformation(
            "Recipient notice webhook processed with lane {Lane}, channel {Channel}, outcome {Outcome}.",
            lane,
            channel,
            outcome);
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-05 <c>unsubscribeRecipientNotices</c>; O4, O10, O11 choice 1): verifies the
/// token in constant time, reads the e-mail copy of the notice in a tenant transaction of the token's organization under
/// FORCE RLS (as VOICE-001), opens it with the PII key after that transaction committed, hashes it under every
/// configured contact lookup key version and records the EMAIL opt-out (source EMAIL_LINK). An invalid token, or a notice
/// or e-mail copy that no longer exists, is the uniform 404. No audit row is written (O10): the opt-out row is the record.
/// </summary>
internal sealed class RecipientNoticeUnsubscribeService(
    IRecipientNoticeUnsubscribeTokens tokens,
    IRecipientNoticeReceiptStore store,
    IServiceProvider services,
    IRecipientContactLookup contactLookup,
    ILogger<RecipientNoticeUnsubscribeService> logger) : IRecipientNoticeUnsubscribeService
{
    public async Task<RecipientNoticeUnsubscribeResult> UnsubscribeAsync(string? token, CancellationToken cancellationToken)
    {
        if (!tokens.TryVerify(token, out var organizationId, out var notificationId))
        {
            return Log(RecipientNoticeUnsubscribeResult.NotFound, "TOKEN_INVALID");
        }

        try
        {
            var contact = await store.ReadUnsubscribeContactAsync(organizationId, notificationId, cancellationToken);
            if (contact is null)
            {
                return Log(RecipientNoticeUnsubscribeResult.NotFound, "NOTICE_NOT_FOUND");
            }

            var emails = services.GetRequiredService<IRecipientEmailOpener>();
            var email = await emails.OpenAsync(
                new StoredRecipientEmail(contact.OwnerOrganizationId, contact.LocationId, contact.KeyVersion, contact.EmailCiphertext),
                cancellationToken);
            if (email.Status == RecipientEmailOpenStatus.Unavailable)
            {
                return Log(RecipientNoticeUnsubscribeResult.Unavailable, "KEY_UNAVAILABLE");
            }

            if (email.Status != RecipientEmailOpenStatus.Opened ||
                !RecipientContactValue.TryNormalize(RecipientContactChannel.Email, email.Normalized, out var normalized))
            {
                return Log(RecipientNoticeUnsubscribeResult.NotFound, "CONTACT_UNUSABLE");
            }

            var recorded = await store.RecordEmailOptOutAsync(
                contactLookup.HashForLookup(RecipientContactChannel.Email, normalized),
                cancellationToken);
            return Log(RecipientNoticeUnsubscribeResult.Recorded, recorded ? "OPT_OUT_RECORDED" : "OPT_OUT_ALREADY_RECORDED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is RecipientNoticeStoreUnavailableException or ContactLookupUnavailableException)
        {
            return Log(RecipientNoticeUnsubscribeResult.Unavailable, "STORE_UNAVAILABLE");
        }
    }

    private RecipientNoticeUnsubscribeResult Log(RecipientNoticeUnsubscribeResult result, string outcome)
    {
        logger.LogInformation(
            "Recipient notice unsubscribe processed with lane {Lane}, channel {Channel}, outcome {Outcome}.",
            "UNSUBSCRIBE",
            "EMAIL",
            outcome);
        return result;
    }
}
