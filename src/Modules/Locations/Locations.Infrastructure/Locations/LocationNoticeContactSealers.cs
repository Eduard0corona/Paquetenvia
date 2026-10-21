using System.Security.Cryptography;
using System.Text;
using Locations.Application.Geocoding;
using Paqueteria.Infrastructure.Security.Pii;

namespace Locations.Infrastructure.Locations;

/// <summary>
/// <c>Locations:NoticeKeyProtector=Disabled</c> (the default, and the pilot value until AI-08 NTF-008 wires the key):
/// no recipient-notice key exists, so no destination gets a notice copy and the quote is unaffected. Recipient
/// notices stay off (AI-24 <c>recipient_notices.switches</c>); an order without notice copies never gets one.
/// </summary>
public sealed class DisabledLocationNoticeContactSealer : ILocationNoticeContactSealer
{
    public Task<SealedLocationNoticeContact?> SealAsync(
        LocationNoticeContactValues values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<SealedLocationNoticeContact?>(null);
    }
}

/// <summary>
/// <c>Locations:NoticeKeyProtector=Mock</c> (Development, Testing and DEV_SYNTHETIC only, like the mock PII protector):
/// one-way digests under a synthetic-only key version, so nothing can be opened. The Worker's synthetic contact
/// resolver (AI-08 NTF-005) never decrypts and uses the AI-24 placeholders instead.
/// </summary>
public sealed class DeterministicMockLocationNoticeContactSealer : ILocationNoticeContactSealer
{
    /// <summary>The synthetic-only notice key version; it is never a Staging or Production key.</summary>
    public const string KeyVersion = "ntf002-notice-mock-v1";

    public Task<SealedLocationNoticeContact?> SealAsync(
        LocationNoticeContactValues values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(values.Phone);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<SealedLocationNoticeContact?>(new SealedLocationNoticeContact(
            KeyVersion,
            Digest(AzureKeyVaultLocationNoticeContactSealer.NoticePhonePurpose, values.Phone),
            string.IsNullOrWhiteSpace(values.Email)
                ? null
                : Digest(AzureKeyVaultLocationNoticeContactSealer.NoticeEmailPurpose, values.Email)));
    }

    private static byte[] Digest(string purpose, string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"NTF-002-NOTICE-MOCK\0{KeyVersion}\0{purpose}\0{value}"));
}

/// <summary>
/// <c>Locations:NoticeKeyProtector=AzureKeyVault</c>: the ADP-001 envelope format v1 under the recipient-notice key
/// (Key Vault RSA key <c>recipient-notice-kek</c>, wrapped with its public part), bound to the owning organization,
/// the location row and the purpose. The API holds no way to open a notice copy; only the Worker unwraps
/// (AI-24 <c>recipient_notices.contact.roles</c>). Any failure is <see cref="LocationPiiProtectionUnavailableException"/>
/// (503, nothing written).
/// </summary>
public sealed class AzureKeyVaultLocationNoticeContactSealer(IRecipientNoticeEnvelopeSealer sealer) : ILocationNoticeContactSealer
{
    public const string NoticePhonePurpose = "locations.notice_phone";
    public const string NoticeEmailPurpose = "locations.notice_email";

    public async Task<SealedLocationNoticeContact?> SealAsync(
        LocationNoticeContactValues values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(values.Phone);
        var hasEmail = !string.IsNullOrWhiteSpace(values.Email);
        var inputs = new List<PiiPlaintext> { new(NoticePhonePurpose, values.Phone) };
        if (hasEmail)
        {
            inputs.Add(new PiiPlaintext(NoticeEmailPurpose, values.Email!));
        }

        PiiProtectedBatch batch;
        try
        {
            batch = await sealer.SealAsync(
                    new PiiBinding(values.OwnerOrganizationId, values.LocationId),
                    inputs,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocationPiiProtectionUnavailableException(exception);
        }

        if (batch.Ciphertexts.Count != inputs.Count || string.IsNullOrWhiteSpace(batch.KeyVersion))
        {
            throw new LocationPiiProtectionUnavailableException();
        }

        return new SealedLocationNoticeContact(
            batch.KeyVersion,
            batch.Ciphertexts[0],
            hasEmail ? batch.Ciphertexts[1] : null);
    }
}
