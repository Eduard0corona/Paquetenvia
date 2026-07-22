using System.Security.Claims;
using Identity.Application.Bootstrap;
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
        var context = Context(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "external")], "Untrusted")), requirement);
        await new AuthenticatedIdentityHandler().HandleAsync(context);
        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, true)]
    [InlineData(MockIdentityProfiles.SuspendedUser, false)]
    [InlineData(MockIdentityProfiles.DisabledUser, false)]
    public async Task Active_identity_requires_resolved_active_context(string credential, bool expected)
    {
        var requirement = new ActiveIdentityRequirement();
        var context = Context(await PrincipalForAsync(credential), requirement);
        await new ActiveIdentityHandler().HandleAsync(context);
        Assert.Equal(expected, context.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, false)]
    public async Task Require_mfa_uses_external_amr_evidence(string credential, bool expected)
    {
        var requirement = new RequireMfaRequirement();
        var context = Context(await PrincipalForAsync(credential), requirement);
        await new RequireMfaHandler().HandleAsync(context);
        Assert.Equal(expected, context.HasSucceeded);
    }

    [Fact]
    public async Task Organization_membership_requires_role_in_exact_organization()
    {
        var principal = await PrincipalForAsync(MockIdentityProfiles.ActiveMultiOrganization);
        var requirement = new OrganizationMembershipRequirement(OrganizationRole.Viewer);
        var allowed = Context(principal, requirement,
            new OrganizationAuthorizationResource(MockIdentityProfiles.ViewerOrganizationId));
        var wrong = Context(principal, requirement,
            new OrganizationAuthorizationResource(MockIdentityProfiles.OperationsOrganizationId));

        await new OrganizationMembershipHandler().HandleAsync(allowed);
        await new OrganizationMembershipHandler().HandleAsync(wrong);

        Assert.True(allowed.HasSucceeded);
        Assert.False(wrong.HasSucceeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedMembership)]
    [InlineData(MockIdentityProfiles.RevokedMembership)]
    public async Task Inactive_memberships_are_not_returned_by_resolver(string credential)
    {
        var requirement = new OrganizationMembershipRequirement(OrganizationRole.Viewer);
        var context = Context(await PrincipalForAsync(credential), requirement,
            MockIdentityProfiles.ViewerOrganizationId);
        await new OrganizationMembershipHandler().HandleAsync(context);
        Assert.False(context.HasSucceeded);
    }

    private static AuthorizationHandlerContext Context(
        ClaimsPrincipal principal,
        IAuthorizationRequirement requirement,
        object? resource = null) => new([requirement], principal, resource);

    private static async Task<ClaimsPrincipal> PrincipalForAsync(string credential)
    {
        var authentication = await new MockIdentityProvider().AuthenticateAsync(credential, default);
        var resolution = await new MockIdentityContextResolver().ResolveAsync(authentication.Identity!.Subject, default);
        Assert.True(IdentityClaimsPrincipalFactory.TryCreate(authentication.Identity, resolution, out var source));
        return await new PaquetenviaClaimsTransformation().TransformAsync(source!);
    }
}
