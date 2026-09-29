using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Orders.Application.Tracking;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Orders.Endpoints;

/// <summary>
/// TRK-002-AUTO-LINK: the authenticated read (get-or-create) and revocation of an order's public tracking link.
/// </summary>
/// <remarks>
/// Every order gets its link when it is created; <c>issueTrackingLink</c> returns that same link, re-derived for the
/// current generation, for any retry and any Idempotency-Key, and never rotates it. It creates one only when the
/// order has none that can be shown, and never for a finished order (409 <c>TRACKING_LINK_ORDER_FINISHED</c>).
/// The order must be owned by the selected organization; any other order, missing or cross-tenant, is the uniform
/// 404. The plaintext token exists only in the 200 body, served with <c>Cache-Control: no-store</c>: it is never
/// logged, never placed in a problem response and never stored (only its SHA-256 is). Nothing is sent to customers
/// here; delivery over WhatsApp or email waits on GATE-004 and GATE-007.
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
            .Produces<PublicTrackingLinkResponse>(StatusCodes.Status200OK)
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
        IOptions<PublicTrackingOptions> options,
        PublicTrackingBaseUrlPolicy baseUrlPolicy,
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

        // Fail closed before any token is issued: a base this environment does not accept (http outside
        // Development and Testing) never becomes a link.
        var publicBaseUrl = options.Value.PublicBaseUrl;
        if (publicBaseUrl is null || !baseUrlPolicy.IsValid(publicBaseUrl))
        {
            return Unavailable();
        }

        PublicTrackingTokenGrant grant;
        try
        {
            grant = await service.GetOrCreateAsync(
                new GetOrCreatePublicTrackingLinkCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    orderId,
                    idempotencyKey),
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
        catch (PublicTrackingLinkOrderFinishedException)
        {
            return OrderFinished();
        }
        catch (PublicTrackingTokenConflictException)
        {
            return Conflict();
        }
        catch (PublicTrackingTokenInfrastructureException)
        {
            return Unavailable();
        }

        // The body carries the plaintext token: no shared or private cache may keep it.
        var headers = httpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        return Results.Json(
            PublicTrackingLinkResponse.From(grant, publicBaseUrl, baseUrlPolicy),
            statusCode: StatusCodes.Status200OK);
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

    /// <summary>The one coded 409; its only extension is the constant AI-05 problem code.</summary>
    private static IResult OrderFinished() =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = PublicTrackingLinkOrderFinishedException.ProblemCode,
            });

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

/// <summary>
/// The one response that carries a plaintext public tracking token (AI-05 <c>PublicTrackingLink</c>). It is never
/// logged or stored. <c>url</c> is the public page, <c>{PublicTracking:PublicBaseUrl}/track/{token}</c>.
/// </summary>
public sealed record PublicTrackingLinkResponse(
    [property: JsonPropertyName("token_id")] Guid TokenId,
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("generation")] int Generation,
    [property: JsonPropertyName("valid_until")] DateTimeOffset? ValidUntil)
{
    public static PublicTrackingLinkResponse From(
        PublicTrackingTokenGrant grant,
        string publicBaseUrl,
        PublicTrackingBaseUrlPolicy baseUrlPolicy)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(baseUrlPolicy);
        return new PublicTrackingLinkResponse(
            grant.TokenId,
            grant.OrderId,
            grant.Token,
            baseUrlPolicy.BuildUrl(publicBaseUrl, grant.Token),
            grant.Generation,
            grant.ValidUntil);
    }

    /// <summary>Keeps the token out of any accidental <c>ToString()</c>, such as a structured log argument.</summary>
    public override string ToString() =>
        $"{nameof(PublicTrackingLinkResponse)} {{ TokenId = {TokenId}, OrderId = {OrderId}, Token = [redacted], Url = [redacted], Generation = {Generation}, ValidUntil = {ValidUntil:O} }}";
}
