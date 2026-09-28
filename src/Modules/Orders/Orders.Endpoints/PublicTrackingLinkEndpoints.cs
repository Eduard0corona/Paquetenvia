using System.Text.Json.Serialization;
using Orders.Application.Tracking;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Orders.Endpoints;

/// <summary>
/// TRK-002-ISSUE-ENDPOINT: authenticated issuance and revocation of an order's public tracking link.
/// </summary>
/// <remarks>
/// The order must be owned by the selected organization; any other order, missing or cross-tenant, is the uniform
/// 404. The plaintext token exists only in the 201 body, served with <c>Cache-Control: no-store</c>: it is never
/// logged, never placed in a problem response and never stored (the service persists only its SHA-256 hash).
/// Nothing is sent to customers here; delivery over WhatsApp or email waits on GATE-004 and GATE-007.
/// </remarks>
public static class PublicTrackingLinkEndpoints
{
    public const string IssueRoute = "/api/v1/orders/{orderId:guid}/tracking-link";
    public const string RevokeRoute = "/api/v1/orders/{orderId:guid}/tracking-link/revoke";

    public static IEndpointRouteBuilder MapPublicTrackingLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(IssueRoute, IssueAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("issueTrackingLink")
            .WithTags("Tracking")
            .Produces<PublicTrackingLinkResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapPost(RevokeRoute, RevokeAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("revokeTrackingLink")
            .WithTags("Tracking")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> IssueAsync(
        Guid orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IPublicTrackingTokenService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) || orderId == Guid.Empty)
        {
            return Conflict();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return TenantCapabilityGate.Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.IssueTrackingLink) is { } denied)
        {
            return denied;
        }

        PublicTrackingTokenGrant grant;
        try
        {
            grant = await IssueOrRotateAsync(
                service,
                actorId,
                tenantContext.OrganizationId,
                orderId,
                idempotencyKey,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PublicTrackingTokenNotFoundException)
        {
            return NotFound();
        }
        catch (PublicTrackingTokenConflictException)
        {
            return Conflict();
        }
        catch (PublicTrackingTokenInfrastructureException)
        {
            return Unavailable();
        }

        // The body carries the only copy of the plaintext token: no shared or private cache may keep it.
        var headers = httpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        return Results.Json(
            new PublicTrackingLinkResponse(grant.TokenId, grant.OrderId, grant.Token, grant.ExpiresAt),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(
        Guid orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IPublicTrackingTokenService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadIdempotencyKey(httpContext.Request, out var idempotencyKey) || orderId == Guid.Empty)
        {
            return Conflict();
        }

        if (!session.IsActive || session.UserId is not { } actorId || !tenantContext.IsSelected)
        {
            return TenantCapabilityGate.Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.RevokeTrackingLink) is { } denied)
        {
            return denied;
        }

        try
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    orderId,
                    idempotencyKey),
                cancellationToken);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PublicTrackingTokenNotFoundException)
        {
            return NotFound();
        }
        catch (PublicTrackingTokenConflictException)
        {
            return Conflict();
        }
        catch (PublicTrackingTokenInfrastructureException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// The first link of an order is audited as TRACKING_TOKEN_ISSUED. When an active link already exists the
    /// service refuses a plain issue inside its own transaction, which rolls back without writing anything, and
    /// the request becomes a rotation audited as TRACKING_TOKEN_ROTATED that revokes every earlier link.
    /// </summary>
    private static async Task<PublicTrackingTokenGrant> IssueOrRotateAsync(
        IPublicTrackingTokenService service,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.IssueAsync(
                new IssuePublicTrackingTokenCommand(actorId, organizationId, orderId, requestId),
                cancellationToken);
        }
        catch (PublicTrackingTokenConflictException)
        {
            return await service.RotateAsync(
                new RotatePublicTrackingTokenCommand(actorId, organizationId, orderId, requestId),
                cancellationToken);
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

    private static IResult Conflict() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

/// <summary>
/// The one response that ever carries a plaintext public tracking token. It is never logged or stored; the
/// public link is <c>/track/{token}</c> on the tracking origin.
/// </summary>
public sealed record PublicTrackingLinkResponse(
    [property: JsonPropertyName("token_id")] Guid TokenId,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    /// <summary>Keeps the token out of any accidental <c>ToString()</c>, such as a structured log argument.</summary>
    public override string ToString() =>
        $"{nameof(PublicTrackingLinkResponse)} {{ TokenId = {TokenId}, OrderId = {OrderId}, Token = [redacted], ExpiresAt = {ExpiresAt:O} }}";
}
