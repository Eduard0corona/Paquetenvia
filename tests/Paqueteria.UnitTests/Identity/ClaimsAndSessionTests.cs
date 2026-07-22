using System.Security.Claims;
using Identity.Application.Authentication;
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
        Assert.Equal("mock-subject-multi-org", twice.FindFirst(IdentityClaimTypes.Subject)?.Value);
    }

    [Fact]
    public async Task Session_is_request_snapshot_with_exact_organization_role_queries()
    {
        var principal = await TransformedPrincipalForAsync(MockIdentityProfiles.ActiveMultiOrganization);
        var session = AuthenticatedSession.FromPrincipal(principal);

        Assert.True(session.IsAuthenticated);
        Assert.Equal("mock-subject-multi-org", session.Subject);
        Assert.Equal(NormalizedIdentityStatus.Active, session.IdentityStatus);
        Assert.True(session.MfaSatisfied);
        Assert.True(session.HasOrganizationAccess(MockIdentityProfiles.ViewerOrganizationId));
        Assert.False(session.HasOrganizationAccess(MockIdentityProfiles.ForeignOrganizationId));
        Assert.True(session.HasRole(
            MockIdentityProfiles.ViewerOrganizationId,
            NormalizedOrganizationRole.Viewer));
        Assert.False(session.HasRole(
            MockIdentityProfiles.OperationsOrganizationId,
            NormalizedOrganizationRole.Viewer));
        Assert.True(session.HasRole(
            MockIdentityProfiles.OperationsOrganizationId,
            NormalizedOrganizationRole.Dispatcher));

        var mutableCopy = session.ActiveMemberships.ToList();
        mutableCopy.Clear();
        Assert.Equal(2, session.ActiveMemberships.Count);
    }

    [Fact]
    public void Principal_without_internal_session_is_anonymous()
    {
        var external = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "PLATFORM_ADMIN"), new Claim("amr", "mfa")],
            authenticationType: "External"));

        var session = AuthenticatedSession.FromPrincipal(external);

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Subject);
        Assert.Empty(session.ActiveMemberships);
    }

    [Fact]
    public async Task Revoked_membership_is_preserved_as_claim_but_never_enters_active_session()
    {
        var principal = await TransformedPrincipalForAsync(MockIdentityProfiles.RevokedMembership);
        var session = AuthenticatedSession.FromPrincipal(principal);

        Assert.Single(principal.Claims, claim => claim.Type == IdentityClaimTypes.Membership);
        Assert.Empty(session.ActiveMemberships);
        Assert.False(session.HasOrganizationAccess(MockIdentityProfiles.ViewerOrganizationId));
    }

    [Theory]
    [InlineData("UNKNOWN", "11111111-1111-1111-1111-111111111111|VIEWER|ACTIVE")]
    [InlineData("ACTIVE", "11111111-1111-1111-1111-111111111111|OWNER|ACTIVE")]
    public async Task Unknown_status_or_role_fails_closed(string status, string membership)
    {
        var principal = SourcePrincipal(status, membership);

        var transformed = await _transformation.TransformAsync(principal);

        Assert.False(transformed.Identity?.IsAuthenticated ?? false);
        Assert.False(AuthenticatedSession.FromPrincipal(transformed).IsAuthenticated);
    }

    [Fact]
    public async Task Arbitrary_external_claims_cannot_elevate_a_session()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, "PLATFORM_ADMIN"),
                new Claim(IdentityClaimTypes.Membership, "11111111-1111-1111-1111-111111111111|PLATFORM_ADMIN|ACTIVE"),
                new Claim(IdentityClaimTypes.AuthenticationMethodReference, "mfa"),
            ],
            authenticationType: "Untrusted"));

        var transformed = await _transformation.TransformAsync(principal);
        var session = AuthenticatedSession.FromPrincipal(transformed);

        Assert.False(session.IsAuthenticated);
        Assert.False(session.MfaSatisfied);
        Assert.False(session.HasAnyActiveRole(NormalizedOrganizationRole.PlatformAdmin));
    }

    private async Task<ClaimsPrincipal> PrincipalForAsync(string credential)
    {
        var result = await new MockIdentityProvider().AuthenticateAsync(credential, default);
        Assert.True(IdentityClaimsPrincipalFactory.TryCreate(result.Identity!, out var principal));
        return principal!;
    }

    private async Task<ClaimsPrincipal> TransformedPrincipalForAsync(string credential) =>
        await _transformation.TransformAsync(await PrincipalForAsync(credential));

    private static ClaimsPrincipal SourcePrincipal(string status, string membership)
    {
        Claim Internal(string type, string value) =>
            new(type, value, ClaimValueTypes.String, IdentityClaimTypes.Issuer, IdentityClaimTypes.Issuer);

        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                Internal(IdentityClaimTypes.SourceSubject, "synthetic-subject"),
                Internal(IdentityClaimTypes.SourceStatus, status),
                Internal(IdentityClaimTypes.SourceMfa, "true"),
                Internal(IdentityClaimTypes.SourceMembership, membership),
            ],
            IdentitySecurityDefaults.SourceAuthenticationType));
    }

    private static string ClaimKey(Claim claim) => $"{claim.Type}|{claim.Value}|{claim.Issuer}";
}
