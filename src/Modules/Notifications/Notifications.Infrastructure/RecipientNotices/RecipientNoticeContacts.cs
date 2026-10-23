using Paqueteria.Application.RecipientNotices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orders.Application.RecipientNotices;
using Paqueteria.Application.Contacts;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Messaging;
using Paqueteria.Infrastructure.Security.Pii;

namespace Notifications.Infrastructure.RecipientNotices;

internal enum RecipientNoticeContactStatus
{
    /// <summary>The contact was opened (or, synthetic, replaced by its placeholder).</summary>
    Resolved,

    /// <summary>
    /// The contact cannot be used: no copy for the channel, an altered copy or a key version the Worker cannot open.
    /// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L3: FAILED with MESSAGING_RECIPIENT_INVALID, permanent.
    /// </summary>
    Unusable,

    /// <summary>Key Vault could not be reached: the send is retried with backoff (L4 choice 3).</summary>
    Unavailable,
}

/// <summary>
/// A resolved contact, in memory only: the provider recipient and the normalized value the opt-out hash is taken of.
/// <see cref="ToString"/> prints neither.
/// </summary>
internal sealed class RecipientNoticeContact
{
    public static readonly RecipientNoticeContact Unusable = new(RecipientNoticeContactStatus.Unusable, null, null);
    public static readonly RecipientNoticeContact Unavailable = new(RecipientNoticeContactStatus.Unavailable, null, null);

    private RecipientNoticeContact(RecipientNoticeContactStatus status, MessagingRecipient? recipient, string? normalized)
    {
        Status = status;
        Recipient = recipient;
        Normalized = normalized;
    }

    public RecipientNoticeContactStatus Status { get; }

    public MessagingRecipient? Recipient { get; }

    public string? Normalized { get; }

    public static RecipientNoticeContact Resolved(string normalized) =>
        new(RecipientNoticeContactStatus.Resolved, new MessagingRecipient(normalized), normalized);

    public override string ToString() => $"RecipientNoticeContact {{ Status = {Status} }}";
}

/// <summary>
/// AI-24 <c>recipient_notices.contact.read_at_send</c>: the contact of a destination for one channel. The copies are read
/// by the Locations reader in its own tenant transaction, which is committed first; only then, with no transaction
/// open, does the real resolver open the copy with the unwrap-only notice key and normalize it (the phone as E.164
/// <c>+52</c>, the e-mail with <c>RecipientEmailPolicy</c>). A channel on a synthetic provider never decrypts and uses the
/// AI-24 placeholders (<c>contact.synthetic</c>, L4 choice 5).
/// </summary>
internal sealed class RecipientNoticeContactResolver(
    IRecipientNoticeContactReader reader,
    IOptions<MessagingOptions> messaging,
    IServiceProvider services)
{
    /// <summary>AI-24 <c>recipient_notices.contact.synthetic</c>: the WhatsApp placeholder (as VOICE-001).</summary>
    public const string SyntheticPhone = "+520000000002";

    /// <summary>AI-24 <c>recipient_notices.contact.synthetic</c>: the e-mail placeholder.</summary>
    public const string SyntheticEmail = "destinatario@example.invalid";

    public async Task<RecipientNoticeContact> ResolveAsync(
        Guid ownerOrganizationId,
        Guid destinationLocationId,
        MessagingChannel channel,
        CancellationToken cancellationToken)
    {
        var copies = await reader.ReadAsync(ownerOrganizationId, destinationLocationId, cancellationToken);
        var hasCopy = channel == MessagingChannel.WhatsApp ? copies?.HasPhone == true : copies?.HasEmail == true;
        if (copies is null || !hasCopy)
        {
            return RecipientNoticeContact.Unusable;
        }

        if (RecipientNoticeProviders.IsSynthetic(messaging.Value, channel))
        {
            return RecipientNoticeContact.Resolved(channel == MessagingChannel.WhatsApp ? SyntheticPhone : SyntheticEmail);
        }

        if (!RecipientNoticeProviders.IsReal(messaging.Value, channel))
        {
            return RecipientNoticeContact.Unusable;
        }

        // The real path: the transaction that read the copies is committed; the value lives in memory only from here.
        var opener = services.GetRequiredService<IRecipientNoticeEnvelopeOpener>();
        string opened;
        try
        {
            opened = await opener.OpenAsync(
                new PiiBinding(copies.OwnerOrganizationId, copies.LocationId),
                channel == MessagingChannel.WhatsApp
                    ? RecipientNoticeContactCopies.PhonePurpose
                    : RecipientNoticeContactCopies.EmailPurpose,
                channel == MessagingChannel.WhatsApp ? copies.PhoneCiphertext! : copies.EmailCiphertext!,
                copies.KeyVersion!,
                cancellationToken);
        }
        catch (PiiCiphertextRejectedException)
        {
            return RecipientNoticeContact.Unusable;
        }
        catch (PiiProtectionUnavailableException)
        {
            return RecipientNoticeContact.Unavailable;
        }

        if (channel == MessagingChannel.WhatsApp)
        {
            return MexicanPhonePolicy.TryNormalize(opened, out var digits)
                ? RecipientNoticeContact.Resolved(RecipientContactValue.PhoneCountryPrefix + digits)
                : RecipientNoticeContact.Unusable;
        }

        return RecipientEmailPolicy.TryNormalize(opened, out var email)
            ? RecipientNoticeContact.Resolved(email)
            : RecipientNoticeContact.Unusable;
    }
}

/// <summary>
/// The fallback of the Worker readers when the Orders and Locations recipient notice readers are not composed (hosts
/// that run the Notifications lane without them, such as the NTF-001 tests). Recipient notices then refuse to start
/// enabled; while they are off nothing calls these members.
/// </summary>
internal sealed class UnavailableRecipientNoticeReaders :
    IRecipientNoticeOrderReader,
    IRecipientNoticeContactReader,
    IRecipientNoticeTrackingLinks
{
    public RecipientNoticeLinkReadiness Readiness => RecipientNoticeLinkReadiness.Unavailable;

    public Task<RecipientNoticeOrderSnapshot?> ReadAsync(Guid ownerOrganizationId, Guid orderId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The Orders recipient notice reader is not composed in this host.");

    Task<RecipientNoticeContactCopies?> IRecipientNoticeContactReader.ReadAsync(
        Guid ownerOrganizationId,
        Guid locationId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The Locations recipient notice reader is not composed in this host.");

    public RecipientNoticeTrackingLink? TryBuild(Guid orderId, RecipientNoticeLinkRecord link) => null;
}

/// <summary>
/// The fallback <see cref="IMessagingProvider"/> of hosts that do not compose the GATE-004 adapters: every channel is
/// Disabled, as <c>Messaging</c> is by default.
/// </summary>
internal sealed class DisabledRecipientNoticeMessagingProvider : IMessagingProvider
{
    public ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new MessagingResult(MessagingOutcome.PermanentFailure, MessagingResultCodes.ChannelDisabled));
    }
}
