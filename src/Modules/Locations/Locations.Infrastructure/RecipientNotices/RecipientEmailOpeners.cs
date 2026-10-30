using Locations.Infrastructure.Geocoding;
using Paqueteria.Application.Contacts;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Infrastructure.Security.Pii;

namespace Locations.Infrastructure.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 1): <c>Locations:PiiProtector=AzureKeyVault</c>. The e-mail is
/// opened with the ADP-001 envelope under the PII key, bound to its owner organization, location row, purpose
/// <c>locations.email</c> and key version, and normalized with <see cref="RecipientEmailPolicy"/>. A copy rejected for
/// its row, or one that is no e-mail, is unusable; an unreachable key service is unavailable.
/// </summary>
public sealed class EnvelopeRecipientEmailOpener(IPiiEnvelopeProtector envelope) : IRecipientEmailOpener
{
    public async Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        string opened;
        try
        {
            opened = await envelope.UnprotectAsync(
                    new PiiBinding(email.OwnerOrganizationId, email.LocationId),
                    AzureKeyVaultLocationPiiProtector.EmailPurpose,
                    email.Ciphertext,
                    email.KeyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PiiCiphertextRejectedException)
        {
            return OpenedRecipientEmail.Unusable;
        }
        catch (Exception)
        {
            return OpenedRecipientEmail.Unavailable;
        }

        return RecipientEmailPolicy.TryNormalize(opened, out var normalized)
            ? OpenedRecipientEmail.Opened(normalized.ToLowerInvariant())
            : OpenedRecipientEmail.Unusable;
    }
}

/// <summary>
/// <c>Locations:PiiProtector=Mock</c> (Development, Testing and DEV_SYNTHETIC): the mock stored one-way digests, so
/// nothing is decrypted; every e-mail is the AI-24 placeholder <c>destinatario@example.invalid</c>
/// (<c>recipient_notices.contact.synthetic</c>), the same value the Worker's synthetic provider sends to.
/// </summary>
public sealed class SyntheticRecipientEmailOpener : IRecipientEmailOpener
{
    public const string Placeholder = "destinatario@example.invalid";

    public Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenedRecipientEmail.Opened(Placeholder));
    }
}

/// <summary>No PII protector is configured: no e-mail can be opened.</summary>
public sealed class DisabledRecipientEmailOpener : IRecipientEmailOpener
{
    public Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        return Task.FromResult(OpenedRecipientEmail.Unavailable);
    }
}
