using Identity.Application.Bootstrap;
using Identity.Infrastructure.Mock;

namespace Paqueteria.UnitTests.Identity;

public sealed class MockIdentityProviderTests
{
    private readonly MockIdentityProvider _provider = new();
    private readonly MockIdentityContextResolver _resolver = new();

    [Fact]
    public async Task Known_token_authenticates_only_subject_and_mfa()
    {
        var result = await _provider.AuthenticateAsync(MockIdentityProfiles.ActiveViewer, default);

        Assert.True(result.IsValid);
        Assert.Equal("mock-subject-active-viewer", result.Identity?.Subject);
        Assert.False(result.Identity?.MfaSatisfied);
        Assert.Equal(2, result.Identity!.GetType().GetProperties().Length);
    }

    [Fact]
    public async Task Unknown_token_is_invalid_without_throwing()
    {
        var result = await _provider.AuthenticateAsync("unknown-profile", default);

        Assert.False(result.IsValid);
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedUser)]
    [InlineData(MockIdentityProfiles.DisabledUser)]
    public async Task External_provider_cannot_distinguish_internal_inactive_states(string credential)
    {
        var authentication = await _provider.AuthenticateAsync(credential, default);
        var resolution = await _resolver.ResolveAsync(authentication.Identity!.Subject, default);

        Assert.True(authentication.IsValid);
        Assert.False(resolution.IsResolved);
    }

    [Fact]
    public async Task Resolver_keeps_multi_organization_roles_and_default_membership()
    {
        var authentication = await _provider.AuthenticateAsync(MockIdentityProfiles.ActiveMultiOrganization, default);
        var resolution = await _resolver.ResolveAsync(authentication.Identity!.Subject, default);

        Assert.Collection(
            resolution.Context!.Memberships,
            membership =>
            {
                Assert.Equal(MockIdentityProfiles.ViewerOrganizationId, membership.OrganizationId);
                Assert.Equal(OrganizationRole.Viewer, membership.Role);
                Assert.True(membership.IsDefault);
            },
            membership =>
            {
                Assert.Equal(MockIdentityProfiles.OperationsOrganizationId, membership.OrganizationId);
                Assert.Equal(OrganizationRole.Dispatcher, membership.Role);
                Assert.False(membership.IsDefault);
            });
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedMembership)]
    [InlineData(MockIdentityProfiles.RevokedMembership)]
    public async Task Inactive_memberships_are_absent_from_authorized_context(string credential)
    {
        var authentication = await _provider.AuthenticateAsync(credential, default);
        var resolution = await _resolver.ResolveAsync(authentication.Identity!.Subject, default);

        Assert.True(resolution.IsResolved);
        Assert.Empty(resolution.Context!.Memberships);
    }

    [Fact]
    public async Task Cancellation_is_honored_by_both_mock_ports()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _provider.AuthenticateAsync(MockIdentityProfiles.ActiveViewer, source.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _resolver.ResolveAsync("mock-subject-active-viewer", source.Token));
    }
}
