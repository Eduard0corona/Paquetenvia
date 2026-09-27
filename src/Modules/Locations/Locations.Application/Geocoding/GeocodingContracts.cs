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

public interface ILocationPiiProtector
{
    /// <summary>
    /// AI05-REMOVE-PII-KEY-VERSION: the key version this protector writes with. The server selects it and
    /// persists it next to every ciphertext; a client never supplies it.
    /// </summary>
    string CurrentKeyVersion { get; }

    byte[] Protect(string plaintext, string keyVersion);
}

public sealed class LocationPiiProtectionUnavailableException()
    : Exception("Location PII protection is unavailable.");
