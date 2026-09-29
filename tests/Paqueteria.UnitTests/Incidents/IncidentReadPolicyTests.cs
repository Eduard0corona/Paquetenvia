using Custody.Application.ProofUploads;
using Incidents.Application.Incidents;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Paqueteria.Domain.Tenancy;

namespace Paqueteria.UnitTests.Incidents;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29: the capability, shape and cursor rules of listIncidents, getIncident and
/// listOrderProofs.
/// </summary>
public sealed class IncidentReadPolicyTests
{
    private static readonly Guid Tenant = Guid.Parse("5d5c0a11-0000-4000-8000-0000000000a1");
    private static readonly Guid Actor = Guid.Parse("5d5c0a11-0000-4000-8000-0000000000a2");
    private static readonly Guid Order = Guid.Parse("5d5c0a11-0000-4000-8000-0000000000a3");
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 29, 15, 4, 5, TimeSpan.Zero);

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    public void Reads_admit_exactly_the_resolution_roles(
        bool dispatcher,
        bool platformAdmin,
        bool mfa,
        bool expected)
    {
        Assert.Equal(expected, IncidentReadPolicy.MayRead(dispatcher, platformAdmin, mfa));
        Assert.Equal(expected, ProofReadPolicy.MayRead(dispatcher, platformAdmin, mfa));
    }

    public static TheoryData<OrganizationRole> RolesThatNeverRead => new()
    {
        OrganizationRole.Driver,
        OrganizationRole.Viewer,
        OrganizationRole.Finance,
        OrganizationRole.AllyAdmin,
        OrganizationRole.AllyOperator,
        OrganizationRole.BusinessAdmin,
        OrganizationRole.BusinessOperator,
    };

    [Theory]
    [MemberData(nameof(RolesThatNeverRead))]
    public void Every_other_role_is_forbidden_on_the_reads_with_or_without_mfa(OrganizationRole role)
    {
        foreach (var capability in new[]
                 {
                     TenantCapabilities.ListIncidents, TenantCapabilities.GetIncident, TenantCapabilities.ListOrderProofs,
                 })
        {
            foreach (var mfa in new[] { false, true })
            {
                Assert.Equal(TenantCapabilityDecision.Forbidden, capability.Evaluate(Session(mfa, role), Tenant));
            }
        }
    }

    [Fact]
    public void A_platform_admin_needs_mfa_and_a_dispatcher_never_does()
    {
        foreach (var capability in new[]
                 {
                     TenantCapabilities.ListIncidents, TenantCapabilities.GetIncident, TenantCapabilities.ListOrderProofs,
                 })
        {
            Assert.Equal(
                TenantCapabilityDecision.MfaRequired,
                capability.Evaluate(Session(false, OrganizationRole.PlatformAdmin), Tenant));
            Assert.Equal(
                TenantCapabilityDecision.Allowed,
                capability.Evaluate(Session(true, OrganizationRole.PlatformAdmin), Tenant));
            Assert.Equal(
                TenantCapabilityDecision.Allowed,
                capability.Evaluate(Session(false, OrganizationRole.Dispatcher), Tenant));
        }
    }

    [Fact]
    public void A_driver_may_open_but_never_read_or_resolve()
    {
        var driver = Session(false, OrganizationRole.Driver);
        Assert.Equal(TenantCapabilityDecision.Allowed, TenantCapabilities.OpenIncident.Evaluate(driver, Tenant));
        Assert.Equal(TenantCapabilityDecision.Forbidden, TenantCapabilities.ResolveIncident.Evaluate(driver, Tenant));
        Assert.Equal(TenantCapabilityDecision.Forbidden, TenantCapabilities.ListIncidents.Evaluate(driver, Tenant));
        Assert.Equal(TenantCapabilityDecision.Forbidden, TenantCapabilities.ListOrderProofs.Evaluate(driver, Tenant));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("OPEN", true)]
    [InlineData("INVESTIGATING", true)]
    [InlineData("RESOLVED", true)]
    [InlineData("REJECTED", true)]
    [InlineData("open", false)]
    [InlineData("", false)]
    [InlineData("CLOSED", false)]
    public void The_status_filter_is_exactly_the_incident_vocabulary(string? status, bool expected) =>
        Assert.Equal(expected, IncidentReadPolicy.IsValidStatusFilter(status));

    [Fact]
    public void List_shape_rejects_empty_identifiers()
    {
        var valid = new ListIncidentsQuery(Actor, Tenant, false, null, null, null);
        Assert.True(IncidentReadPolicy.IsValidShape(valid));
        Assert.False(IncidentReadPolicy.IsValidShape(valid with { ActorId = Guid.Empty }));
        Assert.False(IncidentReadPolicy.IsValidShape(valid with { OrganizationId = Guid.Empty }));
        Assert.False(IncidentReadPolicy.IsValidShape(valid with { OrderId = Guid.Empty }));
        Assert.False(IncidentReadPolicy.IsValidShape(valid with { Status = "closed" }));
        Assert.True(IncidentReadPolicy.IsValidShape(valid with { OrderId = Order, Status = "OPEN" }));
        Assert.False(IncidentReadPolicy.IsValidShape(new GetIncidentQuery(Actor, Tenant, false, Guid.Empty)));
    }

    [Fact]
    public void An_incident_cursor_round_trips_exactly()
    {
        var cursor = new IncidentCursor(CreatedAt.AddTicks(1234560), Guid.NewGuid());
        var encoded = IncidentCursorCodec.Encode(cursor);

        Assert.True(encoded.Length <= IncidentCursorCodec.MaximumLength);
        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
        Assert.True(IncidentCursorCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("AAAA")]
    [InlineData("a+b/")]
    [InlineData("a")]
    public void A_malformed_incident_cursor_never_decodes(string? value) =>
        Assert.False(IncidentCursorCodec.TryDecode(value, out _));

    [Fact]
    public void A_cursor_of_another_list_operation_never_decodes()
    {
        var proofCursor = ProofCursorCodec.Encode(new ProofCursor(Order, CreatedAt, Guid.NewGuid()));
        Assert.False(IncidentCursorCodec.TryDecode(proofCursor, out _));

        var orderCursor = global::Orders.Application.Orders.OrderCursorCodec.Encode(CreatedAt, Guid.NewGuid());
        Assert.False(IncidentCursorCodec.TryDecode(orderCursor, out _));

        var incidentCursor = IncidentCursorCodec.Encode(new IncidentCursor(CreatedAt, Guid.NewGuid()));
        Assert.False(ProofCursorCodec.TryDecode(incidentCursor, Order, out _));
    }

    [Fact]
    public void A_non_canonical_or_oversized_cursor_never_decodes()
    {
        var encoded = IncidentCursorCodec.Encode(new IncidentCursor(CreatedAt, Guid.NewGuid()));
        Assert.False(IncidentCursorCodec.TryDecode(encoded + "=", out _));
        Assert.False(IncidentCursorCodec.TryDecode(new string('A', IncidentCursorCodec.MaximumLength + 4), out _));
    }

    [Fact]
    public void A_proof_cursor_is_bound_to_its_order()
    {
        var cursor = new ProofCursor(Order, CreatedAt.AddTicks(10), Guid.NewGuid());
        var encoded = ProofCursorCodec.Encode(cursor);

        Assert.True(encoded.Length <= ProofCursorCodec.MaximumLength);
        Assert.True(ProofCursorCodec.TryDecode(encoded, Order, out var decoded));
        Assert.Equal(cursor, decoded);
        Assert.False(ProofCursorCodec.TryDecode(encoded, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Proof_list_shape_rejects_a_cursor_for_another_order()
    {
        var valid = new ListOrderProofsQuery(Actor, Tenant, false, Order, null);
        Assert.True(ProofReadPolicy.IsValidShape(valid));
        Assert.True(ProofReadPolicy.IsValidShape(valid with { Cursor = new ProofCursor(Order, CreatedAt, Guid.NewGuid()) }));
        Assert.False(ProofReadPolicy.IsValidShape(valid with
        {
            Cursor = new ProofCursor(Guid.NewGuid(), CreatedAt, Guid.NewGuid()),
        }));
        Assert.False(ProofReadPolicy.IsValidShape(valid with { OrderId = Guid.Empty }));
        Assert.False(ProofReadPolicy.IsValidShape(valid with { ActorId = Guid.Empty }));
    }

    private static IOrganizationRequestSession Session(bool mfa, OrganizationRole role) =>
        new TestSession(mfa, [new OrganizationSessionMembership(Tenant, role, false)]);

    private sealed class TestSession(bool mfa, IReadOnlyList<OrganizationSessionMembership> memberships)
        : IOrganizationRequestSession
    {
        public bool IsAuthenticated => true;

        public bool IsActive => true;

        public Guid? UserId => Actor;

        public bool MfaSatisfied => mfa;

        public IReadOnlyList<OrganizationSessionMembership> ActiveMemberships => memberships;

        public bool HasOrganizationAccess(Guid organizationId) =>
            memberships.Any(membership => membership.OrganizationId == organizationId);

        public bool HasRole(Guid organizationId, OrganizationRole role) =>
            memberships.Any(membership => membership.OrganizationId == organizationId && membership.Role == role);
    }
}
