using System.Text.Json;
using System.Text.Json.Serialization;
using Dispatch.Application.Assignments;
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
            .ProducesProblem(StatusCodes.Status409Conflict);

        endpoints.MapGet("/api/v1/driver/me/stops", ListMyStopsAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listMyStops")
            .WithTags("Driver")
            .Produces<IReadOnlyList<DriverStopResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
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
            return Forbidden();
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

    private static async Task<IResult> ListMyStopsAsync(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverStopsQuery query,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive ||
            session.UserId is not { } actorId ||
            !tenantContext.IsSelected)
        {
            return Forbidden();
        }

        try
        {
            var stops = await query.ListCurrentDriverStopsAsync(
                actorId,
                tenantContext.OrganizationId,
                cancellationToken);
            return Results.Ok(stops.Select(value => new DriverStopResponse(
                value.OrderPublicId,
                value.StopType,
                value.Status,
                value.AddressSummary)).ToArray());
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
        new MoneyResponse(result.Cost.Currency, result.Cost.AmountCents));

    private static string PublicCode(AssignmentConflictCode code) => code switch
    {
        AssignmentConflictCode.DriverDocumentExpired => "DRIVER_DOCUMENT_EXPIRED",
        AssignmentConflictCode.DriverIneligible or AssignmentConflictCode.CapacityInsufficient =>
            "DRIVER_INELIGIBLE",
        _ => "CONFLICT",
    };

    private static IResult Conflict(string code) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");
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
    [property: JsonPropertyName("cost")] MoneyResponse Cost);

public sealed record DriverStopResponse(
    [property: JsonPropertyName("order_public_id")] string OrderPublicId,
    [property: JsonPropertyName("stop_type")] string StopType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("address_summary")] string AddressSummary);
