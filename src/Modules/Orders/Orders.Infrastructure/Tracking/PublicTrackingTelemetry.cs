using System.Diagnostics.Metrics;
using Orders.Application.Tracking;

namespace Orders.Infrastructure.Tracking;

internal sealed class PublicTrackingTelemetry : IPublicTrackingTelemetry, IDisposable
{
    internal const string MeterName = "Paquetenvia.PublicTracking";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _lookupCompleted;
    private readonly Counter<long> _lookupFailed;
    private readonly Counter<long> _rateLimitRejected;

    public PublicTrackingTelemetry()
    {
        _lookupCompleted = _meter.CreateCounter<long>("public_tracking.lookup.completed");
        _lookupFailed = _meter.CreateCounter<long>("public_tracking.lookup.failed");
        _rateLimitRejected = _meter.CreateCounter<long>("public_tracking.rate_limit.rejected");
    }

    public void LookupCompleted(string outcome) =>
        _lookupCompleted.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome));

    public void LookupFailed(string category) =>
        _lookupFailed.Add(
            1,
            new KeyValuePair<string, object?>("category", category));

    public void RateLimitRejected() => _rateLimitRejected.Add(1);

    public void Dispose() => _meter.Dispose();
}
