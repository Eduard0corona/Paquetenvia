using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Database.Outbox.Retention;

namespace Paqueteria.UnitTests.Operations;

internal sealed class RecordingPurgeGateway(
    Func<OutboxPurgeRequest, int, CancellationToken, Task<int>> respond) : IOutboxPurgeGateway
{
    private readonly ConcurrentDictionary<OutboxRetentionLane, int> _calls = new();

    public ConcurrentQueue<OutboxPurgeRequest> Requests { get; } = new();

    public static RecordingPurgeGateway Returning(Func<OutboxPurgeRequest, int> count) =>
        new((request, _, _) => Task.FromResult(count(request)));

    public Task<int> PurgeAsync(OutboxPurgeRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var call = _calls.AddOrUpdate(request.Lane, 1, (_, value) => value + 1);
        return respond(request, call, cancellationToken);
    }

    public IReadOnlyList<OutboxPurgeRequest> For(OutboxRetentionLane lane) =>
        Requests.Where(request => request.Lane == lane).ToArray();
}

/// <summary>Records what the host hands to <see cref="IJobScheduler"/> and returns at once.</summary>
internal sealed class RecordingJobScheduler : IJobScheduler
{
    public ConcurrentQueue<IScheduledJob> Jobs { get; } = new();

    public ConcurrentQueue<CancellationToken> Tokens { get; } = new();

    public Task RunAsync(IScheduledJob job, CancellationToken cancellationToken)
    {
        Jobs.Enqueue(job);
        Tokens.Enqueue(cancellationToken);
        return Task.CompletedTask;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public TimeSpan AdvanceOnRead { get; init; }

    public override DateTimeOffset GetUtcNow()
    {
        var value = _now;
        _now += AdvanceOnRead;
        return value;
    }
}

/// <summary>Real time, but every scheduled delay elapses after one millisecond.</summary>
internal sealed class AcceleratedTimeProvider : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, Compress(dueTime), Compress(period));

    private static TimeSpan Compress(TimeSpan value) =>
        value == Timeout.InfiniteTimeSpan || value == TimeSpan.Zero ? value : TimeSpan.FromMilliseconds(1);
}

internal sealed record CapturedLog(
    LogLevel Level,
    EventId EventId,
    IReadOnlyDictionary<string, object?> Values,
    string Message);

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        Entries.Enqueue(new CapturedLog(
            logLevel,
            eventId,
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            formatter(state, exception)));
    }
}
