using System.Security.Claims;
using Identity.Application.Bootstrap;
using Identity.Endpoints.Security;
using Identity.Endpoints.Session;
using Identity.Infrastructure.Mock;

namespace Paqueteria.UnitTests.Identity;

public sealed class ClaimsAndSessionTests
{
    private readonly PaquetenviaClaimsTransformation _transformation = new();

    [Fact]
    public async Task Transformation_is_idempotent_and_does_not_create_global_roles()
    {
        var principal = await PrincipalForAsync(MockIdentityProfiles.ActiveMultiOrganization);
        var once = await _transformation.TransformAsync(principal);
        var twice = await _transformation.TransformAsync(once);

        Assert.Equal(
            once.Claims.Select(ClaimKey).Order(StringComparer.Ordinal),
            twice.Claims.Select(ClaimKey).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(twice.Claims, claim => claim.Type == ClaimTypes.Role);
        Assert.Equal(2, twice.Claims.Count(claim => claim.Type == IdentityClaimTypes.Membership));
    }

    [Fact]
    public async Task Session_preserves_exact_organization_roles_and_default_flag()
    {
        var session = AuthenticatedSession.FromPrincipal(
            await TransformedPrincipalForAsync(MockIdentityProfiles.ActiveMultiOrganization));

        Assert.True(session.IsAuthenticated);
        Assert.Equal(IdentityContextStatus.Active, session.IdentityStatus);
        Assert.True(session.MfaSatisfied);
        Assert.True(session.HasRole(MockIdentityProfiles.ViewerOrganizationId, OrganizationRole.Viewer));
        Assert.False(session.HasRole(MockIdentityProfiles.OperationsOrganizationId, OrganizationRole.Viewer));
        Assert.True(session.HasRole(MockIdentityProfiles.OperationsOrganizationId, OrganizationRole.Dispatcher));
        Assert.True(session.ActiveMemberships.Single(x => x.OrganizationId == MockIdentityProfiles.ViewerOrganizationId).IsDefault);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedUser)]
    [InlineData(MockIdentityProfiles.DisabledUser)]
    public async Task Unresolved_subject_remains_externally_authenticated_without_internal_claims(string credential)
    {
        var transformed = await TransformedPrincipalForAsync(credential);
        var session = AuthenticatedSession.FromPrincipal(transformed);

        Assert.True(session.IsAuthenticated);
        Assert.NotNull(session.Subject);
        Assert.Null(session.IdentityStatus);
        Assert.Empty(session.ActiveMemberships);
    }

    [Fact]
    public void Principal_without_internal_source_is_anonymous()
    {
        var external = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "PLATFORM_ADMIN"), new Claim("amr", "mfa")],
            authenticationType: "External"));

        Assert.False(AuthenticatedSession.FromPrincipal(external).IsAuthenticated);
    }

    [Fact]
    public async Task Arbitrary_external_claims_cannot_elevate_a_session()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, "PLATFORM_ADMIN"),
                new Claim(IdentityClaimTypes.Membership, "11111111-1111-1111-1111-111111111111|PLATFORM_ADMIN|true"),
                new Claim(IdentityClaimTypes.AuthenticationMethodReference, "mfa"),
            ],
            authenticationType: "Untrusted"));

        var session = AuthenticatedSession.FromPrincipal(await _transformation.TransformAsync(principal));
        Assert.False(session.IsAuthenticated);
        Assert.False(session.HasAnyActiveRole(OrganizationRole.PlatformAdmin));
    }

    private static async Task<ClaimsPrincipal> PrincipalForAsync(string credential)
    {
        var authentication = await new MockIdentityProvider().AuthenticateAsync(credential, default);
        var resolution = await new MockIdentityContextResolver().ResolveAsync(authentication.Identity!.Subject, default);
        Assert.True(IdentityClaimsPrincipalFactory.TryCreate(authentication.Identity, resolution, out var principal));
        return principal!;
    }

    private async Task<ClaimsPrincipal> TransformedPrincipalForAsync(string credential) =>
        await _transformation.TransformAsync(await PrincipalForAsync(credential));

    private static string ClaimKey(Claim claim) => $"{claim.Type}|{claim.Value}|{claim.Issuer}";
}
