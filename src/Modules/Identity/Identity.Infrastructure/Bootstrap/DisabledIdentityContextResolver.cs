using Identity.Application.Bootstrap;

namespace Identity.Infrastructure.Bootstrap;

public sealed class DisabledIdentityContextResolver : IIdentityContextResolver
{
    public ValueTask<IdentityContextResolution> ResolveAsync(
        string identitySubject,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(IdentityContextResolution.NoAuthorizedContext);
    }
}
