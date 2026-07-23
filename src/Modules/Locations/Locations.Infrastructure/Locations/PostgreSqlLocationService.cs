using System.Security.Cryptography;
using System.Text;
using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Domain;
using Locations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using DomainLocation = Locations.Domain.Location;

namespace Locations.Infrastructure.Locations;

public sealed class PostgreSqlLocationService(
    TenantTransactionContext<LocationsDbContext> transactionContext,
    IGeocodingProvider geocodingProvider,
    ILocationPiiProtector piiProtector,
    IAppendOnlyAuditWriter auditWriter,
    IClock clock) : ILocationService, IServiceabilityEvaluator, IQuoteLocationResolver
{
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public Task<IReadOnlyList<CityResult>> ListCitiesAsync(
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken) => ExecuteAsync<IReadOnlyList<CityResult>>(
            actorId,
            organizationId,
            async (dbContext, token) => await dbContext.Cities
                .AsNoTracking()
                .Where(city => city.Status == GeographicStatus.Active)
                .OrderBy(city => city.CountryCode)
                .ThenBy(city => city.StateCode)
                .ThenBy(city => city.Name)
                .Select(city => new CityResult(city.Id, city.Name, city.StateCode, city.CountryCode, city.Timezone))
                .ToArrayAsync(token),
            cancellationToken);

    public Task<IReadOnlyList<ServiceAreaResult>> ListServiceAreasAsync(
        Guid actorId,
        Guid organizationId,
        Guid cityId,
        CancellationToken cancellationToken) => ExecuteAsync<IReadOnlyList<ServiceAreaResult>>(
            actorId,
            organizationId,
            async (dbContext, token) =>
            {
                if (!await dbContext.Cities.AsNoTracking().AnyAsync(city => city.Id == cityId, token))
                {
                    throw new LocationResourceNotFoundException();
                }

                return await dbContext.ServiceAreas
                    .AsNoTracking()
                    .Where(area => area.CityId == cityId)
                    .OrderBy(area => area.Name)
                    .Select(area => new ServiceAreaResult(area.Id, area.CityId, area.Name, area.Status.ToContractValue()))
                    .ToArrayAsync(token);
            },
            cancellationToken);

    public Task<IReadOnlyList<OperatingZoneResult>> ListOperatingZonesAsync(
        Guid actorId,
        Guid organizationId,
        Guid serviceAreaId,
        CancellationToken cancellationToken) => ExecuteAsync<IReadOnlyList<OperatingZoneResult>>(
            actorId,
            organizationId,
            async (dbContext, token) =>
            {
                if (!await dbContext.ServiceAreas.AsNoTracking().AnyAsync(area => area.Id == serviceAreaId, token))
                {
                    throw new LocationResourceNotFoundException();
                }

                return await dbContext.OperatingZones
                    .AsNoTracking()
                    .Where(zone => zone.ServiceAreaId == serviceAreaId)
                    .OrderBy(zone => zone.Name)
                    .Select(zone => new OperatingZoneResult(
                        zone.Id,
                        zone.ServiceAreaId,
                        zone.Name,
                        zone.ZoneType.ToContractValue(),
                        zone.Status.ToContractValue()))
                    .ToArrayAsync(token);
            },
            cancellationToken);

    public Task<IReadOnlyList<LocationResult>> ListLocationsAsync(
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken) => ExecuteAsync<IReadOnlyList<LocationResult>>(
            actorId,
            organizationId,
            async (dbContext, token) => await dbContext.Locations
                .AsNoTracking()
                .OrderBy(location => location.CreatedAt)
                .ThenBy(location => location.Id)
                .Select(location => new LocationResult(
                    location.Id,
                    location.CityId,
                    location.ServiceAreaId,
                    location.OperatingZoneId,
                    location.AddressSummary,
                    location.Point.Y,
                    location.Point.X))
                .ToArrayAsync(token),
            cancellationToken);

    public Task<ServiceabilityResult> EvaluateAsync(
        EvaluateServiceabilityCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCoordinates(command.ActorId, command.OrganizationId, command.CityId, command.Lat, command.Lng);
        var point = GeometryFactory.CreatePoint(new Coordinate(command.Lng, command.Lat));
        return ExecuteAsync(
            command.ActorId,
            command.OrganizationId,
            (dbContext, token) => EvaluateWithinTransactionAsync(
                dbContext,
                command.CityId,
                command.ServiceAreaId,
                command.OperatingZoneId,
                point,
                token),
            cancellationToken);
    }

    public async Task<ResolveQuoteLocationResult> ResolveAsync(
        ResolveQuoteLocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateQuoteLocation(command);

        var safeSummary = command.Role == QuoteLocationRole.Origin
            ? "Synthetic quote origin"
            : "Synthetic quote destination";
        GeocodingResult geocoded;
        try
        {
            geocoded = await geocodingProvider.GeocodeAsync(
                new GeocodingRequest(command.AddressText, safeSummary, command.Lat, command.Lng),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LocationServiceUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocationServiceUnavailableException("Quote location resolution failed safely.", exception);
        }

        var point = GeometryFactory.CreatePoint(new Coordinate(geocoded.Longitude, geocoded.Latitude));
        var locationId = CreateIdempotentId(command.OrganizationId, command.IdempotencySubkey);

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({command.OrganizationId:D} || ':' || {command.IdempotencySubkey}, 0));",
                        token);

                    var existing = await dbContext.Locations.AsNoTracking()
                        .SingleOrDefaultAsync(location => location.Id == locationId, token);
                    if (existing is not null)
                    {
                        return new ResolveQuoteLocationResult(
                            QuoteLocationResolutionStatus.Resolved,
                            new QuoteLocationResult(existing.Id, existing.CityId, existing.ServiceAreaId!.Value, existing.OperatingZoneId));
                    }

                    var areas = await dbContext.ServiceAreas.AsNoTracking()
                        .Where(area => area.Status == GeographicStatus.Active && area.Polygon.Covers(point))
                        .OrderBy(area => area.Id)
                        .Select(area => new { area.Id, area.CityId })
                        .ToArrayAsync(token);
                    if (areas.Length == 0)
                    {
                        return new ResolveQuoteLocationResult(QuoteLocationResolutionStatus.OutsideCoverage, null);
                    }

                    if (areas.Length != 1 || !await dbContext.Cities.AsNoTracking()
                            .AnyAsync(city => city.Id == areas[0].CityId && city.Status == GeographicStatus.Active, token))
                    {
                        return new ResolveQuoteLocationResult(QuoteLocationResolutionStatus.AmbiguousCoverage, null);
                    }

                    var area = areas[0];
                    var zones = await dbContext.OperatingZones.AsNoTracking()
                        .Where(zone => zone.ServiceAreaId == area.Id &&
                            zone.Status == GeographicStatus.Active &&
                            zone.Polygon.Covers(point))
                        .OrderBy(zone => zone.Id)
                        .Select(zone => new { zone.Id, zone.ZoneType })
                        .ToArrayAsync(token);
                    if (zones.Any(zone => zone.ZoneType == OperatingZoneType.Excluded))
                    {
                        return new ResolveQuoteLocationResult(QuoteLocationResolutionStatus.ExcludedZone, null);
                    }

                    var applicableZones = zones.Where(zone => zone.ZoneType != OperatingZoneType.Excluded).ToArray();
                    if (applicableZones.Length > 1)
                    {
                        return new ResolveQuoteLocationResult(QuoteLocationResolutionStatus.AmbiguousCoverage, null);
                    }

                    const string keyVersion = "PRC-001-SYNTHETIC-V1";
                    var protectedAddress = string.IsNullOrWhiteSpace(command.References)
                        ? command.AddressText
                        : $"{command.AddressText}\nReferences: {command.References}";
                    var entity = new DomainLocation(
                        locationId,
                        command.OrganizationId,
                        area.CityId,
                        area.Id,
                        applicableZones.FirstOrDefault()?.Id,
                        point,
                        piiProtector.Protect(protectedAddress, keyVersion),
                        safeSummary,
                        piiProtector.Protect(command.ContactName, keyVersion),
                        piiProtector.Protect(command.Phone, keyVersion),
                        keyVersion,
                        clock.UtcNow);

                    dbContext.Locations.Add(entity);
                    await dbContext.SaveChangesAsync(token);

                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    await auditWriter.WriteAsync(
                        connection,
                        transaction,
                        new AuditEntry(
                            Guid.NewGuid(),
                            command.OrganizationId,
                            command.ActorId,
                            "LOCATION_CREATED",
                            "LOCATION",
                            locationId,
                            command.RequestId,
                            RedactedAuditPayload.Empty,
                            clock.UtcNow),
                        token);

                    return new ResolveQuoteLocationResult(
                        QuoteLocationResolutionStatus.Resolved,
                        new QuoteLocationResult(entity.Id, entity.CityId, entity.ServiceAreaId!.Value, entity.OperatingZoneId));
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LocationPiiProtectionUnavailableException)
        {
            throw;
        }
        catch (LocationServiceUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocationServiceUnavailableException("Quote location resolution failed safely.", exception);
        }
    }

    public async Task<CreateLocationResult> CreateAsync(
        CreateLocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);

        GeocodingResult geocoded;
        try
        {
            geocoded = await geocodingProvider.GeocodeAsync(
                new GeocodingRequest(command.AddressText, command.AddressSummary, command.Lat, command.Lng),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LocationServiceUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocationServiceUnavailableException("Geocoding failed safely.", exception);
        }

        var point = GeometryFactory.CreatePoint(new Coordinate(geocoded.Longitude, geocoded.Latitude));
        var locationId = CreateIdempotentId(command.OrganizationId, command.IdempotencyKey);

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({command.OrganizationId:D} || ':' || {command.IdempotencyKey}, 0));",
                        token);
                    var existing = await dbContext.Locations.AsNoTracking()
                        .SingleOrDefaultAsync(location => location.Id == locationId, token);
                    if (existing is not null)
                    {
                        return new CreateLocationResult(ServiceabilityStatus.Serviceable, ToResult(existing));
                    }

                    var serviceability = await EvaluateWithinTransactionAsync(
                        dbContext,
                        command.CityId,
                        command.ServiceAreaId,
                        command.OperatingZoneId,
                        point,
                        token);
                    if (serviceability.Status != ServiceabilityStatus.Serviceable || serviceability.ServiceAreaId is null)
                    {
                        return new CreateLocationResult(serviceability.Status, null);
                    }

                    var entity = new DomainLocation(
                        locationId,
                        command.OrganizationId,
                        command.CityId,
                        serviceability.ServiceAreaId,
                        serviceability.OperatingZoneId,
                        point,
                        piiProtector.Protect(command.AddressText, command.PiiKeyVersion),
                        geocoded.AddressSummary,
                        ProtectOptional(command.ContactName, command.PiiKeyVersion),
                        ProtectOptional(command.Phone, command.PiiKeyVersion),
                        command.PiiKeyVersion,
                        clock.UtcNow);

                    dbContext.Locations.Add(entity);
                    await dbContext.SaveChangesAsync(token);

                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    await auditWriter.WriteAsync(
                        connection,
                        transaction,
                        new AuditEntry(
                            Guid.NewGuid(),
                            command.OrganizationId,
                            command.ActorId,
                            "LOCATION_CREATED",
                            "LOCATION",
                            locationId,
                            command.RequestId,
                            RedactedAuditPayload.Empty,
                            clock.UtcNow),
                        token);

                    return new CreateLocationResult(ServiceabilityStatus.Serviceable, ToResult(entity));
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LocationPiiProtectionUnavailableException)
        {
            throw;
        }
        catch (LocationServiceUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocationServiceUnavailableException("The location operation failed safely.", exception);
        }
    }

    private static async Task<ServiceabilityResult> EvaluateWithinTransactionAsync(
        LocationsDbContext dbContext,
        Guid cityId,
        Guid? serviceAreaId,
        Guid? operatingZoneId,
        Point point,
        CancellationToken cancellationToken)
    {
        var cityExists = await dbContext.Cities.AsNoTracking()
            .AnyAsync(city => city.Id == cityId && city.Status == GeographicStatus.Active, cancellationToken);
        if (!cityExists)
        {
            return new ServiceabilityResult(ServiceabilityStatus.InvalidCity, null, null);
        }

        var areaResolution = await ResolveServiceAreaAsync(dbContext, cityId, serviceAreaId, point, cancellationToken);
        if (areaResolution.Status != ServiceabilityStatus.Serviceable || areaResolution.AreaId is null)
        {
            return new ServiceabilityResult(areaResolution.Status, null, null);
        }

        var zoneResolution = await ResolveOperatingZoneAsync(
            dbContext,
            areaResolution.AreaId.Value,
            operatingZoneId,
            point,
            cancellationToken);
        return new ServiceabilityResult(zoneResolution.Status, areaResolution.AreaId, zoneResolution.ZoneId);
    }

    private async Task<T> ExecuteAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<LocationsDbContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new ArgumentException("Actor and organization identifiers are required.");
        }

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                action,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new LocationServiceUnavailableException("The location query failed safely.", exception);
        }
    }

    private static async Task<(ServiceabilityStatus Status, Guid? AreaId)> ResolveServiceAreaAsync(
        LocationsDbContext dbContext,
        Guid cityId,
        Guid? serviceAreaId,
        Point point,
        CancellationToken cancellationToken)
    {
        if (serviceAreaId is { } requestedAreaId)
        {
            var exists = await dbContext.ServiceAreas.AsNoTracking().AnyAsync(
                area => area.Id == requestedAreaId && area.CityId == cityId && area.Status == GeographicStatus.Active,
                cancellationToken);
            if (!exists)
            {
                return (ServiceabilityStatus.InaccessibleServiceArea, null);
            }

            var covers = await dbContext.ServiceAreas.AsNoTracking().AnyAsync(
                area => area.Id == requestedAreaId && area.Polygon.Covers(point),
                cancellationToken);
            return covers
                ? (ServiceabilityStatus.Serviceable, requestedAreaId)
                : (ServiceabilityStatus.OutsideServiceArea, null);
        }

        var matching = await dbContext.ServiceAreas.AsNoTracking()
            .Where(area => area.CityId == cityId &&
                area.Status == GeographicStatus.Active &&
                area.Polygon.Covers(point))
            .OrderBy(area => area.Id)
            .Select(area => area.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return matching == Guid.Empty
            ? (ServiceabilityStatus.OutsideServiceArea, null)
            : (ServiceabilityStatus.Serviceable, matching);
    }

    private static async Task<(ServiceabilityStatus Status, Guid? ZoneId)> ResolveOperatingZoneAsync(
        LocationsDbContext dbContext,
        Guid serviceAreaId,
        Guid? operatingZoneId,
        Point point,
        CancellationToken cancellationToken)
    {
        if (operatingZoneId is { } requestedZoneId)
        {
            var requestedType = await dbContext.OperatingZones.AsNoTracking()
                .Where(zone => zone.Id == requestedZoneId &&
                    zone.ServiceAreaId == serviceAreaId &&
                    zone.Status == GeographicStatus.Active)
                .Select(zone => (OperatingZoneType?)zone.ZoneType)
                .SingleOrDefaultAsync(cancellationToken);
            if (requestedType is null)
            {
                return (ServiceabilityStatus.InaccessibleOperatingZone, null);
            }

            var covers = await dbContext.OperatingZones.AsNoTracking().AnyAsync(
                zone => zone.Id == requestedZoneId && zone.Polygon.Covers(point),
                cancellationToken);
            if (!covers)
            {
                return (ServiceabilityStatus.OutsideServiceArea, null);
            }

            return requestedType == OperatingZoneType.Excluded
                ? (ServiceabilityStatus.ExcludedZone, null)
                : (ServiceabilityStatus.Serviceable, requestedZoneId);
        }

        var zones = await dbContext.OperatingZones.AsNoTracking()
            .Where(zone => zone.ServiceAreaId == serviceAreaId &&
                zone.Status == GeographicStatus.Active &&
                zone.Polygon.Covers(point))
            .OrderBy(zone => zone.ZoneType == OperatingZoneType.Excluded ? 0 : 1)
            .ThenBy(zone => zone.ZoneType)
            .ThenBy(zone => zone.Id)
            .Select(zone => new { zone.Id, zone.ZoneType })
            .ToArrayAsync(cancellationToken);
        if (zones.FirstOrDefault()?.ZoneType == OperatingZoneType.Excluded)
        {
            return (ServiceabilityStatus.ExcludedZone, null);
        }

        return (ServiceabilityStatus.Serviceable, zones.FirstOrDefault()?.Id);
    }

    private byte[]? ProtectOptional(string? value, string keyVersion) =>
        string.IsNullOrWhiteSpace(value) ? null : piiProtector.Protect(value, keyVersion);

    private static LocationResult ToResult(DomainLocation location) => new(
        location.Id,
        location.CityId,
        location.ServiceAreaId,
        location.OperatingZoneId,
        location.AddressSummary,
        location.Point.Y,
        location.Point.X);

    private static Guid CreateIdempotentId(Guid organizationId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"GEO-001\0{organizationId:D}\0{idempotencyKey}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void Validate(CreateLocationCommand command)
    {
        if (command.ActorId == Guid.Empty || command.OrganizationId == Guid.Empty || command.CityId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(command.AddressText) || command.AddressText.Trim().Length < 8 ||
            string.IsNullOrWhiteSpace(command.AddressSummary) || command.AddressSummary.Length > 180 ||
            string.IsNullOrWhiteSpace(command.PiiKeyVersion) ||
            command.Lat is < -90 or > 90 || command.Lng is < -180 or > 180 ||
            double.IsNaN(command.Lat) || double.IsNaN(command.Lng) ||
            double.IsInfinity(command.Lat) || double.IsInfinity(command.Lng))
        {
            throw new ArgumentException("The location command is invalid.", nameof(command));
        }
    }

    private static void ValidateCoordinates(Guid actorId, Guid organizationId, Guid cityId, double latitude, double longitude)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty || cityId == Guid.Empty ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
            double.IsNaN(latitude) || double.IsNaN(longitude) ||
            double.IsInfinity(latitude) || double.IsInfinity(longitude))
        {
            throw new ArgumentException("The serviceability request is invalid.");
        }
    }

    private static void ValidateQuoteLocation(ResolveQuoteLocationCommand command)
    {
        if (command.ActorId == Guid.Empty || command.OrganizationId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencySubkey) ||
            !Enum.IsDefined(command.Role) ||
            string.IsNullOrWhiteSpace(command.AddressText) || command.AddressText.Trim().Length < 8 ||
            string.IsNullOrWhiteSpace(command.ContactName) ||
            string.IsNullOrWhiteSpace(command.Phone) ||
            command.References?.Length > 500 ||
            command.Lat is < -90 or > 90 || command.Lng is < -180 or > 180 ||
            double.IsNaN(command.Lat) || double.IsNaN(command.Lng) ||
            double.IsInfinity(command.Lat) || double.IsInfinity(command.Lng))
        {
            throw new ArgumentException("The quote location command is invalid.", nameof(command));
        }
    }
}
