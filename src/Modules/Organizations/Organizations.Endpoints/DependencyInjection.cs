using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddOrganizationsEndpoints(this IServiceCollection services)
    {
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>());
        services.AddScoped<IAuthorizationHandler, ActiveOrganizationRoleHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(OrganizationPolicies.ActiveOrganizationMember, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ActiveOrganizationRoleRequirement(null, false));
            })
            .AddPolicy(OrganizationPolicies.PlatformAdminMfa, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ActiveOrganizationRoleRequirement(OrganizationRole.PlatformAdmin, true));
            });
        return services;
    }
}
