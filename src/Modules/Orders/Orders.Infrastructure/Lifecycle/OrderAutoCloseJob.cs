using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application.Lifecycle;
using Paqueteria.Application.Scheduling;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10 Worker job: one bounded <see cref="OrderAutoCloseCycle"/> per interval. Orders whose
/// CLOSED guards do not hold yet are counted, not logged one by one. A cycle in which an attempt failed unexpectedly
/// still processes the rest of its orders, then reports itself failed so the OBS-002 job-failure alert sees it.
/// Logs and metrics carry counts, outcomes and AI-05 rule codes only: never an identifier or personal datum.
/// </summary>
internal sealed class OrderAutoCloseJob(
    OrderAutoCloseCycle cycle,
    IOptions<OrderAutoCloseOptions> options,
    OrderAutoCloseTelemetry telemetry,
    ILogger<OrderAutoCloseJob> logger) : IScheduledJob
{
    public const string JobName = "orders.auto-close";

    public string Name => JobName;

    public TimeSpan Interval => options.Value.PollInterval;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        OrderAutoCloseCycleResult result;
        try
        {
            result = await cycle.RunAsync(options.Value.ToPolicy(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            telemetry.CycleFailed();
            throw;
        }

        var outcome = result.Failed > 0 ? "failed" : result.Drained ? "drained" : "capped";
        telemetry.CycleCompleted(outcome, result);
        if (result.Closed > 0 || result.Failed > 0 || !result.Drained)
        {
            logger.Log(
                result.Failed > 0 ? LogLevel.Warning : LogLevel.Information,
                "Order auto-close cycle {Outcome}: closed {Closed}, not eligible {NotEligible}, superseded {Superseded}, failed {Failed} of {Attempted} attempts in {Batches} batches.",
                outcome,
                result.Closed,
                result.NotEligible,
                result.Superseded,
                result.Failed,
                result.Attempted,
                result.Batches);
        }

        if (result.Failed > 0)
        {
            throw new OrderAutoCloseCycleException(result.Failed, result.FailureTypes);
        }
    }
}

/// <summary>Raised after a cycle finished with failed attempts; it carries counts and exception type names only.</summary>
public sealed class OrderAutoCloseCycleException(int failed, IReadOnlyList<string> failureTypes)
    : Exception($"ORD_AUTO_CLOSE_ATTEMPTS_FAILED: {failed} ({string.Join(",", failureTypes)})")
{
    public int Failed { get; } = failed;

    public IReadOnlyList<string> FailureTypes { get; } = failureTypes;
}

internal sealed class OrderAutoCloseTelemetry : IDisposable
{
    internal const string MeterName = "Paquetenvia.Orders.AutoClose";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _attempts;

    public OrderAutoCloseTelemetry()
    {
        _cycles = _meter.CreateCounter<long>("orders.auto_close.cycles");
        _attempts = _meter.CreateCounter<long>("orders.auto_close.attempts");
    }

    public void CycleFailed() => _cycles.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

    /// <summary>
    /// One attempts series per outcome: <c>closed</c>, <c>superseded</c>, <c>failed</c> and, for an order that stayed
    /// DELIVERED, the lower-case AI-05 code of the guard that did not hold (a fixed set of four CLOSED rules).
    /// </summary>
    public void CycleCompleted(string outcome, OrderAutoCloseCycleResult result)
    {
        _cycles.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Add("closed", result.Closed);
        Add("superseded", result.Superseded);
        Add("failed", result.Failed);
        foreach (var (rule, count) in result.NotEligibleByRule)
        {
            Add(rule.ToLowerInvariant(), count);
        }
    }

    public void Dispose() => _meter.Dispose();

    private void Add(string outcome, int count)
    {
        if (count > 0)
        {
            _attempts.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
