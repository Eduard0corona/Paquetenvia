using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;

namespace Notifications.Infrastructure.RecipientNotices.Panel;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; T6 choices 2 to 8): the dispatcher's list and "Detener avisos".
/// <list type="number">
/// <item>The list is one tenant transaction of the selected organization that reads the order's notices only when that
/// organization owns it, with the consent box and whether the destination holds an e-mail; nothing is decrypted.</item>
/// <item>A stop reads, in one tenant transaction that then commits, the stored answer of the same press or the
/// destination's PII ciphertexts; with no transaction open it opens them in memory with the API's PII key (Locations'
/// openers, as VOICE-001 and the NTF-006 unsubscribe) and hashes each usable one under every configured contact lookup
/// key version; then one transaction records the opt-outs (source DISPATCHER), the audit row and the stored answer
/// (AI-13 §7.2). A contact that cannot be opened leaves its channel out; with none left the answer is 409 and nothing is
/// written; without the PII key or the contact lookup key it is 503.</item>
/// </list>
/// Log lines carry the lane and the outcome only; never a phone, an e-mail, a hash, an identifier or a key.
/// </summary>
internal sealed class RecipientNoticePanelService(
    IRecipientNoticePanelStore store,
    IRecipientContactLookup contactLookup,
    IServiceProvider services,
    TimeProvider time,
    ILogger<RecipientNoticePanelService> logger) : IRecipientNoticePanelService
{
    public async Task<RecipientNoticeListResult> ListAsync(
        ListOrderRecipientNoticesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ActorId == Guid.Empty || query.OrganizationId == Guid.Empty || query.OrderId == Guid.Empty)
        {
            throw new RecipientNoticePanelNotFoundException();
        }

        var result = await store.ListAsync(query.ActorId, query.OrganizationId, query.OrderId, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            Log("PANEL_LIST", "NOT_FOUND");
            throw new RecipientNoticePanelNotFoundException();
        }

        Log("PANEL_LIST", "LISTED");
        return result;
    }

    public async Task<RecipientNoticeStopResult> StopAsync(
        StopOrderRecipientNoticesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IdempotencyKeyPolicy.IsValid(command.IdempotencyKey))
        {
            throw new RecipientNoticeStopConflictException(RecipientNoticeStopConflictCodes.InvalidRequest);
        }

        if (command.ActorId == Guid.Empty || command.OrganizationId == Guid.Empty || command.OrderId == Guid.Empty)
        {
            throw new RecipientNoticePanelNotFoundException();
        }

        var requestHash = RequestHash(command);
        var read = await store.ReadStopAsync(command, requestHash, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            Log("PANEL_STOP", "NOT_FOUND");
            throw new RecipientNoticePanelNotFoundException();
        }

        if (read.Replay is { } replay)
        {
            Log("PANEL_STOP", "REPLAYED");
            return replay;
        }

        if (!contactLookup.IsAvailable)
        {
            Log("PANEL_STOP", "LOOKUP_KEY_UNAVAILABLE");
            throw new RecipientNoticePanelUnavailableException();
        }

        // The transaction that read the ciphertexts is committed: the values live in memory only from here.
        var optOuts = new List<RecipientNoticeStopOptOut>(2);
        if (read.Contact is { } contact)
        {
            if (contact.PhoneCiphertext is { Length: > 0 } phoneCiphertext &&
                await OpenPhoneAsync(contact, phoneCiphertext, cancellationToken).ConfigureAwait(false) is { } phone)
            {
                optOuts.Add(new RecipientNoticeStopOptOut(
                    RecipientNoticePanelContract.WhatsAppChannel,
                    Hash(RecipientContactChannel.WhatsApp, phone)));
            }

            if (contact.EmailCiphertext is { Length: > 0 } emailCiphertext &&
                await OpenEmailAsync(contact, emailCiphertext, cancellationToken).ConfigureAwait(false) is { } email)
            {
                optOuts.Add(new RecipientNoticeStopOptOut(
                    RecipientNoticePanelContract.EmailChannel,
                    Hash(RecipientContactChannel.Email, email)));
            }
        }

        if (optOuts.Count == 0)
        {
            Log("PANEL_STOP", "NO_USABLE_CONTACT");
            throw new RecipientNoticeStopConflictException(RecipientNoticeStopConflictCodes.RecipientContactUnavailable);
        }

        var recordedAt = UtcMicrosecondPrecision.Normalize(time.GetUtcNow());
        var result = await store.RecordStopAsync(
                new RecipientNoticeStopWrite(command, requestHash, optOuts, recordedAt),
                cancellationToken)
            .ConfigureAwait(false);
        Log("PANEL_STOP", "RECORDED");
        return result;
    }

    /// <summary>
    /// The identity of one press: the organization, the person, the order and the operation, so a key reused for another
    /// order, or by another person, is a conflict and never a replay.
    /// </summary>
    internal static byte[] RequestHash(StopOrderRecipientNoticesCommand command) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"NTF-007|STOP_RECIPIENT_NOTICES|v1|{command.OrganizationId:D}|{command.ActorId:D}|{command.OrderId:D}"));

    private async Task<string?> OpenPhoneAsync(
        RecipientNoticeStopContact contact,
        byte[] ciphertext,
        CancellationToken cancellationToken)
    {
        var phones = services.GetRequiredService<IRecipientPhoneOpener>();
        var opened = await phones.OpenAsync(
                new StoredRecipientPhone(contact.OwnerOrganizationId, contact.LocationId, contact.KeyVersion, ciphertext),
                cancellationToken)
            .ConfigureAwait(false);
        return Usable(opened.Status == RecipientPhoneOpenStatus.Unavailable, opened.Status == RecipientPhoneOpenStatus.Opened,
            RecipientContactChannel.WhatsApp, opened.Normalized);
    }

    private async Task<string?> OpenEmailAsync(
        RecipientNoticeStopContact contact,
        byte[] ciphertext,
        CancellationToken cancellationToken)
    {
        var emails = services.GetRequiredService<IRecipientEmailOpener>();
        var opened = await emails.OpenAsync(
                new StoredRecipientEmail(contact.OwnerOrganizationId, contact.LocationId, contact.KeyVersion, ciphertext),
                cancellationToken)
            .ConfigureAwait(false);
        return Usable(opened.Status == RecipientEmailOpenStatus.Unavailable, opened.Status == RecipientEmailOpenStatus.Opened,
            RecipientContactChannel.Email, opened.Normalized);
    }

    /// <summary>T6 choice 8: an unreachable PII key is 503; a value that cannot be opened or used leaves its channel out.</summary>
    private string? Usable(bool unavailable, bool opened, RecipientContactChannel channel, string? value)
    {
        if (unavailable)
        {
            Log("PANEL_STOP", "PII_KEY_UNAVAILABLE");
            throw new RecipientNoticePanelUnavailableException();
        }

        if (opened && RecipientContactValue.TryNormalize(channel, value, out var normalized))
        {
            return normalized;
        }

        Log("PANEL_STOP", channel == RecipientContactChannel.WhatsApp ? "PHONE_UNUSABLE" : "EMAIL_UNUSABLE");
        return null;
    }

    private IReadOnlyList<ContactLookupHash> Hash(RecipientContactChannel channel, string normalized)
    {
        try
        {
            return contactLookup.HashForLookup(channel, normalized);
        }
        catch (ContactLookupUnavailableException exception)
        {
            Log("PANEL_STOP", "LOOKUP_KEY_UNAVAILABLE");
            throw new RecipientNoticePanelUnavailableException(exception);
        }
    }

    // AI-24 recipient_notices.logging: the lane and the outcome only.
    private void Log(string lane, string outcome) =>
        logger.LogInformation(
            "Recipient notice panel processed with lane {Lane}, outcome {Outcome}.",
            lane,
            outcome);
}
