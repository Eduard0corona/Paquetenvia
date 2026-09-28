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
/// location row the ciphertexts are bound to.
/// </summary>
public sealed record LocationPiiValues(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string AddressText,
    string? ContactName,
    string? Phone);

/// <summary>
/// The protected values of one location, all under <see cref="KeyVersion"/>, which the row
/// persists in <c>pii_key_version</c>. Optional values stay <see langword="null"/>.
/// </summary>
public sealed record ProtectedLocationPii(
    string KeyVersion,
    byte[] AddressTextCiphertext,
    byte[]? ContactNameCiphertext,
    byte[]? PhoneCiphertext);

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
