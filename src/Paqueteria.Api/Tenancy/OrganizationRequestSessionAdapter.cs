using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Organizations.Application.Session;

namespace Paqueteria.Api.Tenancy;

public sealed class OrganizationRequestSessionAdapter(IAuthenticatedSession session)
    : IOrganizationRequestSession
{
    public bool IsAuthenticated => session.IsAuthenticated;

    public bool IsActive => session.IdentityStatus == IdentityContextStatus.Active;

    public Guid? UserId => session.UserId;

    public bool MfaSatisfied => session.MfaSatisfied;

    public IReadOnlyList<OrganizationSessionMembership> ActiveMemberships => session.ActiveMemberships
        .Select(membership => new OrganizationSessionMembership(
            membership.OrganizationId,
            membership.Role,
            membership.IsDefault))
        .ToArray();

    public bool HasOrganizationAccess(Guid organizationId) => session.HasOrganizationAccess(organizationId);

    public bool HasRole(Guid organizationId, Paqueteria.Domain.Tenancy.OrganizationRole role) =>
        session.HasRole(organizationId, role);
}
