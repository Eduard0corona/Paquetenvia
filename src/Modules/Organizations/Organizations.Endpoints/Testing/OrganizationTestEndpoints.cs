using Microsoft.Extensions.Hosting;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints.Testing;

public static class OrganizationTestEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationTestProbes(
        this IEndpointRouteBuilder endpoints,
        IHostEnvironment environment)
    {
        if (!environment.IsEnvironment("Testing"))
        {
            return endpoints;
        }

        endpoints.MapGet("/__tests/tenancy/active", (ITenantContext tenant) =>
                Results.Ok(new { organizationId = tenant.OrganizationId }))
            .RequireTenantContext()
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .ExcludeFromDescription();

        endpoints.MapGet("/__tests/tenancy/platform-admin", () => Results.NoContent())
            .RequireTenantContext()
            .RequireAuthorization(OrganizationPolicies.PlatformAdminMfa)
            .ExcludeFromDescription();

        return endpoints;
    }
}
