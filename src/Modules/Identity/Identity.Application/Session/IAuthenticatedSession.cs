using Identity.Application.Bootstrap;

namespace Identity.Application.Session;

public interface IAuthenticatedSession
{
    bool IsAuthenticated { get; }

    string? Subject { get; }

    IdentityContextStatus? IdentityStatus { get; }

    bool MfaSatisfied { get; }

    IReadOnlyList<IdentityContextMembership> ActiveMemberships { get; }

    bool HasOrganizationAccess(Guid organizationId);

    bool HasRole(Guid organizationId, OrganizationRole role);

    bool HasAnyActiveRole(OrganizationRole role);
}
