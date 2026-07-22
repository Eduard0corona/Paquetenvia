using System.Text.Json.Serialization;
using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Locations.Endpoints;

public static class LocationEndpoints
{
    public static IEndpointRouteBuilder MapLocationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/cities", ListCitiesAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("listCities")
            .WithTags("Locations")
            .Produces<IReadOnlyList<CityResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/v1/service-areas", ListServiceAreasAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("listServiceAreas")
            .WithTags("Locations")
            .Produces<IReadOnlyList<ServiceAreaResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/v1/operating-zones", ListOperatingZonesAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("listOperatingZones")
            .WithTags("Locations")
            .Produces<IReadOnlyList<OperatingZoneResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapGet("/api/v1/locations", ListLocationsAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("listLocations")
            .WithTags("Locations")
            .Produces<IReadOnlyList<LocationResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost("/api/v1/locations", CreateLocationAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext()
            .WithName("createLocation")
            .WithTags("Locations")
            .Accepts<CreateLocationRequest>("application/json")
            .Produces<LocationResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListCitiesAsync(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ILocationService service,
        CancellationToken cancellationToken) => await ExecuteAsync(
            session,
            tenantContext,
            async (actorId, organizationId) => Results.Ok(
                (await service.ListCitiesAsync(actorId, organizationId, cancellationToken)).Select(ToResponse)),
            cancellationToken);

    private static async Task<IResult> ListServiceAreasAsync(
        Guid city_id,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ILocationService service,
        CancellationToken cancellationToken)
    {
        if (city_id == Guid.Empty)
        {
            return BadRequest();
        }

        return await ExecuteAsync(
            session,
            tenantContext,
            async (actorId, organizationId) => Results.Ok(
                (await service.ListServiceAreasAsync(actorId, organizationId, city_id, cancellationToken)).Select(ToResponse)),
            cancellationToken);
    }

    private static async Task<IResult> ListOperatingZonesAsync(
        Guid service_area_id,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ILocationService service,
        CancellationToken cancellationToken)
    {
        if (service_area_id == Guid.Empty)
        {
            return BadRequest();
        }

        return await ExecuteAsync(
            session,
            tenantContext,
            async (actorId, organizationId) => Results.Ok(
                (await service.ListOperatingZonesAsync(actorId, organizationId, service_area_id, cancellationToken)).Select(ToResponse)),
            cancellationToken);
    }

    private static async Task<IResult> ListLocationsAsync(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ILocationService service,
        CancellationToken cancellationToken) => await ExecuteAsync(
            session,
            tenantContext,
            async (actorId, organizationId) => Results.Ok(
                (await service.ListLocationsAsync(actorId, organizationId, cancellationToken)).Select(ToResponse)),
            cancellationToken);

    private static async Task<IResult> CreateLocationAsync(
        HttpContext httpContext,
        CreateLocationRequest request,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        ILocationService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) || !IsValid(request))
        {
            return BadRequest();
        }

        return await ExecuteAsync(
            session,
            tenantContext,
            async (actorId, organizationId) =>
            {
                var result = await service.CreateAsync(
                    new CreateLocationCommand(
                        actorId,
                        organizationId,
                        idempotencyKey,
                        request.CityId,
                        request.ServiceAreaId,
                        request.OperatingZoneId,
                        request.AddressText,
                        request.AddressSummary,
                        request.ContactName,
                        request.Phone,
                        request.Lat,
                        request.Lng,
                        request.PiiKeyVersion,
                        httpContext.TraceIdentifier),
                    cancellationToken);
                return result.Status switch
                {
                    ServiceabilityStatus.Serviceable when result.Location is not null =>
                        Results.Created($"/api/v1/locations/{result.Location.Id:D}", ToResponse(result.Location)),
                    ServiceabilityStatus.InvalidCity or
                    ServiceabilityStatus.InaccessibleServiceArea or
                    ServiceabilityStatus.InaccessibleOperatingZone => NotFound(),
                    _ => Forbidden(),
                };
            },
            cancellationToken);
    }

    private static async Task<IResult> ExecuteAsync(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        Func<Guid, Guid, Task<IResult>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        try
        {
            return await operation(actorId, tenantContext.OrganizationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return BadRequest();
        }
        catch (LocationResourceNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (exception is LocationServiceUnavailableException or LocationPiiProtectionUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
        }
    }

    private static bool TryReadIdempotencyKey(HttpRequest request, out string value)
    {
        value = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return value.Length <= 200 && value == value.Trim();
    }

    private static bool IsValid(CreateLocationRequest request) =>
        request.CityId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(request.AddressText) && request.AddressText.Trim().Length >= 8 &&
        !string.IsNullOrWhiteSpace(request.AddressSummary) && request.AddressSummary.Length <= 180 &&
        !string.IsNullOrWhiteSpace(request.PiiKeyVersion) &&
        request.Lat is >= -90 and <= 90 && request.Lng is >= -180 and <= 180 &&
        !double.IsNaN(request.Lat) && !double.IsNaN(request.Lng) &&
        !double.IsInfinity(request.Lat) && !double.IsInfinity(request.Lng);

    private static CityResponse ToResponse(CityResult result) =>
        new(result.Id, result.Name, result.StateCode, result.CountryCode, result.Timezone);

    private static ServiceAreaResponse ToResponse(ServiceAreaResult result) =>
        new(result.Id, result.CityId, result.Name, result.Status);

    private static OperatingZoneResponse ToResponse(OperatingZoneResult result) =>
        new(result.Id, result.ServiceAreaId, result.Name, result.ZoneType, result.Status);

    private static LocationResponse ToResponse(LocationResult result) =>
        new(result.Id, result.CityId, result.ServiceAreaId, result.OperatingZoneId, result.AddressSummary, result.Lat, result.Lng);

    private static IResult BadRequest() => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.");
    private static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");
    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");
}

public sealed record CreateLocationRequest(
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("operating_zone_id")] Guid? OperatingZoneId,
    [property: JsonPropertyName("address_text")] string AddressText,
    [property: JsonPropertyName("address_summary")] string AddressSummary,
    [property: JsonPropertyName("contact_name")] string? ContactName,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lng")] double Lng,
    [property: JsonPropertyName("pii_key_version")] string PiiKeyVersion);

public sealed record CityResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state_code")] string StateCode,
    [property: JsonPropertyName("country_code")] string CountryCode,
    [property: JsonPropertyName("timezone")] string Timezone);

public sealed record ServiceAreaResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status);

public sealed record OperatingZoneResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("service_area_id")] Guid ServiceAreaId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("zone_type")] string ZoneType,
    [property: JsonPropertyName("status")] string Status);

public sealed record LocationResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("operating_zone_id")] Guid? OperatingZoneId,
    [property: JsonPropertyName("address_summary")] string AddressSummary,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lng")] double Lng);
