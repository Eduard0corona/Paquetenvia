using Paqueteria.Domain.Tenancy;

namespace Organizations.Application.Session;

public sealed record OrganizationSessionMembership(
    Guid OrganizationId,
    OrganizationRole Role,
    bool IsDefault);

public interface IOrganizationRequestSession
{
    bool IsAuthenticated { get; }

    bool IsActive { get; }

    Guid? UserId { get; }

    bool MfaSatisfied { get; }

    /// <summary>
    /// AUTH-EMAIL-VERIFIED-REQUIRED: the identity provider asserted a verified email for this session.
    /// Fails closed (false) for any session that does not say otherwise.
    /// </summary>
    bool EmailVerified => false;

    IReadOnlyList<OrganizationSessionMembership> ActiveMemberships { get; }

    bool HasOrganizationAccess(Guid organizationId);

    bool HasRole(Guid organizationId, OrganizationRole role);
}
