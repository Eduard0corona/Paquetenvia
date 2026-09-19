using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Realtime.Application.Dispatching;
using Realtime.Application.Events;
using Realtime.Application.Publishing;

namespace Paqueteria.IntegrationTests.Realtime;

/// <summary>
/// DIAGNOSTIC ONLY (driver PWA cold CI correlation probe). Append-only
/// timeline shared by the test body, the Kestrel host logging pipeline and the
/// realtime test hooks. It observes existing behavior and never blocks,
/// retries or changes synchronization.
/// </summary>
internal sealed class RealtimeDiagnosticTimeline
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<string> _events = new();
    private readonly string _testId;

    internal RealtimeDiagnosticTimeline(string testName)
    {
        _testId = $"pid={Environment.ProcessId} test={testName}";
        Record("test", "timeline_started");
    }

    internal double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;

    internal void Record(string source, string message)
    {
        _events.Enqueue(
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.ffffffZ} +{_clock.Elapsed.TotalMilliseconds,10:F3}ms [{_testId}] {source}: {message}");
    }

    internal IReadOnlyList<string> Snapshot() => [.. _events];

    internal static string Short(Guid id) => id.ToString("D")[..8];
}

/// <summary>
/// DIAGNOSTIC ONLY. Captures the SignalR/HTTP-connections/Realtime categories of
/// the test host into the timeline, including the connection-id logging scope.
/// </summary>
internal sealed class RealtimeDiagnosticLoggerProvider(
    RealtimeDiagnosticTimeline timeline) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;

    internal static readonly string[] Categories =
    [
        "Microsoft.AspNetCore.SignalR",
        "Microsoft.AspNetCore.Http.Connections",
        "Realtime",
    ];

    public ILogger CreateLogger(string categoryName) =>
        new DiagnosticLogger(timeline, categoryName, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class DiagnosticLogger(
        RealtimeDiagnosticTimeline timeline,
        string category,
        Func<IExternalScopeProvider?> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            scopes()?.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None &&
            Categories.Any(prefix =>
                category.StartsWith(prefix, StringComparison.Ordinal));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var scope = new StringBuilder();
            scopes()?.ForEachScope(
                static (value, builder) => builder.Append('{').Append(value).Append('}'),
                scope);
            var text = formatter(state, exception);
            if (exception is not null)
            {
                text += $" exception={exception.GetType().Name}: {exception.Message}";
            }

            timeline.Record(
                "host",
                $"{logLevel} {category}{scope} {text}");
        }
    }
}

/// <summary>
/// DIAGNOSTIC ONLY. Pass-through publisher decorator that records the begin,
/// completion or failure of the AssignmentChanged publications and whether the
/// driver audience matches the synthetic driver of the test.
/// </summary>
internal sealed class RealtimeDiagnosticPublisher(
    IRealtimePublisher inner,
    RealtimeDiagnosticTimeline timeline,
    Guid expectedDriverId) : IRealtimePublisher
{
    internal static void Decorate(
        IServiceCollection services,
        RealtimeDiagnosticTimeline timeline,
        Guid expectedDriverId)
    {
        var descriptor = services.Single(static candidate =>
            candidate.ServiceType == typeof(IRealtimePublisher));
        services.Remove(descriptor);
        services.AddSingleton<IRealtimePublisher>(provider =>
            new RealtimeDiagnosticPublisher(
                (IRealtimePublisher)descriptor.ImplementationFactory!(provider),
                timeline,
                expectedDriverId));
    }

    public async Task PublishOperationsAssignmentChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<AssignmentChangedPayload> message,
        CancellationToken cancellationToken)
    {
        var eventId = RealtimeDiagnosticTimeline.Short(message.EventId);
        timeline.Record("publish", $"operations AssignmentChanged begin event={eventId}");
        try
        {
            await inner.PublishOperationsAssignmentChangedAsync(audience, message, cancellationToken);
            timeline.Record("publish", $"operations AssignmentChanged completed event={eventId}");
        }
        catch (Exception exception)
        {
            timeline.Record(
                "publish",
                $"operations AssignmentChanged FAILED event={eventId} {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    public async Task PublishDriverAssignmentChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<AssignmentChangedPayload> message,
        CancellationToken cancellationToken)
    {
        var eventId = RealtimeDiagnosticTimeline.Short(message.EventId);
        var target = audience == DriverAudience.ForDriver(expectedDriverId)
            ? $"Driver({RealtimeDiagnosticTimeline.Short(expectedDriverId)}) expected_target=true"
            : "expected_target=false";
        timeline.Record("publish", $"driver AssignmentChanged begin event={eventId} target={target}");
        try
        {
            await inner.PublishDriverAssignmentChangedAsync(audience, message, cancellationToken);
            timeline.Record("publish", $"driver AssignmentChanged completed event={eventId} target={target}");
        }
        catch (Exception exception)
        {
            timeline.Record(
                "publish",
                $"driver AssignmentChanged FAILED event={eventId} target={target} {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    public Task PublishOperationsOrderStatusChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<OrderStatusChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsOrderStatusChangedAsync(audience, message, cancellationToken);

    public Task PublishOperationsOrderTimelineEventAddedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<OrderTimelineEventAddedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsOrderTimelineEventAddedAsync(audience, message, cancellationToken);

    public Task PublishOperationsRouteChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<RouteChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsRouteChangedAsync(audience, message, cancellationToken);

    public Task PublishOperationsIncidentCreatedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<IncidentCreatedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsIncidentCreatedAsync(audience, message, cancellationToken);

    public Task PublishOperationsExternalOfferChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<ExternalOfferChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsExternalOfferChangedAsync(audience, message, cancellationToken);

    public Task PublishOperationsNotificationStatusChangedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<NotificationStatusChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsNotificationStatusChangedAsync(audience, message, cancellationToken);

    public Task PublishOperationsDriverLocationUpdatedAsync(
        OperationsAudience audience,
        RealtimeEnvelope<DriverLocationUpdatedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishOperationsDriverLocationUpdatedAsync(audience, message, cancellationToken);

    public Task PublishDriverRouteChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<RouteChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishDriverRouteChangedAsync(audience, message, cancellationToken);

    public Task PublishDriverOrderStatusChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<OrderStatusChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishDriverOrderStatusChangedAsync(audience, message, cancellationToken);

    public Task PublishDriverExternalOfferChangedAsync(
        DriverAudience audience,
        RealtimeEnvelope<ExternalOfferChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishDriverExternalOfferChangedAsync(audience, message, cancellationToken);

    public Task PublishTrackingPublicOrderStatusChangedAsync(
        TrackingAudience audience,
        PublicRealtimeEnvelope<PublicOrderStatusChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishTrackingPublicOrderStatusChangedAsync(audience, message, cancellationToken);

    public Task PublishTrackingPublicEtaChangedAsync(
        TrackingAudience audience,
        PublicRealtimeEnvelope<PublicEtaChangedPayload> message,
        CancellationToken cancellationToken) =>
        inner.PublishTrackingPublicEtaChangedAsync(audience, message, cancellationToken);
}

/// <summary>
/// DIAGNOSTIC ONLY. Never throws: it only records the existing
/// after-all-audiences-published checkpoint of the synthetic outbox row.
/// </summary>
internal sealed class RealtimeDiagnosticCheckpointObserver(
    RealtimeDiagnosticTimeline timeline,
    Guid expectedOutboxId) : IRealtimeOutboxFailureInjector
{
    public ValueTask OnCheckpointAsync(
        RealtimeOutboxLane lane,
        Guid outboxId,
        RealtimeOutboxCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        timeline.Record(
            "outbox",
            $"checkpoint {checkpoint} lane={lane} outbox={RealtimeDiagnosticTimeline.Short(outboxId)} expected_outbox={outboxId == expectedOutboxId}");
        return ValueTask.CompletedTask;
    }
}
