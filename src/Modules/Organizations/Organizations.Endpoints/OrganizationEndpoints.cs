using Organizations.Application.OrganizationContexts;
using Organizations.Application.OrganizationRoles;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints;

public static class OrganizationEndpoints
{
    /// <summary>ORG-MY-ROLES-2026-10-12: AI-05 <c>getMyOrganizationRoles</c>.</summary>
    public const string MyOrganizationRolesPath = "/api/v1/me/organization-roles";

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/me/organization-contexts", ListOrganizationContextsAsync)
            .RequireAuthorization("Identity.Active")
            .WithName("listOrganizationContexts")
            .WithTags("Identity")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapGet(MyOrganizationRolesPath, GetMyOrganizationRolesAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getMyOrganizationRoles")
            .WithTags("Identity")
            .Produces<MyOrganizationRolesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        endpoints.MapRegistrationEndpoints();
        endpoints.MapPendingMembershipEndpoints();
        return endpoints;
    }

    private static async Task<IResult> ListOrganizationContextsAsync(
        IOrganizationRequestSession session,
        IOrganizationContextReader reader,
        CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.UserId is not { } userId)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");
        }

        var memberships = session.ActiveMemberships
            .Select(membership => new AuthorizedOrganizationMembership(
                membership.OrganizationId,
                membership.Role,
                membership.IsDefault))
            .ToArray();

        try
        {
            return Results.Ok(await reader.ReadAsync(userId, memberships, cancellationToken));
        }
        catch (OrganizationContextUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
        }
    }

    /// <summary>
    /// ORG-MY-ROLES-2026-10-12 (project owner, "Endpoint nuevo de roles (Recommended)"): the requesting person's own
    /// ACTIVE membership roles in the organization selected with X-Organization-Id, read in one tenant transaction and
    /// never written. A person without an ACTIVE membership there receives the uniform 403 and reads nothing. Every
    /// answer carries Cache-Control no-store.
    /// </summary>
    private static async Task<IResult> GetMyOrganizationRolesAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IMyOrganizationRolesReader reader,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!session.IsActive || session.UserId is not { } userId || !tenantContext.IsSelected)
        {
            return TenantCapabilityGate.Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetMyOrganizationRoles) is { } denied)
        {
            return denied;
        }

        try
        {
            var roles = await reader.ReadAsync(userId, tenantContext.OrganizationId, cancellationToken);
            return roles.Count == 0
                ? TenantCapabilityGate.Forbidden()
                : Results.Ok(new MyOrganizationRolesResponse(roles));
        }
        catch (MyOrganizationRolesUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
        }
    }
}
