using Orders.Application.Tracking;

namespace Orders.Infrastructure.Tracking;

public sealed class DisabledPublicTrackingProjectionReader : IPublicTrackingProjectionReader
{
    public ValueTask<PublicTrackingLookupResult> FindAsync(
        string token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PublicTrackingLookupResult.NotFound);
    }
}
