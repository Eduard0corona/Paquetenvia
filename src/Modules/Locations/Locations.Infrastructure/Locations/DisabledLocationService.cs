using Locations.Application.Locations;

namespace Locations.Infrastructure.Locations;

public sealed class DisabledLocationService : ILocationService, IServiceabilityEvaluator
{
    public Task<IReadOnlyList<CityResult>> ListCitiesAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) => Fail<IReadOnlyList<CityResult>>();
    public Task<IReadOnlyList<ServiceAreaResult>> ListServiceAreasAsync(Guid actorId, Guid organizationId, Guid cityId, CancellationToken cancellationToken) => Fail<IReadOnlyList<ServiceAreaResult>>();
    public Task<IReadOnlyList<OperatingZoneResult>> ListOperatingZonesAsync(Guid actorId, Guid organizationId, Guid serviceAreaId, CancellationToken cancellationToken) => Fail<IReadOnlyList<OperatingZoneResult>>();
    public Task<IReadOnlyList<LocationResult>> ListLocationsAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) => Fail<IReadOnlyList<LocationResult>>();
    public Task<CreateLocationResult> CreateAsync(CreateLocationCommand command, CancellationToken cancellationToken) => Fail<CreateLocationResult>();
    public Task<ServiceabilityResult> EvaluateAsync(EvaluateServiceabilityCommand command, CancellationToken cancellationToken) => Fail<ServiceabilityResult>();

    private static Task<T> Fail<T>() => Task.FromException<T>(new LocationServiceUnavailableException("Locations are disabled."));
}
