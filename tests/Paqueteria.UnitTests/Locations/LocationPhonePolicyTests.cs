using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;

namespace Paqueteria.UnitTests.Locations;

/// <summary>
/// ORD-PHONE-PLUS52-LOCATIONS-2026-10-03: the optional saved-location phone of createLocation (GEO-001) follows the
/// same 10-digit Mexican rule as the quote phone, and only the normalized digits reach the PII protector.
/// </summary>
public sealed class LocationPhonePolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("667123456")]
    [InlineData("66712345678")]
    [InlineData("526671234567")]
    [InlineData("+1 667 123 4567")]
    [InlineData("+52 1 667 123 4567")]
    [InlineData("(667) 123 4567")]
    [InlineData("667.123.4567")]
    [InlineData("667123456a")]
    public async Task An_invalid_phone_is_rejected_before_geocoding_or_protection(string phone)
    {
        var geocoding = new CountingGeocodingProvider();
        var protector = new CapturingPiiProtector();
        var service = new PostgreSqlLocationService(null!, geocoding, protector, null!, null!);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Command(phone), CancellationToken.None));

        Assert.Equal(0, geocoding.Calls);
        Assert.Null(protector.Captured);
    }

    [Theory]
    [InlineData("6671234567")]
    [InlineData("667 123 4567")]
    [InlineData("667-123-4567")]
    [InlineData("+526671234567")]
    [InlineData("+52 667 123 4567")]
    [InlineData("+52-6671234567")]
    public async Task A_valid_phone_reaches_the_protector_as_the_ten_national_digits(string phone)
    {
        var protector = new CapturingPiiProtector();
        var service = new PostgreSqlLocationService(null!, new CountingGeocodingProvider(), protector, null!, null!);

        await Assert.ThrowsAsync<LocationPiiProtectionUnavailableException>(() =>
            service.CreateAsync(Command(phone), CancellationToken.None));

        Assert.NotNull(protector.Captured);
        Assert.Equal("6671234567", protector.Captured.Phone);
    }

    [Fact]
    public async Task A_location_without_phone_stays_valid()
    {
        var protector = new CapturingPiiProtector();
        var service = new PostgreSqlLocationService(null!, new CountingGeocodingProvider(), protector, null!, null!);

        await Assert.ThrowsAsync<LocationPiiProtectionUnavailableException>(() =>
            service.CreateAsync(Command(null), CancellationToken.None));

        Assert.NotNull(protector.Captured);
        Assert.Null(protector.Captured.Phone);
    }

    private static CreateLocationCommand Command(string? phone) => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "synthetic-location-key-0001",
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        null,
        null,
        "Synthetic private address",
        "Synthetic summary",
        "Synthetic contact",
        phone,
        24.8,
        -107.4,
        "request-1");

    private sealed class CountingGeocodingProvider : IGeocodingProvider
    {
        private readonly ManualGeocodingProvider inner = new();

        public int Calls { get; private set; }

        public Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.GeocodeAsync(request, cancellationToken);
        }
    }

    /// <summary>Records what it was asked to protect, then fails closed so nothing else runs.</summary>
    private sealed class CapturingPiiProtector : ILocationPiiProtector
    {
        public LocationPiiValues? Captured { get; private set; }

        public Task<ProtectedLocationPii> ProtectAsync(LocationPiiValues values, CancellationToken cancellationToken)
        {
            Captured = values;
            throw new LocationPiiProtectionUnavailableException();
        }
    }
}
