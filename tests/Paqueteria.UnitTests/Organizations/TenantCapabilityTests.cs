using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Paqueteria.Domain.Tenancy;

namespace Paqueteria.UnitTests.Organizations;

/// <summary>
/// D5-CAPABILITY-MATRIX decision semantics: any active role the actor holds in the selected organization that
/// the operation admits decides; MFA_REQUIRED only when every such role needs a second factor the session lacks.
/// </summary>
public sealed class TenantCapabilityTests
{
    private static readonly Guid Tenant = Guid.Parse("5d5c0a11-0000-4000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("5d5c0a11-0000-4000-8000-000000000002");

    public static TheoryData<OrganizationRole> RolesOutsideOrderCreation => new()
    {
        OrganizationRole.Finance,
        OrganizationRole.Driver,
        OrganizationRole.Viewer,
        OrganizationRole.AllyAdmin,
        OrganizationRole.AllyOperator,
        OrganizationRole.BusinessAdmin,
        OrganizationRole.BusinessOperator,
    };

    [Theory]
    [MemberData(nameof(RolesOutsideOrderCreation))]
    public void Unlisted_roles_are_forbidden_with_or_without_mfa(OrganizationRole role)
    {
        foreach (var mfa in new[] { false, true })
        {
            Assert.Equal(
                TenantCapabilityDecision.Forbidden,
                TenantCapabilities.CreateOrder.Evaluate(Session(mfa, (Tenant, role)), Tenant));
        }
    }

    [Theory]
    [InlineData(OrganizationRole.Dispatcher)]
    [InlineData(OrganizationRole.PlatformAdmin)]
    public void Listed_roles_are_allowed_without_mfa_where_the_matrix_never_demanded_it(OrganizationRole role)
    {
        Assert.Equal(
            TenantCapabilityDecision.Allowed,
            TenantCapabilities.CreateOrder.Evaluate(Session(false, (Tenant, role)), Tenant));
    }

    [Theory]
    [InlineData(OrganizationRole.PlatformAdmin)]
    [InlineData(OrganizationRole.Finance)]
    public void A_role_that_needs_mfa_is_mfa_required_without_it_and_allowed_with_it(OrganizationRole role)
    {
        Assert.Equal(
            TenantCapabilityDecision.MfaRequired,
            TenantCapabilities.ReconcileCod.Evaluate(Session(false, (Tenant, role)), Tenant));
        Assert.Equal(
            TenantCapabilityDecision.Allowed,
            TenantCapabilities.ReconcileCod.Evaluate(Session(true, (Tenant, role)), Tenant));
    }

    [Fact]
    public void Dispatcher_never_needs_mfa()
    {
        foreach (var capability in TenantCapabilities.All.Values)
        {
            Assert.All(
                capability.Grants.Where(grant => grant.Role == OrganizationRole.Dispatcher),
                grant => Assert.False(grant.RequiresMfa));
        }
    }

    [Fact]
    public void Settlements_admit_finance_without_mfa_and_platform_admin_only_with_it()
    {
        foreach (var capability in new[]
                 {
                     TenantCapabilities.CreateSettlement, TenantCapabilities.GetSettlement,
                     TenantCapabilities.ListSettlements, TenantCapabilities.AddSettlementAdjustment,
                     TenantCapabilities.VoidSettlement, TenantCapabilities.ExportSettlementCsv,
                 })
        {
            Assert.Equal(
                TenantCapabilityDecision.Allowed,
                capability.Evaluate(Session(false, (Tenant, OrganizationRole.Finance)), Tenant));
            Assert.Equal(
                TenantCapabilityDecision.MfaRequired,
                capability.Evaluate(Session(false, (Tenant, OrganizationRole.PlatformAdmin)), Tenant));
            Assert.Equal(
                TenantCapabilityDecision.Forbidden,
                capability.Evaluate(Session(true, (Tenant, OrganizationRole.Dispatcher)), Tenant));
        }
    }

    /// <summary>D7-SETTLEMENT-MFA: approving and paying need MFA for every permitted role, FINANCE included.</summary>
    [Fact]
    public void Approve_and_pay_require_mfa_for_finance_and_platform_admin()
    {
        foreach (var capability in new[] { TenantCapabilities.ApproveSettlement, TenantCapabilities.MarkSettlementPaid })
        {
            foreach (var role in new[] { OrganizationRole.Finance, OrganizationRole.PlatformAdmin })
            {
                Assert.Equal(TenantCapabilityDecision.MfaRequired, capability.Evaluate(Session(false, (Tenant, role)), Tenant));
                Assert.Equal(TenantCapabilityDecision.Allowed, capability.Evaluate(Session(true, (Tenant, role)), Tenant));
            }

            Assert.Equal(
                TenantCapabilityDecision.Forbidden,
                capability.Evaluate(Session(true, (Tenant, OrganizationRole.Dispatcher)), Tenant));
        }
    }

    [Fact]
    public void Another_granted_role_without_mfa_needs_no_second_factor()
    {
        var session = Session(false, (Tenant, OrganizationRole.PlatformAdmin), (Tenant, OrganizationRole.Dispatcher));

        Assert.Equal(TenantCapabilityDecision.Allowed, TenantCapabilities.AssignDriver.Evaluate(session, Tenant));
    }

    [Fact]
    public void A_viewer_holding_a_platform_admin_membership_is_told_only_mfa_is_missing()
    {
        var session = Session(false, (Tenant, OrganizationRole.Viewer), (Tenant, OrganizationRole.PlatformAdmin));

        Assert.Equal(TenantCapabilityDecision.MfaRequired, TenantCapabilities.AssignDriver.Evaluate(session, Tenant));
    }

    [Fact]
    public void Only_the_selected_organization_counts()
    {
        var session = Session(true, (Tenant, OrganizationRole.Viewer), (Other, OrganizationRole.Dispatcher));

        Assert.Equal(TenantCapabilityDecision.Forbidden, TenantCapabilities.CreateQuote.Evaluate(session, Tenant));
        Assert.Equal(TenantCapabilityDecision.Allowed, TenantCapabilities.CreateQuote.Evaluate(session, Other));
        Assert.Equal(TenantCapabilityDecision.Forbidden, TenantCapabilities.CreateQuote.Evaluate(session, Guid.NewGuid()));
    }

    [Fact]
    public void An_inactive_or_anonymous_session_is_forbidden_even_with_a_granted_role()
    {
        Assert.Equal(
            TenantCapabilityDecision.Forbidden,
            TenantCapabilities.GetOrder.Evaluate(
                Session(true, (Tenant, OrganizationRole.Dispatcher)) with { Active = false }, Tenant));
        Assert.Equal(
            TenantCapabilityDecision.Forbidden,
            TenantCapabilities.GetOrder.Evaluate(
                Session(true, (Tenant, OrganizationRole.Dispatcher)) with { Authenticated = false }, Tenant));
    }

    [Fact]
    public void Viewer_is_withheld_from_exact_location_coordinates_and_admitted_to_every_other_read()
    {
        var viewer = Session(false, (Tenant, OrganizationRole.Viewer));

        Assert.Equal(TenantCapabilityDecision.Forbidden, TenantCapabilities.ListLocations.Evaluate(viewer, Tenant));
        foreach (var read in new[]
                 {
                     TenantCapabilities.GetQuote, TenantCapabilities.ListOrders, TenantCapabilities.GetOrder,
                     TenantCapabilities.ListCities, TenantCapabilities.ListServiceAreas,
                     TenantCapabilities.ListOperatingZones,
                 })
        {
            Assert.Equal(TenantCapabilityDecision.Allowed, read.Evaluate(viewer, Tenant));
        }
    }

    [Fact]
    public void A_capability_admits_at_least_one_role_each_once()
    {
        Assert.All(TenantCapabilities.All.Values, capability =>
        {
            Assert.NotEmpty(capability.Grants);
            Assert.Equal(capability.Grants.Count, capability.Grants.Select(grant => grant.Role).Distinct().Count());
        });
        Assert.Equal(TenantCapabilities.All.Count, TenantCapabilities.All.Values.Select(c => c.OperationId).Distinct().Count());
    }

    private static FixedSession Session(bool mfa, params (Guid Organization, OrganizationRole Role)[] memberships) =>
        new(true, true, mfa, memberships
            .Select(membership => new OrganizationSessionMembership(membership.Organization, membership.Role, false))
            .ToArray());

    private sealed record FixedSession(
        bool Authenticated,
        bool Active,
        bool Mfa,
        IReadOnlyList<OrganizationSessionMembership> Memberships) : IOrganizationRequestSession
    {
        public bool IsAuthenticated => Authenticated;

        public bool IsActive => Active;

        public Guid? UserId => Guid.Parse("5d5c0a11-0000-4000-8000-0000000000ff");

        public bool MfaSatisfied => Mfa;

        public IReadOnlyList<OrganizationSessionMembership> ActiveMemberships => Memberships;

        public bool HasOrganizationAccess(Guid organizationId) =>
            Memberships.Any(membership => membership.OrganizationId == organizationId);

        public bool HasRole(Guid organizationId, OrganizationRole role) =>
            Memberships.Any(membership => membership.OrganizationId == organizationId && membership.Role == role);
    }
}
