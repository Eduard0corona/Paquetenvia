using System.Security.Claims;
using Identity.Application.Bootstrap;
using Identity.Endpoints.Authorization;
using Identity.Endpoints.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Identity.Endpoints.Testing;

public static class IdentityTestEndpoints
{
    public static IEndpointRouteBuilder MapIdentityTestProbes(
        this IEndpointRouteBuilder endpoints,
        IWebHostEnvironment environment)
    {
        if (!string.Equals(environment.EnvironmentName, "Testing", StringComparison.Ordinal))
        {
            return endpoints;
        }

        endpoints.MapGet(
                "/__tests/security/authenticated",
                static () => Results.NoContent())
            .RequireAuthorization(IdentityPolicies.ActiveIdentity)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/__tests/security/privileged",
                static () => Results.NoContent())
            .RequireAuthorization(IdentityPolicies.PrivilegedMfa)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/__tests/security/organization/{organizationId:guid}",
                AuthorizeOrganizationAsync)
            .RequireAuthorization(IdentityPolicies.ActiveIdentity)
            .ExcludeFromDescription();

        endpoints.MapHub<SecurityTestHub>("/__tests/hubs/security")
            .RequireAuthorization(IdentityPolicies.ActiveIdentity)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> AuthorizeOrganizationAsync(
        Guid organizationId,
        ClaimsPrincipal user,
        IAuthorizationService authorizationService)
    {
        var result = await authorizationService.AuthorizeAsync(
            user,
            new OrganizationAuthorizationResource(organizationId),
            new OrganizationMembershipRequirement(OrganizationRole.Viewer));

        return result.Succeeded ? Results.NoContent() : Results.Forbid();
    }
}
