using System.Security.Cryptography;
using System.Text;
using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Paqueteria.Infrastructure.Security.Pii;

namespace Locations.Infrastructure.Geocoding;

public sealed class DisabledGeocodingProvider : IGeocodingProvider
{
    public Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken) =>
        throw new LocationServiceUnavailableException("Geocoding is disabled.");
}

public sealed class ManualGeocodingProvider : IGeocodingProvider
{
    public Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCoordinates(request.Latitude, request.Longitude);
        return Task.FromResult(new GeocodingResult(
            NormalizeSummary(request.AddressSummary),
            request.Latitude,
            request.Longitude,
            "MANUAL",
            true));
    }

    internal static string NormalizeSummary(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    internal static void ValidateCoordinates(double latitude, double longitude)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
            double.IsNaN(latitude) || double.IsNaN(longitude) ||
            double.IsInfinity(latitude) || double.IsInfinity(longitude))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Geographic coordinates are invalid.");
        }
    }
}

public sealed class DeterministicMockGeocodingProvider : IGeocodingProvider
{
    public Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManualGeocodingProvider.ValidateCoordinates(request.Latitude, request.Longitude);
        return Task.FromResult(new GeocodingResult(
            ManualGeocodingProvider.NormalizeSummary(request.AddressSummary),
            Math.Round(request.Latitude, 6, MidpointRounding.ToEven),
            Math.Round(request.Longitude, 6, MidpointRounding.ToEven),
            "MOCK",
            false));
    }
}

public sealed class DisabledLocationPiiProtector : ILocationPiiProtector
{
    public string CurrentKeyVersion => throw new LocationPiiProtectionUnavailableException();

    public byte[] Protect(string plaintext, string keyVersion) => throw new LocationPiiProtectionUnavailableException();

    public Task<ProtectedLocationPii> ProtectAsync(LocationPiiValues values, CancellationToken cancellationToken) =>
        Task.FromException<ProtectedLocationPii>(new LocationPiiProtectionUnavailableException());
}

public sealed class DeterministicMockLocationPiiProtector : ILocationPiiProtector
{
    /// <summary>The synthetic-only key version; it is never a Staging or Production key.</summary>
    public const string KeyVersion = "geo001-mock-v1";

    public string CurrentKeyVersion => KeyVersion;

    public byte[] Protect(string plaintext, string keyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        return SHA256.HashData(Encoding.UTF8.GetBytes($"GEO-001-MOCK\0{keyVersion}\0{plaintext}"));
    }

    public Task<ProtectedLocationPii> ProtectAsync(LocationPiiValues values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProtectedLocationPii(
            KeyVersion,
            Protect(values.AddressText, KeyVersion),
            string.IsNullOrWhiteSpace(values.ContactName) ? null : Protect(values.ContactName, KeyVersion),
            string.IsNullOrWhiteSpace(values.Phone) ? null : Protect(values.Phone, KeyVersion)));
    }
}

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE: production protector. Each value is sealed with its own
/// AES-256-GCM data key wrapped by the Key Vault key; the version comes from Key Vault. Any failure
/// becomes <see cref="LocationPiiProtectionUnavailableException"/> (503, nothing written).
/// </summary>
public sealed class AzureKeyVaultLocationPiiProtector(IPiiEnvelopeProtector envelope) : ILocationPiiProtector
{
    public const string AddressTextPurpose = "locations.address_text";
    public const string ContactNamePurpose = "locations.contact_name";
    public const string PhonePurpose = "locations.phone";

    public async Task<ProtectedLocationPii> ProtectAsync(LocationPiiValues values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(values.AddressText);
        var inputs = new List<PiiPlaintext> { new(AddressTextPurpose, values.AddressText) };
        var hasContactName = !string.IsNullOrWhiteSpace(values.ContactName);
        var hasPhone = !string.IsNullOrWhiteSpace(values.Phone);
        if (hasContactName)
        {
            inputs.Add(new PiiPlaintext(ContactNamePurpose, values.ContactName!));
        }

        if (hasPhone)
        {
            inputs.Add(new PiiPlaintext(PhonePurpose, values.Phone!));
        }

        PiiProtectedBatch batch;
        try
        {
            batch = await envelope.ProtectAsync(inputs, cancellationToken).ConfigureAwait(false);
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

        var index = 1;
        return new ProtectedLocationPii(
            batch.KeyVersion,
            batch.Ciphertexts[0],
            hasContactName ? batch.Ciphertexts[index++] : null,
            hasPhone ? batch.Ciphertexts[index] : null);
    }
}
