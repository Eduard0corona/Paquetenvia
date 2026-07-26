using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class RealtimeOutboxTelemetry : IDisposable
{
    private readonly Meter _meter = new("Paquetenvia.Realtime.Outbox");
    private readonly Counter<long> _claimBatches;
    private readonly Counter<long> _claimedMessages;
    private readonly Counter<long> _publishedMessages;
    private readonly Counter<long> _audienceDeliveries;
    private readonly Counter<long> _settlements;
    private readonly Counter<long> _staleRequeued;
    private readonly Counter<long> _leaseLost;
    private readonly Counter<long> _mappingFailures;
    private readonly UpDownCounter<long> _inFlight;
    private readonly Histogram<double> _publishDuration;
    private readonly Histogram<double> _batchDuration;
    private readonly Histogram<double> _messageAge;

    public RealtimeOutboxTelemetry()
    {
        _claimBatches = _meter.CreateCounter<long>("realtime.outbox.claim_batches");
        _claimedMessages = _meter.CreateCounter<long>("realtime.outbox.claimed_messages");
        _publishedMessages = _meter.CreateCounter<long>("realtime.outbox.published_messages");
        _audienceDeliveries = _meter.CreateCounter<long>("realtime.outbox.audience_deliveries");
        _settlements = _meter.CreateCounter<long>("realtime.outbox.settlements");
        _staleRequeued = _meter.CreateCounter<long>("realtime.outbox.stale_requeued");
        _leaseLost = _meter.CreateCounter<long>("realtime.outbox.lease_lost");
        _mappingFailures = _meter.CreateCounter<long>("realtime.outbox.mapping_failures");
        _inFlight = _meter.CreateUpDownCounter<long>("realtime.outbox.in_flight");
        _publishDuration = _meter.CreateHistogram<double>("realtime.outbox.publish_duration", "ms");
        _batchDuration = _meter.CreateHistogram<double>("realtime.outbox.batch_duration", "ms");
        _messageAge = _meter.CreateHistogram<double>("realtime.outbox.oldest_claimed_age", "s");
    }

    public IDisposable MeasureBatch(string lane) =>
        Measure(value => _batchDuration.Record(value, Lane(lane)));

    public IDisposable MeasurePublish(string lane, string eventType) =>
        Measure(value => _publishDuration.Record(
            value,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("event_type", eventType)));

    public void Claimed(string lane, int count, DateTimeOffset? oldest)
    {
        _claimBatches.Add(1, Lane(lane));
        _claimedMessages.Add(count, Lane(lane));
        if (oldest is not null)
        {
            _messageAge.Record(
                Math.Max(0, (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds),
                Lane(lane));
        }
    }

    public void BeginMessage(string lane) => _inFlight.Add(1, Lane(lane));
    public void EndMessage(string lane) => _inFlight.Add(-1, Lane(lane));

    public void Published(string lane, string eventType) =>
        _publishedMessages.Add(
            1,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("event_type", eventType));

    public void AudienceDelivered(string lane, string eventType, string audience, string outcome) =>
        _audienceDeliveries.Add(
            1,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("event_type", eventType),
            new KeyValuePair<string, object?>("audience", audience),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void Settled(string lane, string outcome, string? errorClass = null) =>
        _settlements.Add(
            1,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("error_class", errorClass));

    public void StaleRequeued(string lane, int count) =>
        _staleRequeued.Add(count, Lane(lane));

    public void LeaseLost(string lane) => _leaseLost.Add(1, Lane(lane));

    public void MappingFailed(string lane, string errorClass) =>
        _mappingFailures.Add(
            1,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("error_class", errorClass));

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> Lane(string lane) => new("lane", lane);

    private static IDisposable Measure(Action<double> recorder) => new Measurement(recorder);

    private sealed class Measurement(Action<double> recorder) : IDisposable
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();

        public void Dispose() => recorder(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
    }
}
