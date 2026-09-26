using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

/// <summary>
/// OPS-004 retention metrics. Dimensions are limited to <c>lane</c> (business|location),
/// <c>mode</c> (dry_run|delete), <c>outcome</c> and a bounded <c>error_class</c>.
/// </summary>
internal sealed class OutboxRetentionTelemetry : IDisposable
{
    public const string MeterName = "Paquetenvia.Outbox.Retention";

    private readonly Counter<long> _runs;
    private readonly Counter<long> _batches;
    private readonly Counter<long> _eligible;
    private readonly Counter<long> _deleted;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _runDuration;
    private readonly ConcurrentDictionary<(string Lane, string Mode), long> _lastSuccess = new();
    private readonly ConcurrentDictionary<string, long> _deadEligible = new(StringComparer.Ordinal);

    public OutboxRetentionTelemetry()
    {
        _runs = Meter.CreateCounter<long>("outbox.retention.runs");
        _batches = Meter.CreateCounter<long>("outbox.retention.batches");
        _eligible = Meter.CreateCounter<long>("outbox.retention.dry_run_eligible");
        _deleted = Meter.CreateCounter<long>("outbox.retention.deleted_rows");
        _failures = Meter.CreateCounter<long>("outbox.retention.failures");
        _runDuration = Meter.CreateHistogram<double>("outbox.retention.run_duration", "ms");
        Meter.CreateObservableGauge(
            "outbox.retention.last_success",
            ObserveLastSuccess,
            "s",
            "Unix time of the last successful retention run per lane and mode.");
        Meter.CreateObservableGauge(
            "outbox.retention.dead_eligible",
            ObserveDeadEligible,
            "{row}",
            "DEAD rows past the DEAD cutoff at the last lane run, bounded by the lane's maximum batch size.");
    }

    internal Meter Meter { get; } = new(MeterName);

    public void BatchCompleted(string lane, bool dryRun, int affected)
    {
        var mode = Mode(dryRun);
        _batches.Add(1, Lane(lane), mode);
        if (dryRun)
        {
            _eligible.Add(affected, Lane(lane));
        }
        else
        {
            _deleted.Add(affected, Lane(lane));
        }
    }

    public void DeadEligibleObserved(string lane, int count) => _deadEligible[lane] = count;

    public void RunCompleted(string lane, bool dryRun, string outcome, TimeSpan duration, DateTimeOffset completedAt)
    {
        _runs.Add(1, Lane(lane), Mode(dryRun), new KeyValuePair<string, object?>("outcome", outcome));
        _runDuration.Record(duration.TotalMilliseconds, Lane(lane), Mode(dryRun), new KeyValuePair<string, object?>("outcome", outcome));
        if (outcome == OutboxRetentionOutcomes.Success)
        {
            _lastSuccess[(lane, ModeName(dryRun))] = completedAt.ToUnixTimeSeconds();
        }
    }

    public void Failed(string lane, bool dryRun, string errorClass) =>
        _failures.Add(
            1,
            Lane(lane),
            Mode(dryRun),
            new KeyValuePair<string, object?>("error_class", errorClass));

    public void Dispose() => Meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveLastSuccess()
    {
        foreach (var (key, value) in _lastSuccess)
        {
            yield return new Measurement<long>(
                value,
                Lane(key.Lane),
                new KeyValuePair<string, object?>("mode", key.Mode));
        }
    }

    private IEnumerable<Measurement<long>> ObserveDeadEligible()
    {
        foreach (var (lane, value) in _deadEligible)
        {
            yield return new Measurement<long>(value, Lane(lane));
        }
    }

    private static KeyValuePair<string, object?> Lane(string lane) => new("lane", lane);

    private static KeyValuePair<string, object?> Mode(bool dryRun) => new("mode", ModeName(dryRun));

    private static string ModeName(bool dryRun) => dryRun ? "dry_run" : "delete";
}

public static class OutboxRetentionOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Cancelled = "cancelled";
}
