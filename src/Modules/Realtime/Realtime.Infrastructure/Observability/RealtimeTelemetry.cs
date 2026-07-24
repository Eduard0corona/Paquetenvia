using System.Diagnostics;
using System.Diagnostics.Metrics;
using Realtime.Application.Observability;

namespace Realtime.Infrastructure.Observability;

internal sealed class RealtimeTelemetry : IRealtimeTelemetry, IDisposable
{
    internal const string MeterName = "Paquetenvia.Realtime";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _connectionsAccepted;
    private readonly Counter<long> _connectionsRejected;
    private readonly Counter<long> _connectionsClosed;
    private readonly UpDownCounter<long> _activeConnections;
    private readonly Counter<long> _publicationSuccess;
    private readonly Counter<long> _publicationFailure;
    private readonly Histogram<double> _authorizationDuration;
    private readonly Histogram<double> _publicationDuration;

    public RealtimeTelemetry()
    {
        _connectionsAccepted = _meter.CreateCounter<long>("realtime.connections.accepted");
        _connectionsRejected = _meter.CreateCounter<long>("realtime.connections.rejected");
        _connectionsClosed = _meter.CreateCounter<long>("realtime.connections.closed");
        _activeConnections = _meter.CreateUpDownCounter<long>("realtime.connections.active");
        _publicationSuccess = _meter.CreateCounter<long>("realtime.publications.success");
        _publicationFailure = _meter.CreateCounter<long>("realtime.publications.failure");
        _authorizationDuration = _meter.CreateHistogram<double>("realtime.authorization.duration", "ms");
        _publicationDuration = _meter.CreateHistogram<double>("realtime.publication.duration", "ms");
    }

    public IDisposable MeasureAuthorization(string hub, string authKind) =>
        new DurationMeasurement(
            elapsed => _authorizationDuration.Record(
                elapsed,
                new KeyValuePair<string, object?>("hub", hub),
                new KeyValuePair<string, object?>("auth.kind", authKind)));

    public IDisposable MeasurePublication(string eventType) =>
        new DurationMeasurement(
            elapsed => _publicationDuration.Record(
                elapsed,
                new KeyValuePair<string, object?>("event.type", eventType)));

    public void ConnectionAccepted(string hub, string authKind)
    {
        var tags = ConnectionTags(hub, authKind);
        _connectionsAccepted.Add(1, tags);
        _activeConnections.Add(1, new KeyValuePair<string, object?>("hub", hub));
    }

    public void ConnectionRejected(string hub, string authKind) =>
        _connectionsRejected.Add(1, ConnectionTags(hub, authKind));

    public void ConnectionClosed(string hub)
    {
        _connectionsClosed.Add(1, new KeyValuePair<string, object?>("hub", hub));
        _activeConnections.Add(-1, new KeyValuePair<string, object?>("hub", hub));
    }

    public void PublicationSucceeded(string eventType) =>
        _publicationSuccess.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void PublicationFailed(string eventType) =>
        _publicationFailure.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void Dispose() => _meter.Dispose();

    private static TagList ConnectionTags(string hub, string authKind)
    {
        var tags = new TagList
        {
            { "hub", hub },
            { "auth.kind", authKind },
        };
        return tags;
    }

    private sealed class DurationMeasurement(Action<double> recorder) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public void Dispose() => recorder(Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
    }
}
