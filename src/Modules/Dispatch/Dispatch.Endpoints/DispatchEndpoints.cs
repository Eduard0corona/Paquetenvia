using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;
using Dispatch.Application.Fleet;
using Dispatch.Application.Stops;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Dispatch.Endpoints;

public static class DispatchEndpoints
{
    /// <summary>
    /// SEC-004: <c>listAssignableDrivers</c>, <c>RateLimits:AssignableDrivers</c> (60 per minute) per user. The host
    /// registers it with every other policy.
    /// </summary>
    public const string AssignableDriversRateLimitPolicy = "Dispatch.AssignableDrivers";

    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapDispatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/orders/{orderId}/assignments", CreateAssignmentAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("assignDriver")
            .WithTags("Dispatch")
            .Accepts<CreateAssignmentRequest>("application/json")
            .Produces<AssignmentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/orders/{orderId}/assignable-drivers", ListAssignableDriversAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .RequireRateLimiting(DispatchEndpoints.AssignableDriversRateLimitPolicy)
            .WithName("listAssignableDrivers")
            .WithTags("Dispatch")
            .Produces<AssignableDriverPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/drivers", ListOrganizationDriversAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listOrganizationDrivers")
            .WithTags("Dispatch")
            .Produces<OrganizationDriverPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/driver/me/stops", ListMyStopsAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listMyStops")
            .WithTags("Driver")
            .Produces<DriverStopListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/external-offers", CreateExternalOfferAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("createExternalOffer")
            .WithTags("Dispatch")
            .Accepts<CreateExternalOfferRequest>("application/json")
            .Produces<ExternalOfferResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost("/api/v1/external-offers/{offerId}/accept", AcceptExternalOfferAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("acceptExternalOffer")
            .WithTags("Driver")
            .Produces<AssignmentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet("/api/v1/driver/me/external-offers", ListMyExternalOffersAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listMyEligibleExternalOffers")
            .WithTags("Driver")
            .Produces<ExternalOfferPageResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> CreateExternalOfferAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IExternalOfferService service,
        CancellationToken cancellationToken)
    {
        CreateExternalOfferRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<CreateExternalOfferRequest>(
                RequestJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return Conflict("INVALID_REQUEST");
        }

        if (request?.OrderId is not { } orderId || orderId == Guid.Empty ||
            request.CommissionCents is not >= 0 ||
            request.ExpiresAt is not { } expiresAt || expiresAt.Offset != TimeSpan.Zero ||
            request.ExtensionData is { Count: > 0 } ||
            request.EligibleConstraints?.ExtensionData is { Count: > 0 } ||
            !TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            !TrySession(session, tenantContext, out var actorId))
        {
            return Conflict("INVALID_REQUEST");
        }

        var constraints = request.EligibleConstraints is null
            ? new ExternalOfferConstraints([], [], false)
            : new ExternalOfferConstraints(
                request.EligibleConstraints.VehicleTypes ?? [],
                request.EligibleConstraints.ServiceAreaIds ?? [],
                request.EligibleConstraints.RequiresCod ?? false);
        try
        {
            var result = await service.CreateAsync(
                new CreateExternalOfferCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    orderId,
                    request.CommissionCents.Value,
                    expiresAt,
                    constraints,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Json(ToResponse(result), statusCode: StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalOfferForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.CreateExternalOffer);
        }
        catch (ExternalOfferConflictException exception)
        {
            return Conflict(PublicCode(exception.Code));
        }
        catch (ExternalOfferInfrastructureException)
        {
            return Conflict("CONFLICT");
        }
    }

    private static async Task<IResult> AcceptExternalOfferAsync(
        string offerId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IExternalOfferService service,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(offerId, "D", out var parsedOfferId) ||
            parsedOfferId == Guid.Empty ||
            !TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) ||
            !TrySession(session, tenantContext, out var actorId))
        {
            return Conflict("INVALID_REQUEST");
        }
        try
        {
            var result = await service.AcceptAsync(
                new AcceptExternalOfferCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    parsedOfferId,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Ok(ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalOfferForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.AcceptExternalOffer);
        }
        catch (ExternalOfferConflictException exception)
        {
            return Conflict(PublicCode(exception.Code));
        }
        catch (ExternalOfferInfrastructureException)
        {
            return Conflict("CONFLICT");
        }
    }

    private static async Task<IResult> ListMyExternalOffersAsync(
        string? cursor,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IExternalOfferService service,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListMyEligibleExternalOffers) is { } denied)
        {
            return denied;
        }

        try
        {
            var page = await service.ListEligibleAsync(
                actorId,
                tenantContext.OrganizationId,
                cursor,
                cancellationToken);
            return Results.Ok(new ExternalOfferPageResponse(
                page.Items.Select(ToResponse).ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalOfferForbiddenException)
        {
            return Forbidden();
        }
        catch (ExternalOfferInfrastructureException)
        {
            return Forbidden();
        }
    }

    private static async Task<IResult> CreateAssignmentAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IAssignmentService service,
        CancellationToken cancellationToken)
    {
        CreateAssignmentRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<CreateAssignmentRequest>(
                RequestJsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return Conflict("INVALID_REQUEST");
        }

        if (!Guid.TryParseExact(orderId, "D", out var parsedOrderId) ||
            parsedOrderId == Guid.Empty ||
            request is null ||
            request.DriverId is not { } driverId ||
            driverId == Guid.Empty ||
            request.AssignmentType != "OWN" ||
            request.CostCents is not >= 0 ||
            request.RouteId is not null ||
            request.ExtensionData is { Count: > 0 } ||
            !TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey))
        {
            return Conflict("INVALID_REQUEST");
        }

        if (!session.IsActive ||
            session.UserId is not { } actorId ||
            !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        try
        {
            var result = await service.CreateOwnDriverAssignmentAsync(
                new CreateOwnDriverAssignmentCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    idempotencyKey,
                    parsedOrderId,
                    driverId,
                    request.AssignmentType,
                    request.CostCents,
                    request.RouteId,
                    session.MfaSatisfied,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Created(
                $"/api/v1/orders/{parsedOrderId:D}/assignments",
                ToResponse(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AssignmentForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.AssignDriver);
        }
        catch (AssignmentNotFoundException)
        {
            return NotFound();
        }
        catch (AssignmentConflictException exception)
        {
            return Conflict(PublicCode(exception.Code));
        }
        catch (AssignmentInfrastructureException)
        {
            return Conflict("CONFLICT");
        }
    }

    /// <summary>
    /// UI-PHASE2-DRIVER-PICKER-2026-10-05. The only query parameter is <c>cursor</c>, at most once and issued by this
    /// operation; anything else is 409 INVALID_REQUEST before capability or any row is read. The assignDriver
    /// capability is then settled from the session and again inside the tenant transaction; a malformed, missing or
    /// foreign order is the uniform 404 and an order that does not admit an assignment now is 409 CONFLICT.
    /// </summary>
    private static async Task<IResult> ListAssignableDriversAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IAssignableDriversQuery query,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (!TryReadAssignableDriversQuery(httpContext.Request.Query, out var cursor))
        {
            return Conflict("INVALID_REQUEST");
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListAssignableDrivers) is { } denied)
        {
            return denied;
        }

        if (!Guid.TryParseExact(orderId, "D", out var parsedOrderId) || parsedOrderId == Guid.Empty)
        {
            return NotFound();
        }

        try
        {
            var page = await query.ListAsync(
                new ListAssignableDriversQuery(
                    actorId,
                    tenantContext.OrganizationId,
                    session.MfaSatisfied,
                    parsedOrderId,
                    cursor),
                cancellationToken);
            return Results.Ok(new AssignableDriverPageResponse(
                page.Items.Select(item => new AssignableDriverResponse(
                    item.DriverId,
                    item.DriverReference,
                    item.VehicleType,
                    item.Eligible,
                    item.IneligibilityReasons,
                    item.ActiveAssignmentCount)).ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AssignmentForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.ListAssignableDrivers);
        }
        catch (AssignmentNotFoundException)
        {
            return NotFound();
        }
        catch (AssignmentConflictException)
        {
            return Conflict("CONFLICT");
        }
        catch (AssignmentInfrastructureException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service Unavailable.");
        }
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the only query parameter is <c>details</c>, at most once, exactly
    /// <c>true</c> or <c>false</c>; absent means <c>true</c>. Anything else is 409 INVALID_REQUEST.
    /// </summary>
    public static bool TryReadDriverStopsQuery(IQueryCollection query, out bool includeDetails)
    {
        includeDetails = true;
        if (query.Keys.Any(key => !string.Equals(key, "details", StringComparison.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        if (!query.TryGetValue("details", out var value))
        {
            return true;
        }

        switch (value[0])
        {
            case "true":
                return true;
            case "false":
                includeDetails = false;
                return true;
            default:
                return false;
        }
    }

    public static bool TryReadAssignableDriversQuery(IQueryCollection query, out AssignableDriverCursor? cursor)
    {
        cursor = null;
        if (query.Keys.Any(key => !string.Equals(key, "cursor", StringComparison.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        return !query.TryGetValue("cursor", out var value) ||
            AssignableDriverCursorCodec.TryDecode(value[0], out cursor);
    }

    /// <summary>
    /// UI-PHASE3-DRIVERS-2026-10-11. The only query parameter is <c>cursor</c>, at most once and issued by this
    /// operation; anything else is 409 INVALID_REQUEST before capability or any row is read. The capability is then
    /// settled from the session and again inside the tenant transaction; the list has no resource of its own, so
    /// there is no 404, and a store failure or a value outside the published vocabulary is 503.
    /// </summary>
    private static async Task<IResult> ListOrganizationDriversAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IOrganizationDriversQuery query,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (!TryReadOrganizationDriversQuery(httpContext.Request.Query, out var afterDriverId))
        {
            return Conflict("INVALID_REQUEST");
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListOrganizationDrivers) is { } denied)
        {
            return denied;
        }

        try
        {
            var page = await query.ListAsync(
                new ListOrganizationDriversQuery(
                    actorId,
                    tenantContext.OrganizationId,
                    session.MfaSatisfied,
                    afterDriverId),
                cancellationToken);
            return Results.Ok(new OrganizationDriverPageResponse(
                page.Items.Select(item => new OrganizationDriverResponse(
                    item.DriverId,
                    item.DriverReference,
                    item.VehicleType,
                    item.Status,
                    item.ActiveStopCount,
                    item.LastPositionAt)).ToArray(),
                page.NextCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AssignmentForbiddenException)
        {
            return TenantCapabilityGate.Refused(session, tenantContext, TenantCapabilities.ListOrganizationDrivers);
        }
        catch (AssignmentInfrastructureException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service Unavailable.");
        }
    }

    public static bool TryReadOrganizationDriversQuery(IQueryCollection query, out Guid? afterDriverId)
    {
        afterDriverId = null;
        if (query.Keys.Any(key => !string.Equals(key, "cursor", StringComparison.Ordinal)) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            return false;
        }

        if (!query.TryGetValue("cursor", out var value))
        {
            return true;
        }

        if (!OrganizationDriverCursorCodec.TryDecode(value[0], out var after))
        {
            return false;
        }

        afterDriverId = after;
        return true;
    }

    private static async Task<IResult> ListMyStopsAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverStopsQuery query,
        CancellationToken cancellationToken)
    {
        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the body can carry decrypted addresses and contact names, so no
        // answer of this operation may be stored by a browser, a proxy or a shared cache.
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!session.IsActive ||
            session.UserId is not { } actorId ||
            !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        if (!TryReadDriverStopsQuery(httpContext.Request.Query, out var includeDetails))
        {
            return Conflict("INVALID_REQUEST");
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.ListMyStops) is { } denied)
        {
            return denied;
        }

        try
        {
            var result = await query.ListCurrentDriverStopsAsync(
                actorId,
                tenantContext.OrganizationId,
                includeDetails,
                cancellationToken);
            return Results.Ok(new DriverStopListResponse(
                result.Stops.Select(ToResponse).ToArray(),
                result.CompletedToday));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverStopsForbiddenException)
        {
            return Forbidden();
        }
        catch (DriverStopsInfrastructureException)
        {
            return Forbidden();
        }
    }

    private static DriverStopResponse ToResponse(DriverStopResult value) => new(
        value.OrderId,
        value.AggregateVersion,
        value.OrderPublicId,
        value.StopType,
        value.Status,
        value.AddressSummary,
        value.DetailsAvailable,
        value.ContactShortName,
        value.ContactFullName,
        value.ZoneName,
        value.ServiceWindow is { } window ? new DriverStopServiceWindowResponse(window.From, window.To) : null,
        value.Cod is { } cod
            ? new DriverStopCodResponse(new MoneyResponse("MXN", cod.ExpectedCents), cod.Recorded)
            : null,
        value.Navigation is { } navigation
            ? new DriverStopNavigationResponse(
                navigation.Latitude,
                navigation.Longitude,
                navigation.AddressText,
                navigation.References)
            : null);

    private static bool TryReadIdempotencyKey(HttpRequest request, out string value)
    {
        value = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !IdempotencyKeyPolicy.IsValid(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static AssignmentResponse ToResponse(AssignmentResult result) => new(
        result.Id,
        result.OrderId,
        result.DriverId,
        result.Status,
        new MoneyResponse(result.Cost.Currency, result.Cost.AmountCents),
        result.RouteId);

    private static ExternalOfferResponse ToResponse(ExternalOfferResult result) => new(
        result.Id,
        result.OrderId,
        result.Status,
        new MoneyResponse(result.Commission.Currency, result.Commission.AmountCents),
        result.ExpiresAt,
        result.AcceptedByDriverId,
        result.AcceptedAt,
        result.Version);

    private static bool TrySession(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        out Guid actorId)
    {
        actorId = default;
        if (!session.IsActive || session.UserId is not { } id || !tenantContext.IsSelected)
        {
            return false;
        }
        actorId = id;
        return true;
    }

    private static string PublicCode(AssignmentConflictCode code) => code switch
    {
        AssignmentConflictCode.DriverDocumentExpired => "DRIVER_DOCUMENT_EXPIRED",
        AssignmentConflictCode.DriverIneligible or AssignmentConflictCode.CapacityInsufficient =>
            "DRIVER_INELIGIBLE",
        _ => "CONFLICT",
    };

    private static string PublicCode(ExternalOfferConflictCode code) => code switch
    {
        ExternalOfferConflictCode.InvalidRequest => "INVALID_REQUEST",
        ExternalOfferConflictCode.OfferExpired => "OFFER_EXPIRED",
        ExternalOfferConflictCode.DriverIneligible => "DRIVER_INELIGIBLE",
        _ => "CONFLICT",
    };

    private static IResult Conflict(string code) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");
}

public sealed record CreateAssignmentRequest(
    [property: JsonPropertyName("driver_id")] Guid? DriverId,
    [property: JsonPropertyName("assignment_type")] string? AssignmentType,
    [property: JsonPropertyName("cost_cents")] long? CostCents,
    [property: JsonPropertyName("route_id")] Guid? RouteId)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record MoneyResponse(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount_cents")] long AmountCents);

public sealed record AssignmentResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("cost")] MoneyResponse Cost,
    [property: JsonPropertyName("route_id")] Guid? RouteId);

public sealed record AssignableDriverResponse(
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("driver_reference")] string DriverReference,
    [property: JsonPropertyName("vehicle_type")] string VehicleType,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("ineligibility_reasons")] IReadOnlyList<string> IneligibilityReasons,
    [property: JsonPropertyName("active_assignment_count")] int ActiveAssignmentCount);

public sealed record AssignableDriverPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<AssignableDriverResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11: a driver of the dispatcher's read-only list. The id is the stable key and is never
/// shown; last_position_at is only the capture time of the latest position, never the position.
/// </summary>
public sealed record OrganizationDriverResponse(
    [property: JsonPropertyName("driver_id")] Guid DriverId,
    [property: JsonPropertyName("driver_reference")] string DriverReference,
    [property: JsonPropertyName("vehicle_type")] string VehicleType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("active_stop_count")] int ActiveStopCount,
    [property: JsonPropertyName("last_position_at")] DateTimeOffset? LastPositionAt);

public sealed record OrganizationDriverPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<OrganizationDriverResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>
/// AI-05 <c>DriverStopList</c> (listMyStops): the requesting driver's stops and how many of the driver's stops were
/// completed today in America/Mazatlan time, as a count only (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11).
/// </summary>
public sealed record DriverStopListResponse(
    [property: JsonPropertyName("stops")] IReadOnlyList<DriverStopResponse> Stops,
    [property: JsonPropertyName("completed_today")] int CompletedToday);

/// <summary>
/// AI-05 <c>DriverStop</c>. UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 adds the contact name (full on PICKUP and RETURN
/// stops, short on DELIVERY stops), zone, service window, COD of a DELIVERY stop, the navigation target and whether
/// the protected details are present; every member is always present, absent values are JSON null.
/// </summary>
public sealed record DriverStopResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("aggregate_version")] long AggregateVersion,
    [property: JsonPropertyName("order_public_id")] string OrderPublicId,
    [property: JsonPropertyName("stop_type")] string StopType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("address_summary")] string AddressSummary,
    [property: JsonPropertyName("details_available")] bool DetailsAvailable,
    [property: JsonPropertyName("contact_short_name")] string? ContactShortName,
    [property: JsonPropertyName("contact_full_name")] string? ContactFullName,
    [property: JsonPropertyName("zone_name")] string? ZoneName,
    [property: JsonPropertyName("service_window")] DriverStopServiceWindowResponse? ServiceWindow,
    [property: JsonPropertyName("cod")] DriverStopCodResponse? Cod,
    [property: JsonPropertyName("navigation")] DriverStopNavigationResponse? Navigation)
{
    public override string ToString() =>
        $"DriverStopResponse {{ OrderId = {OrderId:D}, Status = {Status}, [details redacted] }}";
}

public sealed record DriverStopServiceWindowResponse(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To);

public sealed record DriverStopCodResponse(
    [property: JsonPropertyName("expected")] MoneyResponse Expected,
    [property: JsonPropertyName("recorded")] bool Recorded);

public sealed record DriverStopNavigationResponse(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("address_text")] string AddressText,
    [property: JsonPropertyName("references")] string? References)
{
    public override string ToString() => "DriverStopNavigationResponse { [redacted] }";
}

public sealed record CreateExternalOfferRequest(
    [property: JsonPropertyName("order_id")] Guid? OrderId,
    [property: JsonPropertyName("commission_cents")] long? CommissionCents,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("eligible_constraints")] ExternalOfferConstraintsRequest? EligibleConstraints)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ExternalOfferConstraintsRequest(
    [property: JsonPropertyName("vehicle_types")] IReadOnlyList<string>? VehicleTypes,
    [property: JsonPropertyName("service_area_ids")] IReadOnlyList<Guid>? ServiceAreaIds,
    [property: JsonPropertyName("requires_cod")] bool? RequiresCod)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ExternalOfferResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("commission")] MoneyResponse Commission,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("accepted_by_driver_id")] Guid? AcceptedByDriverId,
    [property: JsonPropertyName("accepted_at")] DateTimeOffset? AcceptedAt,
    [property: JsonPropertyName("version")] int Version);

public sealed record ExternalOfferPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ExternalOfferResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);
