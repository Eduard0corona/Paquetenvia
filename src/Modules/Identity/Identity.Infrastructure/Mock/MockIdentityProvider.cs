using Identity.Application.Authentication;

namespace Identity.Infrastructure.Mock;

public sealed class MockIdentityProvider : IIdentityProvider
{
    public ValueTask<IdentityAuthenticationResult> AuthenticateAsync(
        string credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(credential) ||
            !MockIdentityProfiles.All.TryGetValue(credential, out var identity))
        {
            return ValueTask.FromResult(IdentityAuthenticationResult.Invalid);
        }

        return ValueTask.FromResult(IdentityAuthenticationResult.Success(identity));
    }
}
