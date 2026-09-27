using Organizations.Application.Session;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Endpoints.Authorization;

/// <summary>
/// FINANCE-COD-RECONCILIATION (owner decision 2026-09-27): FINANCE reads and reconciles collected COD and
/// never creates or modifies orders. A module endpoint asks this boundary instead of naming roles itself.
/// </summary>
public static class FinanceRoleBoundary
{
    /// <summary>
    /// True when every active membership the actor holds in the organization is FINANCE. An actor who also
    /// holds another active role there keeps that role's capabilities.
    /// </summary>
    public static bool IsFinanceOnly(IOrganizationRequestSession session, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var roles = session.ActiveMemberships
            .Where(membership => membership.OrganizationId == organizationId)
            .Select(membership => membership.Role)
            .ToArray();
        return roles.Length > 0 && roles.All(role => role == OrganizationRole.Finance);
    }
}
