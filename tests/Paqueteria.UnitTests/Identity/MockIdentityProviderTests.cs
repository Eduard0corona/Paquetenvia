using Identity.Application.Authentication;
using Identity.Infrastructure.Mock;

namespace Paqueteria.UnitTests.Identity;

public sealed class MockIdentityProviderTests
{
    private readonly MockIdentityProvider _provider = new();

    [Fact]
    public async Task Known_token_selects_the_prebuilt_active_profile()
    {
        var result = await _provider.AuthenticateAsync(MockIdentityProfiles.ActiveViewer, default);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Identity);
        Assert.Equal(NormalizedIdentityStatus.Active, result.Identity.Status);
        Assert.False(result.Identity.MfaSatisfied);
        Assert.Single(result.Identity.Memberships);
    }

    [Fact]
    public async Task Unknown_token_is_invalid_without_throwing()
    {
        var result = await _provider.AuthenticateAsync("unknown-profile", default);

        Assert.False(result.IsValid);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Cancellation_is_honored_before_profile_resolution()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _provider.AuthenticateAsync(MockIdentityProfiles.ActiveViewer, source.Token));
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedUser, NormalizedIdentityStatus.Suspended)]
    [InlineData(MockIdentityProfiles.DisabledUser, NormalizedIdentityStatus.Disabled)]
    public async Task Inactive_identity_profiles_retain_their_normative_status(
        string credential,
        NormalizedIdentityStatus expectedStatus)
    {
        var result = await _provider.AuthenticateAsync(credential, default);

        Assert.True(result.IsValid);
        Assert.Equal(expectedStatus, result.Identity?.Status);
    }

    [Fact]
    public async Task Multi_organization_profile_keeps_roles_bound_to_each_organization()
    {
        var result = await _provider.AuthenticateAsync(MockIdentityProfiles.ActiveMultiOrganization, default);

        Assert.Collection(
            result.Identity!.Memberships,
            membership =>
            {
                Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, membership.OrganizationId);
                Assert.Equal(NormalizedOrganizationRole.Viewer, membership.Role);
            },
            membership =>
            {
                Assert.Equal(MockIdentityProfiles.OperationsOrganizationId, membership.OrganizationId);
                Assert.Equal(NormalizedOrganizationRole.Dispatcher, membership.Role);
            });
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedMembership, NormalizedMembershipStatus.Suspended)]
    [InlineData(MockIdentityProfiles.RevokedMembership, NormalizedMembershipStatus.Revoked)]
    public async Task Inactive_membership_profiles_are_not_rewritten(
        string credential,
        NormalizedMembershipStatus expectedStatus)
    {
        var result = await _provider.AuthenticateAsync(credential, default);

        Assert.Equal(expectedStatus, Assert.Single(result.Identity!.Memberships).Status);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, true)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, false)]
    public async Task Mfa_evidence_is_deterministic(string credential, bool expectedMfa)
    {
        var result = await _provider.AuthenticateAsync(credential, default);

        Assert.Equal(expectedMfa, result.Identity!.MfaSatisfied);
    }
}
