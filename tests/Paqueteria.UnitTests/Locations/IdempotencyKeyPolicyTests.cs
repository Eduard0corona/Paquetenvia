using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using Locations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Application.Idempotency;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.UnitTests.Locations;

public sealed class IdempotencyKeyPolicyTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(129)]
    public async Task Internal_command_rejects_invalid_key_before_geocoding_or_database_access(int length)
    {
        var geocoder = new ObservingGeocodingProvider();
        var state = new TenantDatabaseExecutionState();
        await using var context = CreateContext(state);
        var service = CreateService(context, state, geocoder);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(CreateCommand(new string('a', length)), default));

        Assert.Equal(0, geocoder.CallCount);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(128)]
    public async Task Internal_command_accepts_valid_key_boundaries_and_reaches_geocoding(int length)
    {
        var geocoder = new ObservingGeocodingProvider();
        var state = new TenantDatabaseExecutionState();
        await using var context = CreateContext(state);
        var service = CreateService(context, state, geocoder);

        var exception = await Assert.ThrowsAsync<LocationServiceUnavailableException>(() =>
            service.CreateAsync(CreateCommand(new string('a', length)), default));

        Assert.Equal("Validation passed.", exception.Message);
        Assert.Equal(1, geocoder.CallCount);
    }

    private static LocationsDbContext CreateContext(TenantDatabaseExecutionState state)
    {
        var options = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=geo001-validation-not-used;Username=not-used;Password=not-used",
                postgres => postgres.UseNetTopologySuite())
            .Options;
        return new LocationsDbContext(options, state);
    }

    private static PostgreSqlLocationService CreateService(
        LocationsDbContext context,
        TenantDatabaseExecutionState state,
        IGeocodingProvider geocoder) => new(
            new TenantTransactionContext<LocationsDbContext>(context, state),
            geocoder,
            new DisabledLocationPiiProtector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new SystemClock());

    private static CreateLocationCommand CreateCommand(string idempotencyKey) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        idempotencyKey,
        Guid.NewGuid(),
        null,
        null,
        "Synthetic private address",
        "Synthetic summary",
        null,
        null,
        28.61,
        -106.09,
        "mock-v1",
        "geo001-def-001");

    private sealed class ObservingGeocodingProvider : IGeocodingProvider
    {
        public int CallCount { get; private set; }

        public Task<GeocodingResult> GeocodeAsync(GeocodingRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new LocationServiceUnavailableException("Validation passed.");
        }
    }
}
