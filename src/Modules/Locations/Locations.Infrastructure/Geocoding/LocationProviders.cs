using System.Security.Cryptography;
using System.Text;
using Locations.Application.Geocoding;
using Locations.Application.Locations;

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
    public byte[] Protect(string plaintext, string keyVersion) => throw new LocationPiiProtectionUnavailableException();
}

public sealed class DeterministicMockLocationPiiProtector : ILocationPiiProtector
{
    public byte[] Protect(string plaintext, string keyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        return SHA256.HashData(Encoding.UTF8.GetBytes($"GEO-001-MOCK\0{keyVersion}\0{plaintext}"));
    }
}
