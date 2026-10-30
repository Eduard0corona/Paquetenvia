using Locations.Infrastructure.Geocoding;
using Paqueteria.Application.Contacts;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Infrastructure.Security.Pii;

namespace Locations.Infrastructure.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (T6 choices 5 and 12): <c>Locations:PiiProtector=AzureKeyVault</c>. The phone
/// is opened with the ADP-001 envelope under the PII key, bound to its owner organization, location row, purpose
/// <c>locations.phone</c> and key version (the VOICE-001 recipient precedent), and normalized with
/// <see cref="MexicanPhonePolicy"/> to E.164 <c>+52</c>, the value AI-24 <c>recipient_notices.opt_out.hash</c> hashes. A copy
/// rejected for its row, or one that is no Mexican number, is unusable; an unreachable key service is unavailable.
/// </summary>
public sealed class EnvelopeRecipientPhoneOpener(IPiiEnvelopeProtector envelope) : IRecipientPhoneOpener
{
    public async Task<OpenedRecipientPhone> OpenAsync(StoredRecipientPhone phone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phone);
        string opened;
        try
        {
            opened = await envelope.UnprotectAsync(
                    new PiiBinding(phone.OwnerOrganizationId, phone.LocationId),
                    AzureKeyVaultLocationPiiProtector.PhonePurpose,
                    phone.Ciphertext,
                    phone.KeyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PiiCiphertextRejectedException)
        {
            return OpenedRecipientPhone.Unusable;
        }
        catch (Exception)
        {
            return OpenedRecipientPhone.Unavailable;
        }

        return MexicanPhonePolicy.TryNormalize(opened, out var digits)
            ? OpenedRecipientPhone.Opened(MexicanPhonePolicy.CountryPrefix + digits)
            : OpenedRecipientPhone.Unusable;
    }
}

/// <summary>
/// <c>Locations:PiiProtector=Mock</c> (Development, Testing and DEV_SYNTHETIC): the mock stored one-way digests, so
/// nothing is decrypted; every phone is the AI-24 placeholder <c>+520000000002</c>
/// (<c>recipient_notices.contact.synthetic</c>, as VOICE-001), the same value the Worker's synthetic provider sends to.
/// An opt-out recorded for it silences every synthetic WhatsApp notice of that database until it is reset (T6 choice 11).
/// </summary>
public sealed class SyntheticRecipientPhoneOpener : IRecipientPhoneOpener
{
    public const string Placeholder = "+520000000002";

    public Task<OpenedRecipientPhone> OpenAsync(StoredRecipientPhone phone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phone);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenedRecipientPhone.Opened(Placeholder));
    }
}

/// <summary>No PII protector is configured: no phone can be opened.</summary>
public sealed class DisabledRecipientPhoneOpener : IRecipientPhoneOpener
{
    public Task<OpenedRecipientPhone> OpenAsync(StoredRecipientPhone phone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return Task.FromResult(OpenedRecipientPhone.Unavailable);
    }
}
