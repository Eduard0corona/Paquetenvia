using Identity.Application.Bootstrap;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Organizations.Application.Session;
using Organizations.Domain;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Organizations.Infrastructure.Persistence;
using Organizations.Application.Provisioning;
using Organizations.Infrastructure.Provisioning;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.UnitTests.Organizations;

public sealed class TenancyContractTests
{
    [Fact]
    public void Database_execution_context_deduplicates_and_sorts_organization_ids()
    {
        var userId = Guid.NewGuid();
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var context = new TenantDatabaseExecutionContext(userId, [second, first, second, Guid.Empty]);

        Assert.Equal(userId, context.UserId);
        Assert.Equal(2, context.OrganizationIds.Length);
        Assert.Equal(first, context.OrganizationIds[0]);
        Assert.Equal(second, context.OrganizationIds[1]);
    }

    [Fact]
    public void Database_execution_context_allows_an_explicit_empty_organization_set()
    {
        var context = new TenantDatabaseExecutionContext(Guid.NewGuid(), []);

        Assert.Empty(context.OrganizationIds);
    }

    [Fact]
    public void Database_execution_context_rejects_null_organization_sets()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TenantDatabaseExecutionContext(Guid.NewGuid(), null!));
    }

    [Fact]
    public void Request_tenant_context_is_immutable_after_selection()
    {
        var context = new RequestTenantContext();
        var organizationId = Guid.NewGuid();

        context.Select(organizationId);

        Assert.True(context.IsSelected);
        Assert.Equal(organizationId, context.OrganizationId);
        Assert.Throws<InvalidOperationException>(() => context.Select(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData(" 00000000-0000-0000-0000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000001 ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{00000000-0000-0000-0000-000000000001}")]
    public void Organization_header_rejects_noncanonical_values(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Organization-Id"] = value;

        Assert.False(TenantContextMiddleware.TryReadOrganizationId(context.Request, out _));
    }

    [Fact]
    public void Organization_header_accepts_one_nonempty_D_format_uuid()
    {
        var context = new DefaultHttpContext();
        var expected = Guid.NewGuid();
        context.Request.Headers["X-Organization-Id"] = expected.ToString("D");

        Assert.True(TenantContextMiddleware.TryReadOrganizationId(context.Request, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Active_role_authorization_is_limited_to_the_selected_organization()
    {
        var selectedOrganization = Guid.NewGuid();
        var otherOrganization = Guid.NewGuid();
        var session = new StubSession(
            selectedOrganization,
            OrganizationRole.Viewer,
            otherOrganization,
            OrganizationRole.PlatformAdmin);
        var tenant = new RequestTenantContext();
        tenant.Select(selectedOrganization);
        var requirement = new ActiveOrganizationRoleRequirement(OrganizationRole.PlatformAdmin, true);
        var authorization = new AuthorizationHandlerContext([requirement], new System.Security.Claims.ClaimsPrincipal(), null);

        await new ActiveOrganizationRoleHandler(session, tenant).HandleAsync(authorization);

        Assert.False(authorization.HasSucceeded);
    }

    [Fact]
    public void Organizations_model_adopts_canonical_tables_and_never_generates_ids()
    {
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=design;Username=design;Password=design")
            .Options;
        using var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState());

        var organization = context.Model.FindEntityType(typeof(Organization))!;
        var membership = context.Model.FindEntityType(typeof(OrganizationMembership))!;
        Assert.Equal("organizations", organization.GetSchema());
        Assert.Equal("organizations", organization.GetTableName());
        Assert.Equal("organization_memberships", membership.GetTableName());
        Assert.Equal(ValueGenerated.Never, organization.FindProperty(nameof(Organization.Id))!.ValueGenerated);
        Assert.Equal(ValueGenerated.Never, membership.FindProperty(nameof(OrganizationMembership.Id))!.ValueGenerated);
        Assert.NotEmpty(organization.GetDeclaredQueryFilters());
        Assert.NotEmpty(membership.GetDeclaredQueryFilters());
    }

    [Fact]
    public async Task SaveChanges_outside_a_tenant_transaction_is_rejected_before_database_access()
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unreachable;Username=none;Password=none;Timeout=1")
            .AddInterceptors(new TenantSaveChangesGuardInterceptor(state))
            .Options;
        await using var context = new OrganizationsDbContext(options, state);
        context.Organizations.Add(new Organization(
            Guid.NewGuid(), "Legal", "Display", OrganizationType.Business, OrganizationStatus.Active, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<TenantTransactionRequiredException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Initial_provisioning_authorization_is_resolved_before_any_database_access()
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unreachable;Username=none;Password=none;Timeout=1;Pooling=false")
            .Options;
        await using var context = new OrganizationsDbContext(options, state);
        var provisioner = new PostgreSqlInitialOrganizationProvisioner(
            new DenyInitialOrganizationProvisioningAuthorizer(),
            new NoOpProvisioningFailureInjector(),
            new TenantTransactionContext<OrganizationsDbContext>(context, state));

        await Assert.ThrowsAsync<InitialOrganizationProvisioningForbiddenException>(() => provisioner.ProvisionAsync(
            new InitialOrganizationProvisioningCommand(
                "subject", "Legal", "Display", OrganizationType.Business,
                OrganizationRole.BusinessAdmin, null),
            CancellationToken.None));
    }

    private sealed class StubSession : IOrganizationRequestSession
    {
        public StubSession(Guid firstOrg, OrganizationRole firstRole, Guid secondOrg, OrganizationRole secondRole)
        {
            UserId = Guid.NewGuid();
            ActiveMemberships =
            [
                new OrganizationSessionMembership(firstOrg, firstRole, true),
                new OrganizationSessionMembership(secondOrg, secondRole, false),
            ];
        }

        public bool IsAuthenticated => true;
        public bool IsActive => true;
        public Guid? UserId { get; }
        public bool MfaSatisfied => true;
        public IReadOnlyList<OrganizationSessionMembership> ActiveMemberships { get; }
        public bool HasOrganizationAccess(Guid organizationId) =>
            ActiveMemberships.Any(membership => membership.OrganizationId == organizationId);
        public bool HasRole(Guid organizationId, OrganizationRole role) =>
            ActiveMemberships.Any(membership => membership.OrganizationId == organizationId && membership.Role == role);
    }
}
