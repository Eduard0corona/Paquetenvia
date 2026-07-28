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

internal sealed class Ops001RunTestCheckpoint
{
    private readonly TaskCompletionSource _ownedResourcesStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _continue =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Guid _targetOutboxId;
    private string? _finalOutboxStatus;
    private int _activeHosts;
    private int _activeObservers;
    private int _activeProofWorkers;
    private int _cleanupCompleted;

    internal Ops001RunTestCheckpoint(string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("A report path is required.", nameof(reportPath));
        }

        ReportPath = Path.GetFullPath(reportPath);
    }

    internal string ReportPath { get; }
    internal Guid TargetOutboxId => _targetOutboxId;
    internal string? FinalOutboxStatus => _finalOutboxStatus;
    internal int ActiveHosts => Volatile.Read(ref _activeHosts);
    internal int ActiveObservers => Volatile.Read(ref _activeObservers);
    internal int ActiveProofWorkers => Volatile.Read(ref _activeProofWorkers);
    internal bool CleanupWasCompleted => Volatile.Read(ref _cleanupCompleted) != 0;

    internal void SetTargetOutbox(Guid outboxId) => _targetOutboxId = outboxId;

    internal void OwnedHostStarted() => Interlocked.Increment(ref _activeHosts);
    internal void OwnedObserverStarted() => Interlocked.Increment(ref _activeObservers);
    internal void OwnedProofWorkerStarted() =>
        Interlocked.Increment(ref _activeProofWorkers);

    internal void OwnedHostDisposed() => Decrement(ref _activeHosts, "host");
    internal void OwnedObserverDisposed() => Decrement(ref _activeObservers, "observer");
    internal void OwnedProofWorkerDisposed() =>
        Decrement(ref _activeProofWorkers, "proof worker");

    internal async Task PauseAfterOwnedResourcesStartedAsync(
        CancellationToken cancellationToken)
    {
        _ownedResourcesStarted.TrySetResult();
        await _continue.Task.WaitAsync(cancellationToken);
    }

    internal Task WaitUntilOwnedResourcesStartedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _ownedResourcesStarted.Task.WaitAsync(timeout, cancellationToken);

    internal void Continue() => _continue.TrySetResult();

    internal void CleanupCompleted(string? finalOutboxStatus)
    {
        _finalOutboxStatus = finalOutboxStatus;
        Volatile.Write(ref _cleanupCompleted, 1);
    }

    private static void Decrement(ref int counter, string resource)
    {
        if (Interlocked.Decrement(ref counter) < 0)
        {
            throw new InvalidOperationException(
                $"The owned {resource} resource counter became negative.");
        }
    }
}
