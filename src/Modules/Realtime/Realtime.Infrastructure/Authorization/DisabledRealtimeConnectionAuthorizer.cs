using Realtime.Application.Authorization;

namespace Realtime.Infrastructure.Authorization;

internal sealed class DisabledRealtimeConnectionAuthorizer : IRealtimeConnectionAuthorizer
{
    public ValueTask<ConnectionAuthorizationResult<OperationsConnectionAuthorization>> AuthorizeOperationsAsync(
        PrivateRealtimeConnectionRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConnectionAuthorizationResult<OperationsConnectionAuthorization>.Rejected);

    public ValueTask<ConnectionAuthorizationResult<DriverConnectionAuthorization>> AuthorizeDriverAsync(
        PrivateRealtimeConnectionRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConnectionAuthorizationResult<DriverConnectionAuthorization>.Rejected);

    public ValueTask<ConnectionAuthorizationResult<TrackingConnectionAuthorization>> AuthorizeTrackingAsync(
        string exactToken,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ConnectionAuthorizationResult<TrackingConnectionAuthorization>.Rejected);
}
