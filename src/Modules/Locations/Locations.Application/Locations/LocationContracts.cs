namespace Locations.Application.Locations;

public sealed record CityResult(Guid Id, string Name, string StateCode, string CountryCode, string Timezone);

public sealed record ServiceAreaResult(Guid Id, Guid CityId, string Name, string Status);

public sealed record OperatingZoneResult(Guid Id, Guid ServiceAreaId, string Name, string ZoneType, string Status);

public sealed record LocationResult(
    Guid Id,
    Guid CityId,
    Guid? ServiceAreaId,
    Guid? OperatingZoneId,
    string AddressSummary,
    double Lat,
    double Lng);

public sealed record CreateLocationCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    Guid CityId,
    Guid? ServiceAreaId,
    Guid? OperatingZoneId,
    string AddressText,
    string AddressSummary,
    string? ContactName,
    string? Phone,
    double Lat,
    double Lng,
    string PiiKeyVersion,
    string? RequestId);

public enum ServiceabilityStatus
{
    Serviceable,
    OutsideServiceArea,
    ExcludedZone,
    InvalidCity,
    InaccessibleServiceArea,
    InaccessibleOperatingZone,
}

public static class ServiceabilityStatusExtensions
{
    public static string ToContractValue(this ServiceabilityStatus value) => value switch
    {
        ServiceabilityStatus.Serviceable => "SERVICEABLE",
        ServiceabilityStatus.OutsideServiceArea => "OUTSIDE_SERVICE_AREA",
        ServiceabilityStatus.ExcludedZone => "EXCLUDED_ZONE",
        ServiceabilityStatus.InvalidCity => "INVALID_CITY",
        ServiceabilityStatus.InaccessibleServiceArea => "INACCESSIBLE_SERVICE_AREA",
        ServiceabilityStatus.InaccessibleOperatingZone => "INACCESSIBLE_OPERATING_ZONE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

public sealed record CreateLocationResult(ServiceabilityStatus Status, LocationResult? Location);

public sealed record EvaluateServiceabilityCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid CityId,
    Guid? ServiceAreaId,
    Guid? OperatingZoneId,
    double Lat,
    double Lng);

public sealed record ServiceabilityResult(
    ServiceabilityStatus Status,
    Guid? ServiceAreaId,
    Guid? OperatingZoneId);

public interface IServiceabilityEvaluator
{
    Task<ServiceabilityResult> EvaluateAsync(
        EvaluateServiceabilityCommand command,
        CancellationToken cancellationToken);
}

public interface ILocationService
{
    Task<IReadOnlyList<CityResult>> ListCitiesAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceAreaResult>> ListServiceAreasAsync(Guid actorId, Guid organizationId, Guid cityId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OperatingZoneResult>> ListOperatingZonesAsync(Guid actorId, Guid organizationId, Guid serviceAreaId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LocationResult>> ListLocationsAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken);
    Task<CreateLocationResult> CreateAsync(CreateLocationCommand command, CancellationToken cancellationToken);
}

public sealed class LocationServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LocationResourceNotFoundException()
    : Exception("The geographic resource was not found.");
