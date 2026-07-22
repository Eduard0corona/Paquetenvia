using Locations.Application.Locations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Locations;

public sealed class LocationHttpWebApplicationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid CityId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid ServiceAreaId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    internal static readonly Guid ZoneId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    internal static readonly Guid ForeignResourceId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILocationService>();
            services.AddSingleton<ILocationService, StubLocationService>();
        });
    }

    private sealed class StubLocationService : ILocationService
    {
        public Task<IReadOnlyList<CityResult>> ListCitiesAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CityResult>>([new(CityId, "Synthetic City", "CHH", "MX", "America/Chihuahua")]);

        public Task<IReadOnlyList<ServiceAreaResult>> ListServiceAreasAsync(Guid actorId, Guid organizationId, Guid cityId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ServiceAreaResult>>([new(ServiceAreaId, CityId, "Synthetic Area", "ACTIVE")]);

        public Task<IReadOnlyList<OperatingZoneResult>> ListOperatingZonesAsync(Guid actorId, Guid organizationId, Guid serviceAreaId, CancellationToken cancellationToken)
        {
            if (serviceAreaId == ForeignResourceId)
            {
                throw new LocationResourceNotFoundException();
            }
            return Task.FromResult<IReadOnlyList<OperatingZoneResult>>([new(ZoneId, ServiceAreaId, "Synthetic Zone", "CORE", "ACTIVE")]);
        }

        public Task<IReadOnlyList<LocationResult>> ListLocationsAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocationResult>>([Location()]);

        public Task<CreateLocationResult> CreateAsync(CreateLocationCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(command.ServiceAreaId == ForeignResourceId
                ? new CreateLocationResult(ServiceabilityStatus.InaccessibleServiceArea, null)
                : new CreateLocationResult(ServiceabilityStatus.Serviceable, Location()));

        private static LocationResult Location() => new(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            CityId,
            ServiceAreaId,
            ZoneId,
            "Synthetic summary",
            28.61,
            -106.09);
    }
}
