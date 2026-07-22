using Microsoft.AspNetCore.Authorization;
using Organizations.Application.Session;
using Paqueteria.Application.Tenancy;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Authorization;

public static class OrganizationPolicies
{
    public const string ActiveOrganizationMember = "Organizations.ActiveOrganizationMember";
    public const string PlatformAdminMfa = "Organizations.PlatformAdminMfa";
}

public sealed record ActiveOrganizationRoleRequirement(OrganizationRole? RequiredRole, bool RequireMfa)
    : IAuthorizationRequirement;

public sealed class ActiveOrganizationRoleHandler(
    IOrganizationRequestSession session,
    ITenantContext tenantContext) : AuthorizationHandler<ActiveOrganizationRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveOrganizationRoleRequirement requirement)
    {
        if (session.IsAuthenticated &&
            session.IsActive &&
            tenantContext.IsSelected &&
            session.HasOrganizationAccess(tenantContext.OrganizationId) &&
            (!requirement.RequireMfa || session.MfaSatisfied) &&
            (requirement.RequiredRole is null || session.HasRole(tenantContext.OrganizationId, requirement.RequiredRole.Value)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
