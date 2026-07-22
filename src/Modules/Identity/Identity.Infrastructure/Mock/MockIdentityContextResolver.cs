using Identity.Application.Bootstrap;

namespace Identity.Infrastructure.Mock;

public sealed class MockIdentityContextResolver : IIdentityContextResolver
{
    public ValueTask<IdentityContextResolution> ResolveAsync(
        string identitySubject,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = !string.IsNullOrWhiteSpace(identitySubject) &&
            MockIdentityProfiles.AuthorizationProfiles.TryGetValue(identitySubject, out var context)
                ? IdentityContextResolution.Resolved(context)
                : IdentityContextResolution.NoAuthorizedContext;

        return ValueTask.FromResult(result);
    }
}
