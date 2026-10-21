namespace Locations.Application.Geocoding;

public sealed record GeocodingRequest(string AddressText, string AddressSummary, double Latitude, double Longitude);

public sealed record GeocodingResult(
    string AddressSummary,
    double Latitude,
    double Longitude,
    string ProviderMode,
    bool UsedManualCoordinates);

public interface IGeocodingProvider
{
    Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The personal values of one location, before protection, with the owning organization and the
/// location row the ciphertexts are bound to. NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002): <see cref="Email"/> is
/// the optional delivery recipient e-mail, already normalized by <c>RecipientEmailPolicy</c>, and exists only on the
/// destination of a quote. <see cref="ToString"/> never prints a value.
/// </summary>
public sealed record LocationPiiValues(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string AddressText,
    string? ContactName,
    string? Phone,
    string? Email = null)
{
    public override string ToString() => $"LocationPiiValues {{ LocationId = {LocationId:D}, [redacted] }}";
}

/// <summary>
/// The protected values of one location, all under <see cref="KeyVersion"/>, which the row
/// persists in <c>pii_key_version</c>. Optional values stay <see langword="null"/>; <see cref="EmailCiphertext"/> is
/// sealed with the purpose <c>locations.email</c> (NTF-002). <see cref="ToString"/> never prints a ciphertext.
/// </summary>
public sealed record ProtectedLocationPii(
    string KeyVersion,
    byte[] AddressTextCiphertext,
    byte[]? ContactNameCiphertext,
    byte[]? PhoneCiphertext,
    byte[]? EmailCiphertext = null)
{
    public override string ToString() => $"ProtectedLocationPii {{ KeyVersion = {KeyVersion}, [ciphertexts redacted] }}";
}

public interface ILocationPiiProtector
{
    /// <summary>
    /// AI05-REMOVE-PII-KEY-VERSION / ADP-001: protects every personal value of a location under
    /// the key version the protector selects. The server chooses the version and persists it next
    /// to the ciphertexts; a client never supplies it. An implementation that cannot protect
    /// throws <see cref="LocationPiiProtectionUnavailableException"/> and never returns plaintext.
    /// </summary>
    Task<ProtectedLocationPii> ProtectAsync(LocationPiiValues values, CancellationToken cancellationToken);
}

public sealed class LocationPiiProtectionUnavailableException(Exception? innerException = null)
    : Exception("Location PII protection is unavailable.", innerException);

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2; AI-24 <c>recipient_notices.contact</c>; AI-08 NTF-002): the destination
/// phone (ten normalized digits) and the optional normalized e-mail of one quote destination, with the owning
/// organization and the location row the notice copies are bound to. <see cref="ToString"/> never prints a value.
/// </summary>
public sealed record LocationNoticeContactValues(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string Phone,
    string? Email)
{
    public override string ToString() => $"LocationNoticeContactValues {{ LocationId = {LocationId:D}, [redacted] }}";
}

/// <summary>
/// The notice copies of one destination, both under <see cref="KeyVersion"/> of the recipient-notice key (the row
/// persists it in <c>notice_key_version</c>): <c>notice_phone_ciphertext</c> (purpose <c>locations.notice_phone</c>)
/// and, when the destination has an e-mail, <c>notice_email_ciphertext</c> (purpose <c>locations.notice_email</c>).
/// <see cref="ToString"/> never prints a ciphertext.
/// </summary>
public sealed record SealedLocationNoticeContact(
    string KeyVersion,
    byte[] PhoneCiphertext,
    byte[]? EmailCiphertext)
{
    public override string ToString() =>
        $"SealedLocationNoticeContact {{ KeyVersion = {KeyVersion}, [ciphertexts redacted] }}";
}

public interface ILocationNoticeContactSealer
{
    /// <summary>
    /// Seals the notice copies of a quote destination under the recipient-notice key, a key the API can only wrap
    /// with (it never opens a notice copy). Returns <see langword="null"/> when no recipient-notice key is configured
    /// (<c>Locations:NoticeKeyProtector=Disabled</c>, the default and the pilot value until AI-08 NTF-008): the row then
    /// carries no notice copy and the quote is unaffected. A configured sealer that cannot seal throws
    /// <see cref="LocationPiiProtectionUnavailableException"/> and never returns plaintext.
    /// </summary>
    Task<SealedLocationNoticeContact?> SealAsync(LocationNoticeContactValues values, CancellationToken cancellationToken);
}
