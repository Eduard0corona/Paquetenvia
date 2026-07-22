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

    IReadOnlyList<OrganizationSessionMembership> ActiveMemberships { get; }

    bool HasOrganizationAccess(Guid organizationId);

    bool HasRole(Guid organizationId, OrganizationRole role);
}
