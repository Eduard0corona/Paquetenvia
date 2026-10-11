using System.Security.Cryptography;
using System.Text;
using Drivers.Application.Voice;
using Paqueteria.Application.Contacts;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Security.Pii;

namespace Drivers.Infrastructure.Voice;

/// <summary>The ADP-001 purposes (columns) of the two phones the bridge uses.</summary>
public static class VoicePiiPurposes
{
    /// <summary><c>drivers.driver_profiles.phone_ciphertext</c>.</summary>
    public const string DriverPhone = "drivers.phone";

    /// <summary>
    /// <c>locations.locations.phone_ciphertext</c>. It must stay equal to the Locations protector's purpose
    /// (<c>AzureKeyVaultLocationPiiProtector.PhonePurpose</c>); a test pins both.
    /// </summary>
    public const string LocationPhone = "locations.phone";
}

public sealed record ProtectedDriverPhone(byte[] Ciphertext, string KeyVersion);

/// <summary>Protects the driver's normalized digits for one driver profile row; never logs or returns them.</summary>
public interface IDriverPhoneProtector
{
    Task<ProtectedDriverPhone> ProtectAsync(
        Guid organizationId,
        Guid driverId,
        string digits,
        CancellationToken cancellationToken);
}

/// <summary>Live mode: the ADP-001 Key Vault envelope bound to the driver's organization, row and column.</summary>
public sealed class EnvelopeDriverPhoneProtector(IPiiEnvelopeProtector envelope) : IDriverPhoneProtector
{
    public async Task<ProtectedDriverPhone> ProtectAsync(
        Guid organizationId,
        Guid driverId,
        string digits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digits);
        try
        {
            var batch = await envelope.ProtectAsync(
                    new PiiBinding(organizationId, driverId),
                    [new PiiPlaintext(VoicePiiPurposes.DriverPhone, digits)],
                    cancellationToken)
                .ConfigureAwait(false);
            if (batch.Ciphertexts is not [{ Length: > 0 } ciphertext] || string.IsNullOrWhiteSpace(batch.KeyVersion))
            {
                throw new DriverPhoneUnavailableException();
            }

            return new ProtectedDriverPhone(ciphertext, batch.KeyVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverPhoneUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DriverPhoneUnavailableException(exception);
        }
    }
}

/// <summary>
/// Synthetic mode (tests, CI, DEV_SYNTHETIC): a deterministic one-way digest, like the GEO-001 mock. It is never
/// decrypted; the synthetic resolver uses non-dialable placeholders instead.
/// </summary>
public sealed class SyntheticDriverPhoneProtector : IDriverPhoneProtector
{
    public const string KeyVersion = "voice001-synthetic-v1";

    public Task<ProtectedDriverPhone> ProtectAsync(
        Guid organizationId,
        Guid driverId,
        string digits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digits);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProtectedDriverPhone(
            SHA256.HashData(Encoding.UTF8.GetBytes($"VOICE-001-SYNTHETIC\0{KeyVersion}\0{organizationId:D}\0{driverId:D}\0{digits}")),
            KeyVersion));
    }
}

/// <summary>The stored, protected phones of one call, with the rows they are bound to.</summary>
public sealed record RecipientCallProtectedPhones(
    Guid DriverOrganizationId,
    Guid DriverId,
    byte[] DriverCiphertext,
    string DriverKeyVersion,
    Guid LocationOwnerOrganizationId,
    Guid LocationId,
    byte[] RecipientCiphertext,
    string RecipientKeyVersion)
{
    public override string ToString() => "RecipientCallProtectedPhones { [redacted] }";
}

/// <summary>Why a stored phone cannot be used for a call. It never carries the value.</summary>
public enum RecipientCallPhoneProblem
{
    /// <summary>The driver's stored number cannot be decrypted (for example, stored under another mode).</summary>
    DriverPhoneUnreadable,

    /// <summary>The driver's number is not a dialable Mexican number.</summary>
    DriverPhoneNotDialable,

    /// <summary>The recipient's number cannot be decrypted or is not a dialable Mexican number.</summary>
    RecipientPhoneUnusable,

    /// <summary>The key-encryption key could not be reached.</summary>
    ProtectionUnavailable,
}

public sealed class RecipientCallPhoneException(RecipientCallPhoneProblem problem, Exception? innerException = null)
    : Exception("A stored phone cannot be used for the call.", innerException)
{
    public RecipientCallPhoneProblem Problem { get; } = problem;
}

/// <summary>Turns the protected phones of one call into numbers, only in memory and only for the provider call.</summary>
public interface IRecipientCallPhoneResolver
{
    Task<(VoicePhoneNumber Driver, VoicePhoneNumber Recipient)> ResolveAsync(
        RecipientCallProtectedPhones phones,
        CancellationToken cancellationToken);
}

/// <summary>
/// Live mode: decrypts both phones with the ADP-001 envelope (bound to their tenant, row and column), normalizes
/// them and requires dialable Mexican numbers. The plaintexts exist only in the returned values.
/// </summary>
public sealed class EnvelopeRecipientCallPhoneResolver(IPiiEnvelopeProtector envelope) : IRecipientCallPhoneResolver
{
    public async Task<(VoicePhoneNumber Driver, VoicePhoneNumber Recipient)> ResolveAsync(
        RecipientCallProtectedPhones phones,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phones);
        var driverDigits = await UnprotectAsync(
                new PiiBinding(phones.DriverOrganizationId, phones.DriverId),
                VoicePiiPurposes.DriverPhone,
                phones.DriverCiphertext,
                phones.DriverKeyVersion,
                RecipientCallPhoneProblem.DriverPhoneUnreadable,
                cancellationToken)
            .ConfigureAwait(false);
        var recipientDigits = await UnprotectAsync(
                new PiiBinding(phones.LocationOwnerOrganizationId, phones.LocationId),
                VoicePiiPurposes.LocationPhone,
                phones.RecipientCiphertext,
                phones.RecipientKeyVersion,
                RecipientCallPhoneProblem.RecipientPhoneUnusable,
                cancellationToken)
            .ConfigureAwait(false);

        if (!DriverPhonePolicy.TryNormalize(driverDigits, out var driver) ||
            !VoicePhoneNumber.TryFromNationalDigits(driver, out var driverNumber) || !driverNumber.IsDialable)
        {
            throw new RecipientCallPhoneException(RecipientCallPhoneProblem.DriverPhoneNotDialable);
        }

        if (!MexicanPhonePolicy.TryNormalize(recipientDigits, out var recipient) ||
            !VoicePhoneNumber.TryFromNationalDigits(recipient, out var recipientNumber) || !recipientNumber.IsDialable ||
            string.Equals(driverNumber.E164, recipientNumber.E164, StringComparison.Ordinal))
        {
            throw new RecipientCallPhoneException(RecipientCallPhoneProblem.RecipientPhoneUnusable);
        }

        return (driverNumber, recipientNumber);
    }

    private async Task<string> UnprotectAsync(
        PiiBinding binding,
        string purpose,
        byte[] ciphertext,
        string keyVersion,
        RecipientCallPhoneProblem unreadable,
        CancellationToken cancellationToken)
    {
        try
        {
            return await envelope.UnprotectAsync(binding, purpose, ciphertext, keyVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PiiCiphertextRejectedException exception)
        {
            throw new RecipientCallPhoneException(unreadable, exception);
        }
        catch (Exception exception)
        {
            throw new RecipientCallPhoneException(RecipientCallPhoneProblem.ProtectionUnavailable, exception);
        }
    }
}

/// <summary>
/// Synthetic mode: never decrypts. It returns two fixed placeholders whose national digits start with 0, so no real
/// person can be reached even if they ever reached a real provider (the Twilio adapter refuses them).
/// </summary>
public sealed class SyntheticRecipientCallPhoneResolver : IRecipientCallPhoneResolver
{
    public const string DriverPlaceholder = "+520000000001";
    public const string RecipientPlaceholder = "+520000000002";

    public Task<(VoicePhoneNumber Driver, VoicePhoneNumber Recipient)> ResolveAsync(
        RecipientCallProtectedPhones phones,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(phones);
        cancellationToken.ThrowIfCancellationRequested();
        _ = VoicePhoneNumber.TryParseE164(DriverPlaceholder, out var driver);
        _ = VoicePhoneNumber.TryParseE164(RecipientPlaceholder, out var recipient);
        return Task.FromResult((driver, recipient));
    }
}
