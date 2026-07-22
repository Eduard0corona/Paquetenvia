using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Domain;
using Locations.Infrastructure.Geocoding;
using NetTopologySuite.Geometries;

namespace Paqueteria.UnitTests.Locations;

public sealed class LocationDomainTests
{
    [Theory]
    [InlineData(GeographicStatus.Active, "ACTIVE")]
    [InlineData(GeographicStatus.Inactive, "INACTIVE")]
    public void Geographic_status_uses_normative_values(GeographicStatus status, string expected) =>
        Assert.Equal(expected, status.ToContractValue());

    [Theory]
    [InlineData(OperatingZoneType.Core, "CORE")]
    [InlineData(OperatingZoneType.Standard, "STANDARD")]
    [InlineData(OperatingZoneType.Extended, "EXTENDED")]
    [InlineData(OperatingZoneType.Excluded, "EXCLUDED")]
    public void Zone_type_uses_normative_values(OperatingZoneType zoneType, string expected) =>
        Assert.Equal(expected, zoneType.ToContractValue());

    [Theory]
    [InlineData(ServiceabilityStatus.Serviceable, "SERVICEABLE")]
    [InlineData(ServiceabilityStatus.OutsideServiceArea, "OUTSIDE_SERVICE_AREA")]
    [InlineData(ServiceabilityStatus.ExcludedZone, "EXCLUDED_ZONE")]
    [InlineData(ServiceabilityStatus.InvalidCity, "INVALID_CITY")]
    [InlineData(ServiceabilityStatus.InaccessibleServiceArea, "INACCESSIBLE_SERVICE_AREA")]
    [InlineData(ServiceabilityStatus.InaccessibleOperatingZone, "INACCESSIBLE_OPERATING_ZONE")]
    public void Serviceability_status_uses_structured_values(ServiceabilityStatus status, string expected) =>
        Assert.Equal(expected, status.ToContractValue());

    [Fact]
    public async Task Manual_geocoder_preserves_coordinates_and_normalizes_non_sensitive_summary()
    {
        var result = await new ManualGeocodingProvider().GeocodeAsync(
            new GeocodingRequest("Sensitive full address", "  Centro   Chihuahua  ", 28.632996, -106.069100),
            default);

        Assert.Equal("Centro Chihuahua", result.AddressSummary);
        Assert.Equal(28.632996, result.Latitude);
        Assert.Equal(-106.069100, result.Longitude);
        Assert.Equal("MANUAL", result.ProviderMode);
        Assert.True(result.UsedManualCoordinates);
    }

    [Fact]
    public async Task Mock_geocoder_is_deterministic_and_never_needs_network()
    {
        var provider = new DeterministicMockGeocodingProvider();
        var request = new GeocodingRequest("Sensitive full address", "Centro", 28.63299649, -106.06910049);

        Assert.Equal(
            await provider.GeocodeAsync(request, default),
            await provider.GeocodeAsync(request, default));
    }

    [Fact]
    public async Task Geographic_adapters_honor_cancellation_and_reject_invalid_coordinates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ManualGeocodingProvider().GeocodeAsync(
                new GeocodingRequest("Synthetic address", "Synthetic", 28.6, -106.1),
                cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DeterministicMockGeocodingProvider().GeocodeAsync(
                new GeocodingRequest("Synthetic address", "Synthetic", double.NaN, -106.1),
                default));
    }

    [Fact]
    public async Task Disabled_geocoder_fails_closed_without_disclosing_the_request()
    {
        const string sensitive = "Sensitive private address";
        var exception = await Assert.ThrowsAsync<LocationServiceUnavailableException>(() =>
            new DisabledGeocodingProvider().GeocodeAsync(
                new GeocodingRequest(sensitive, "Summary", 28.6, -106.1),
                default));
        Assert.DoesNotContain(sensitive, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Mock_pii_protection_is_deterministic_and_does_not_retain_plaintext()
    {
        var protector = new DeterministicMockLocationPiiProtector();
        const string plaintext = "Avenida Universidad 1234";

        var first = protector.Protect(plaintext, "mock-v1");
        var second = protector.Protect(plaintext, "mock-v1");

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.DoesNotContain(plaintext, Convert.ToHexString(first), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_pii_protection_fails_closed() =>
        Assert.Throws<LocationPiiProtectionUnavailableException>(() =>
            new DisabledLocationPiiProtector().Protect("Sensitive address", "v1"));

    [Fact]
    public void Location_enforces_srid_and_summary_length()
    {
        var invalidPoint = new Point(-106.1, 28.6) { SRID = 0 };
        Assert.Throws<ArgumentException>(() => new global::Locations.Domain.Location(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, invalidPoint,
            [1], new string('x', 181), null, null, "v1", DateTimeOffset.UtcNow));
    }
}
