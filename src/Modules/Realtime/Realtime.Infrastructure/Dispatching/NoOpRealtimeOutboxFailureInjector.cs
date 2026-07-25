using Realtime.Application.Dispatching;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class NoOpRealtimeOutboxFailureInjector : IRealtimeOutboxFailureInjector
{
    public ValueTask OnCheckpointAsync(
        RealtimeOutboxLane lane,
        Guid outboxId,
        RealtimeOutboxCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
