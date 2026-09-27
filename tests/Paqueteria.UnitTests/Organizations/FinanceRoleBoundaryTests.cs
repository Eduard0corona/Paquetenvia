using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Paqueteria.Domain.Tenancy;

namespace Paqueteria.UnitTests.Organizations;

/// <summary>
/// FINANCE-COD-RECONCILIATION: only an actor whose every active role in the selected organization is FINANCE
/// is finance-only; any other active role there keeps its own capabilities.
/// </summary>
public sealed class FinanceRoleBoundaryTests
{
    private static readonly Guid Tenant = Guid.Parse("2a0f3d5e-6c1b-4f0e-9e2a-5d8c7b6a4f01");
    private static readonly Guid Other = Guid.Parse("2a0f3d5e-6c1b-4f0e-9e2a-5d8c7b6a4f02");

    [Fact]
    public void A_finance_only_member_is_finance_only()
    {
        Assert.True(FinanceRoleBoundary.IsFinanceOnly(Session((Tenant, OrganizationRole.Finance)), Tenant));
    }

    [Theory]
    [InlineData(OrganizationRole.Dispatcher)]
    [InlineData(OrganizationRole.PlatformAdmin)]
    [InlineData(OrganizationRole.Viewer)]
    [InlineData(OrganizationRole.Driver)]
    public void Another_active_role_in_the_same_organization_is_not_finance_only(OrganizationRole role)
    {
        Assert.False(FinanceRoleBoundary.IsFinanceOnly(
            Session((Tenant, OrganizationRole.Finance), (Tenant, role)), Tenant));
        Assert.False(FinanceRoleBoundary.IsFinanceOnly(Session((Tenant, role)), Tenant));
    }

    [Fact]
    public void Only_the_selected_organization_counts()
    {
        var session = Session((Tenant, OrganizationRole.Finance), (Other, OrganizationRole.Dispatcher));

        Assert.True(FinanceRoleBoundary.IsFinanceOnly(session, Tenant));
        Assert.False(FinanceRoleBoundary.IsFinanceOnly(session, Other));
        Assert.False(FinanceRoleBoundary.IsFinanceOnly(session, Guid.NewGuid()));
    }

    private static IOrganizationRequestSession Session(params (Guid Organization, OrganizationRole Role)[] memberships) =>
        new FixedSession(memberships.Select(membership =>
            new OrganizationSessionMembership(membership.Organization, membership.Role, false)).ToArray());

    private sealed class FixedSession(IReadOnlyList<OrganizationSessionMembership> memberships) : IOrganizationRequestSession
    {
        public bool IsAuthenticated => true;

        public bool IsActive => true;

        public Guid? UserId => Guid.Parse("2a0f3d5e-6c1b-4f0e-9e2a-5d8c7b6a4f99");

        public bool MfaSatisfied => false;

        public IReadOnlyList<OrganizationSessionMembership> ActiveMemberships => memberships;

        public bool HasOrganizationAccess(Guid organizationId) =>
            memberships.Any(membership => membership.OrganizationId == organizationId);

        public bool HasRole(Guid organizationId, OrganizationRole role) =>
            memberships.Any(membership => membership.OrganizationId == organizationId && membership.Role == role);
    }
}
