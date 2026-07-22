using System.Security.Claims;
using Identity.Application.Authentication;
using Identity.Endpoints.Authorization;
using Identity.Endpoints.Security;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Authorization;

namespace Paqueteria.UnitTests.Identity;

public sealed class AuthorizationPolicyTests
{
    [Fact]
    public async Task Authenticated_requirement_rejects_an_untrusted_authenticated_principal()
    {
        var requirement = new AuthenticatedIdentityRequirement();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "external")],
            authenticationType: "Untrusted"));
        var context = Context(principal, requirement);

        await new AuthenticatedIdentityHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, true)]
    [InlineData(MockIdentityProfiles.SuspendedUser, false)]
    [InlineData(MockIdentityProfiles.DisabledUser, false)]
    public async Task Active_identity_requirement_uses_normative_identity_status(
        string credential,
        bool expected)
    {
        var requirement = new ActiveIdentityRequirement();
        var context = Context(await PrincipalForAsync(credential), requirement);

        await new ActiveIdentityHandler().HandleAsync(context);

        Assert.Equal(expected, context.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, false)]
    public async Task Require_mfa_uses_authenticated_amr_evidence(string credential, bool expected)
    {
        var requirement = new RequireMfaRequirement();
        var context = Context(await PrincipalForAsync(credential), requirement);

        await new RequireMfaHandler().HandleAsync(context);

        Assert.Equal(expected, context.HasSucceeded);
    }

    [Fact]
    public async Task Organization_membership_requires_role_in_the_exact_resource_organization()
    {
        var principal = await PrincipalForAsync(MockIdentityProfiles.ActiveMultiOrganization);
        var requirement = new OrganizationMembershipRequirement(NormalizedOrganizationRole.Viewer);

        var allowed = Context(
            principal,
            requirement,
            new OrganizationAuthorizationResource(MockIdentityProfiles.ViewerOrganizationId));
        await new OrganizationMembershipHandler().HandleAsync(allowed);

        var wrongOrganization = Context(
            principal,
            requirement,
            new OrganizationAuthorizationResource(MockIdentityProfiles.OperationsOrganizationId));
        await new OrganizationMembershipHandler().HandleAsync(wrongOrganization);

        Assert.True(allowed.HasSucceeded);
        Assert.False(wrongOrganization.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedMembership)]
    [InlineData(MockIdentityProfiles.RevokedMembership)]
    public async Task Inactive_membership_never_satisfies_resource_authorization(string credential)
    {
        var requirement = new OrganizationMembershipRequirement(NormalizedOrganizationRole.Viewer);
        var context = Context(
            await PrincipalForAsync(credential),
            requirement,
            MockIdentityProfiles.ViewerOrganizationId);

        await new OrganizationMembershipHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, false)]
    [InlineData(MockIdentityProfiles.ActiveViewer, false)]
    public async Task Privileged_operation_requires_active_platform_admin_and_mfa(
        string credential,
        bool expected)
    {
        IAuthorizationRequirement[] requirements =
        [
            new ActiveIdentityRequirement(),
            new AnyActiveOrganizationRoleRequirement(NormalizedOrganizationRole.PlatformAdmin),
            new RequireMfaRequirement(),
        ];
        var context = new AuthorizationHandlerContext(
            requirements,
            await PrincipalForAsync(credential),
            resource: null);

        await new ActiveIdentityHandler().HandleAsync(context);
        await new AnyActiveOrganizationRoleHandler().HandleAsync(context);
        await new RequireMfaHandler().HandleAsync(context);

        Assert.Equal(expected, context.HasSucceeded);
    }

    private static AuthorizationHandlerContext Context(
        ClaimsPrincipal principal,
        IAuthorizationRequirement requirement,
        object? resource = null) =>
        new([requirement], principal, resource);

    private static async Task<ClaimsPrincipal> PrincipalForAsync(string credential)
    {
        var result = await new MockIdentityProvider().AuthenticateAsync(credential, default);
        Assert.True(IdentityClaimsPrincipalFactory.TryCreate(result.Identity!, out var source));
        return await new PaquetenviaClaimsTransformation().TransformAsync(source!);
    }
}
