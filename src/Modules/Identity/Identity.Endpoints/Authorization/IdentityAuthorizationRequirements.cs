using Identity.Application.Authentication;
using Identity.Endpoints.Session;
using Microsoft.AspNetCore.Authorization;

namespace Identity.Endpoints.Authorization;

public sealed class AuthenticatedIdentityRequirement : IAuthorizationRequirement;

public sealed class ActiveIdentityRequirement : IAuthorizationRequirement;

public sealed class RequireMfaRequirement : IAuthorizationRequirement;

public sealed record AnyActiveOrganizationRoleRequirement(NormalizedOrganizationRole RequiredRole)
    : IAuthorizationRequirement;

public sealed record OrganizationMembershipRequirement(NormalizedOrganizationRole RequiredRole)
    : IAuthorizationRequirement;

public sealed record OrganizationAuthorizationResource(Guid OrganizationId);

public sealed class AuthenticatedIdentityHandler : AuthorizationHandler<AuthenticatedIdentityRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AuthenticatedIdentityRequirement requirement)
    {
        if (AuthenticatedSession.FromPrincipal(context.User).IsAuthenticated)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class ActiveIdentityHandler : AuthorizationHandler<ActiveIdentityRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveIdentityRequirement requirement)
    {
        var session = AuthenticatedSession.FromPrincipal(context.User);
        if (session.IsAuthenticated && session.IdentityStatus == NormalizedIdentityStatus.Active)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class RequireMfaHandler : AuthorizationHandler<RequireMfaRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequireMfaRequirement requirement)
    {
        var session = AuthenticatedSession.FromPrincipal(context.User);
        if (session.IsAuthenticated && session.MfaSatisfied)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class AnyActiveOrganizationRoleHandler
    : AuthorizationHandler<AnyActiveOrganizationRoleRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AnyActiveOrganizationRoleRequirement requirement)
    {
        var session = AuthenticatedSession.FromPrincipal(context.User);
        if (session.IsAuthenticated && session.HasAnyActiveRole(requirement.RequiredRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public sealed class OrganizationMembershipHandler
    : AuthorizationHandler<OrganizationMembershipRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OrganizationMembershipRequirement requirement)
    {
        var organizationId = context.Resource switch
        {
            Guid id => id,
            OrganizationAuthorizationResource resource => resource.OrganizationId,
            _ => Guid.Empty,
        };

        var session = AuthenticatedSession.FromPrincipal(context.User);
        if (organizationId != Guid.Empty &&
            session.IsAuthenticated &&
            session.IdentityStatus == NormalizedIdentityStatus.Active &&
            session.HasRole(organizationId, requirement.RequiredRole))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
