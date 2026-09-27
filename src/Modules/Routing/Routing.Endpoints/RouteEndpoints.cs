using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Routing.Application.Routes;

namespace Routing.Endpoints;

public static class RouteEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapRouteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/routes", CreateAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("createRoute")
            .WithTags("Routing")
            .Accepts<CreateRouteRequest>("application/json")
            .Produces<RouteResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/routes", ListAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listRoutes")
            .WithTags("Routing")
            .Produces<RoutePageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/routes/{routeId}", GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getRoute")
            .WithTags("Routing")
            .Produces<RouteDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/routes/{routeId}/stops", AddStopAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("addRouteStop")
            .WithTags("Routing")
            .Accepts<AddRouteStopRequest>("application/json")
            .Produces<RouteDetailResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapDelete("/api/v1/routes/{routeId}/stops/{stopId}", RemoveStopAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("removeRouteStop")
            .WithTags("Routing")
            .Produces<RouteDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPut("/api/v1/routes/{routeId}/stops/order", ReorderStopsAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("reorderRouteStops")
            .WithTags("Routing")
            .Accepts<ReorderRouteStopsRequest>("application/json")
            .Produces<RouteDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRouteService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<CreateRouteRequest>(httpContext, cancellationToken);
        if (request is null || request.DriverId is not { } driverId || driverId == Guid.Empty ||
            request.CityId is not { } cityId || cityId == Guid.Empty ||
            request.ServiceAreaId == Guid.Empty || request.ExtensionData is { Count: > 0 } ||
            !TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !TrySession(session, tenantContext, out var actorId))
        {
            return Conflict("INVALID_REQUEST");
        }
        try
        {
            var result = await service.CreateAsync(
                new(
                    actorId,
                    tenantContext.OrganizationId,
                    key,
                    driverId,
                    cityId,
                    request.ServiceAreaId,
                    request.ScheduledFor,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Json(ToResponse(result), statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RoutingForbiddenException) { return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.CreateRoute); }
        catch (RoutingNotFoundException) { return NotFound(); }
        catch (RoutingConflictException exception) { return Conflict(PublicCode(exception.Code)); }
        catch (RoutingUnavailableException) { return Conflict("CONFLICT"); }
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRouteService service,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId) ||
            !TrySingle(httpContext.Request.Query, "status", out var status) ||
            !TryOptionalGuid(httpContext.Request.Query, "driver_id", out var driverId) ||
            !TryOptionalDate(httpContext.Request.Query, "scheduled_for", out var scheduledFor) ||
            !TrySingle(httpContext.Request.Query, "cursor", out var cursor))
        {
            return Forbidden();
        }
        try
        {
            var page = await service.ListAsync(
                new(actorId, tenantContext.OrganizationId, status, driverId, scheduledFor, cursor),
                cancellationToken);
            return Results.Ok(new RoutePageResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (
            exception is RoutingForbiddenException or RoutingUnavailableException) { return Forbidden(); }
    }

    private static async Task<IResult> GetAsync(
        string routeId,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRouteService service,
        CancellationToken cancellationToken)
    {
        if (!TryGuid(routeId, out var parsedRouteId) ||
            !TrySession(session, tenantContext, out var actorId)) return NotFound();
        try
        {
            return Results.Ok(ToResponse(await service.GetAsync(
                new(actorId, tenantContext.OrganizationId, parsedRouteId),
                cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (
            exception is RoutingForbiddenException or RoutingUnavailableException) { return Forbidden(); }
        catch (RoutingNotFoundException) { return NotFound(); }
    }

    private static async Task<IResult> AddStopAsync(
        string routeId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRouteService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<AddRouteStopRequest>(httpContext, cancellationToken);
        if (!TryGuid(routeId, out var parsedRouteId) || request?.OrderId is not { } orderId ||
            orderId == Guid.Empty || request.ExpectedVersion is not >= 1 ||
            request.ExtensionData is { Count: > 0 } ||
            !TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !TrySession(session, tenantContext, out var actorId)) return Conflict("INVALID_REQUEST");
        try
        {
            var result = await service.AddStopAsync(
                new(actorId, tenantContext.OrganizationId, key, parsedRouteId, orderId,
                    request.ExpectedVersion.Value, session.MfaSatisfied, httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Json(ToResponse(result), statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RoutingForbiddenException) { return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.AddRouteStop); }
        catch (RoutingNotFoundException) { return NotFound(); }
        catch (RoutingConflictException exception) { return Conflict(PublicCode(exception.Code)); }
        catch (RoutingUnavailableException) { return Conflict("CONFLICT"); }
    }

    private static async Task<IResult> RemoveStopAsync(
        string routeId,
        string stopId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRouteService service,
        CancellationToken cancellationToken)
    {
        if (!TryGuid(routeId, out var parsedRouteId) || !TryGuid(stopId, out var parsedStopId) ||
            !TryRequiredVersion(httpContext.Request.Query, out var expectedVersion) ||
            !TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !TrySession(session, tenantContext, out var actorId)) return Conflict("INVALID_REQUEST");
        try
        {
            return Results.Ok(ToResponse(await service.RemoveStopAsync(
                new(actorId, tenantContext.OrganizationId, key, parsedRouteId, parsedStopId,
                    expectedVersion, session.MfaSatisfied, httpContext.TraceIdentifier),
                cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RoutingForbiddenException) { return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.RemoveRouteStop); }
        catch (RoutingNotFoundException) { return NotFound(); }
        catch (RoutingConflictException exception) { return Conflict(PublicCode(exception.Code)); }
        catch (RoutingUnavailableException) { return Conflict("CONFLICT"); }
    }

    private static async Task<IResult> ReorderStopsAsync(
        string routeId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRouteService service,
        CancellationToken cancellationToken)
    {
        var request = await ReadAsync<ReorderRouteStopsRequest>(httpContext, cancellationToken);
        if (!TryGuid(routeId, out var parsedRouteId) || request?.ExpectedVersion is not >= 1 ||
            request.StopIds is null || request.StopIds.Any(id => id == Guid.Empty) ||
            request.StopIds.Distinct().Count() != request.StopIds.Count ||
            request.ExtensionData is { Count: > 0 } ||
            !TryReadIdempotencyKey(httpContext.Request, out var key) ||
            !TrySession(session, tenantContext, out var actorId)) return Conflict("INVALID_REQUEST");
        try
        {
            return Results.Ok(ToResponse(await service.ReorderStopsAsync(
                new(actorId, tenantContext.OrganizationId, key, parsedRouteId,
                    request.ExpectedVersion.Value, request.StopIds, session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RoutingForbiddenException) { return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.ReorderRouteStops); }
        catch (RoutingNotFoundException) { return NotFound(); }
        catch (RoutingConflictException exception) { return Conflict(PublicCode(exception.Code)); }
        catch (RoutingUnavailableException) { return Conflict("CONFLICT"); }
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(RequestJsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TrySession(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        out Guid actorId)
    {
        actorId = default;
        if (!session.IsActive || session.UserId is not { } id || !tenantContext.IsSelected) return false;
        actorId = id;
        return true;
    }

    private static bool TryReadIdempotencyKey(HttpRequest request, out string key)
    {
        key = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !IdempotencyKeyPolicy.IsValid(values[0])) return false;
        key = values[0]!;
        return true;
    }

    private static bool TryGuid(string value, out Guid parsed) =>
        Guid.TryParseExact(value, "D", out parsed) && parsed != Guid.Empty &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool TrySingle(IQueryCollection query, string name, out string? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values)) return true;
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) return false;
        value = values[0];
        return true;
    }

    private static bool TryOptionalGuid(IQueryCollection query, string name, out Guid? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values)) return true;
        if (values.Count != 1 || values[0] is not { } text || !TryGuid(text, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryOptionalDate(IQueryCollection query, string name, out DateOnly? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var values)) return true;
        if (values.Count != 1 || values[0] is not { } text ||
            !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryRequiredVersion(IQueryCollection query, out int value)
    {
        value = 0;
        return query.TryGetValue("expected_version", out var values) && values.Count == 1 &&
            int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1;
    }

    private static RouteResponse ToResponse(RouteResult route) => new(
        route.Id,
        route.Status,
        route.Version,
        route.DriverId,
        route.CityId,
        route.ServiceAreaId,
        route.ScheduledFor,
        route.AssignmentCostCentsTotal,
        route.StopCount);

    private static RouteDetailResponse ToResponse(RouteDetailResult route) => new(
        route.Id,
        route.Status,
        route.Version,
        route.DriverId,
        route.CityId,
        route.ServiceAreaId,
        route.ScheduledFor,
        route.AssignmentCostCentsTotal,
        route.StopCount,
        route.Stops.Select(stop => new RouteStopResponse(
            stop.Id, stop.OrderId, stop.Sequence, stop.StopType, stop.Status)).ToArray());

    private static string PublicCode(RoutingConflictCode code) => code switch
    {
        RoutingConflictCode.InvalidRequest => "INVALID_REQUEST",
        RoutingConflictCode.DriverIneligible => "DRIVER_INELIGIBLE",
        RoutingConflictCode.VersionConflict => "VERSION_CONFLICT",
        RoutingConflictCode.InvalidStopSet => "INVALID_STOP_SET",
        _ => "CONFLICT",
    };

    private static IResult Conflict(string code) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");
}

public sealed record CreateRouteRequest(
    [property: JsonPropertyName("driver_id")] Guid? DriverId,
    [property: JsonPropertyName("city_id")] Guid? CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("scheduled_for")] DateOnly? ScheduledFor)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record AddRouteStopRequest(
    [property: JsonPropertyName("order_id")] Guid? OrderId,
    [property: JsonPropertyName("expected_version")] int? ExpectedVersion)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ReorderRouteStopsRequest(
    [property: JsonPropertyName("expected_version")] int? ExpectedVersion,
    [property: JsonPropertyName("stop_ids")] IReadOnlyList<Guid>? StopIds)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public record RouteResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("scheduled_for")] DateOnly? ScheduledFor,
    [property: JsonPropertyName("assignment_cost_cents_total")] long AssignmentCostCentsTotal,
    [property: JsonPropertyName("stop_count")] int StopCount);

public sealed record RouteDetailResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("city_id")] Guid CityId,
    [property: JsonPropertyName("service_area_id")] Guid? ServiceAreaId,
    [property: JsonPropertyName("scheduled_for")] DateOnly? ScheduledFor,
    [property: JsonPropertyName("assignment_cost_cents_total")] long AssignmentCostCentsTotal,
    [property: JsonPropertyName("stop_count")] int StopCount,
    [property: JsonPropertyName("stops")] IReadOnlyList<RouteStopResponse> Stops);

public sealed record RouteStopResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("stop_type")] string StopType,
    [property: JsonPropertyName("status")] string Status);

public sealed record RoutePageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<RouteResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
