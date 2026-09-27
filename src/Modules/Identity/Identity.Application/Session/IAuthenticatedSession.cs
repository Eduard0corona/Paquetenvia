using Identity.Application.Bootstrap;
using Paqueteria.Domain.Tenancy;

namespace Identity.Application.Session;

public interface IAuthenticatedSession
{
    bool IsAuthenticated { get; }

    string? Subject { get; }

    Guid? UserId { get; }

    IdentityContextStatus? IdentityStatus { get; }

    bool MfaSatisfied { get; }

    /// <summary>AUTH-EMAIL-VERIFIED-REQUIRED: the provider asserted a verified email for this session.</summary>
    bool EmailVerified { get; }

    IReadOnlyList<IdentityContextMembership> ActiveMemberships { get; }

    bool HasOrganizationAccess(Guid organizationId);

    bool HasRole(Guid organizationId, OrganizationRole role);

    bool HasAnyActiveRole(OrganizationRole role);
}
