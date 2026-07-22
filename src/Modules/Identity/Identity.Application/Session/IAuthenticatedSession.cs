using Identity.Application.Authentication;

namespace Identity.Application.Session;

public interface IAuthenticatedSession
{
    bool IsAuthenticated { get; }

    string? Subject { get; }

    NormalizedIdentityStatus? IdentityStatus { get; }

    bool MfaSatisfied { get; }

    IReadOnlyList<NormalizedOrganizationMembership> ActiveMemberships { get; }

    bool HasOrganizationAccess(Guid organizationId);

    bool HasRole(Guid organizationId, NormalizedOrganizationRole role);

    bool HasAnyActiveRole(NormalizedOrganizationRole role);
}
