using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Realtime.Application.Dispatching;

namespace Paqueteria.IntegrationTests.Operations;

internal sealed class Ops001PausingOutboxInjector : IRealtimeOutboxFailureInjector
{
    private readonly TaskCompletionSource _paused =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Guid _outboxId;
    private int _entered;

    internal Ops001PausingOutboxInjector(Guid outboxId)
    {
        if (outboxId == Guid.Empty)
        {
            throw new ArgumentException("A target outbox id is required.", nameof(outboxId));
        }

        _outboxId = outboxId;
    }

    internal Task WaitUntilPausedAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _paused.Task.WaitAsync(timeout, cancellationToken);

    internal void Release() => _released.TrySetResult();

    public async ValueTask OnCheckpointAsync(
        RealtimeOutboxLane lane,
        Guid outboxId,
        RealtimeOutboxCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (lane != RealtimeOutboxLane.Business ||
            outboxId != _outboxId ||
            checkpoint !=
                RealtimeOutboxCheckpoint.AfterAllAudiencesPublishedBeforeSettle ||
            Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
        {
            return;
        }

        _paused.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class Ops001LogCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    internal IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CollectorLogger(_messages);

    public void Dispose()
    {
    }

    private sealed class CollectorLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}

internal sealed class Ops001AsyncResourceScope : IAsyncDisposable
{
    private readonly Stack<IAsyncDisposable> _resources = new();
    private int _disposed;

    internal T Own<T>(T resource) where T : IAsyncDisposable
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _resources.Push(resource);
        return resource;
    }

    internal int Count => _resources.Count;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? failures = null;
        while (_resources.TryPop(out var resource))
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }
}
