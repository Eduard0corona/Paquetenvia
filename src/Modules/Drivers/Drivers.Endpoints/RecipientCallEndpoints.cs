using System.Globalization;
using System.Text.Json.Serialization;
using Drivers.Application.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Drivers.Endpoints;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11: "Llamar al destinatario" (AI-05 <c>getRecipientCallAvailability</c> and
/// <c>requestRecipientCall</c>). DRIVER only, for the driver's own ACCEPTED or ACTIVE assignment, while the order is
/// DELIVERING. No phone number is accepted or returned: the request has no body and the answer only says that the
/// call was requested. An unknown order, another tenant's order and a stop that is not the driver's are the same
/// uniform 404.
/// </summary>
public static class RecipientCallEndpoints
{
    public const string Path = "/api/v1/driver/me/stops/{orderId}/recipient-call";

    public static IEndpointRouteBuilder MapRecipientCallEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetAvailabilityAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getRecipientCallAvailability")
            .WithTags("Driver")
            .Produces<RecipientCallAvailabilityResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(Path, RequestAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("requestRecipientCall")
            .WithTags("Driver")
            .Produces<RecipientCallRequestResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> GetAvailabilityAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRecipientCallService service,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetRecipientCallAvailability) is { } denied)
        {
            return denied;
        }

        if (!Guid.TryParseExact(orderId, "D", out var parsedOrderId) || parsedOrderId == Guid.Empty)
        {
            return NotFound();
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await service.GetAvailabilityAsync(
                actorId,
                tenantContext.OrganizationId,
                parsedOrderId,
                cancellationToken);
            return Results.Ok(new RecipientCallAvailabilityResponse(result.Available, result.Reason));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientCallForbiddenException)
        {
            return Forbidden();
        }
        catch (RecipientCallNotFoundException)
        {
            return NotFound();
        }
        catch (RecipientCallUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> RequestAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRecipientCallService service,
        CancellationToken cancellationToken)
    {
        // Shape first: exactly one valid Idempotency-Key, no query and no body (a phone number is never sent).
        var keys = httpContext.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !IdempotencyKeyPolicy.IsValid(keys[0]) ||
            httpContext.Request.Query.Count != 0 ||
            httpContext.Request.ContentLength is > 0 ||
            (httpContext.Request.ContentLength is null && httpContext.Request.Headers.TransferEncoding.Count != 0))
        {
            return Conflict(RecipientCallConflictCodes.InvalidRequest);
        }

        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.RequestRecipientCall) is { } denied)
        {
            return denied;
        }

        if (!Guid.TryParseExact(orderId, "D", out var parsedOrderId) || parsedOrderId == Guid.Empty)
        {
            return NotFound();
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await service.RequestAsync(
                new RequestRecipientCallCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    parsedOrderId,
                    keys[0]!,
                    httpContext.TraceIdentifier),
                cancellationToken);
            return Results.Json(
                new RecipientCallRequestResponse(result.CallRequestId, result.Status),
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientCallForbiddenException)
        {
            return Forbidden();
        }
        catch (RecipientCallNotFoundException)
        {
            return NotFound();
        }
        catch (RecipientCallConflictException exception)
        {
            return Conflict(RecipientCallConflictCodes.All.Contains(exception.Code)
                ? exception.Code
                : RecipientCallConflictCodes.InvalidRequest);
        }
        catch (RecipientCallRateLimitedException exception)
        {
            httpContext.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(exception.RetryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests.");
        }
        catch (RecipientCallUnavailableException)
        {
            return Unavailable();
        }
    }

    private static bool TrySession(IOrganizationRequestSession session, ITenantContext tenantContext, out Guid actorId)
    {
        actorId = default;
        if (!session.IsActive || session.UserId is not { } id || !tenantContext.IsSelected)
        {
            return false;
        }

        actorId = id;
        return true;
    }

    private static IResult Conflict(string code) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

public sealed record RecipientCallAvailabilityResponse(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record RecipientCallRequestResponse(
    [property: JsonPropertyName("call_request_id")] Guid CallRequestId,
    [property: JsonPropertyName("status")] string Status);
