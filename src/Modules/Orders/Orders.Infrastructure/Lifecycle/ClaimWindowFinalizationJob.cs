using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application.Lifecycle;
using Paqueteria.Application.Scheduling;

namespace Orders.Infrastructure.Lifecycle;

internal sealed class ClaimWindowFinalizationJob(
    ClaimWindowFinalizationCycle cycle,
    IOptions<ClaimWindowFinalizationOptions> options,
    ClaimWindowFinalizationTelemetry telemetry,
    ILogger<ClaimWindowFinalizationJob> logger) : IScheduledJob
{
    public const string JobName = "orders.claim-window-finalization";

    public string Name => JobName;

    public TimeSpan Interval => options.Value.PollInterval;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        ClaimWindowFinalizationCycleResult result;
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
            telemetry.CycleCompleted("failed", 0);
            throw;
        }

        var outcome = result.Drained ? "drained" : "capped";
        telemetry.CycleCompleted(outcome, result.Finalized);
        if (result.Finalized > 0 || !result.Drained)
        {
            logger.LogInformation(
                "Claim-window finalization cycle {Outcome}: finalized {Finalized} orders in {Batches} batches.",
                outcome,
                result.Finalized,
                result.Batches);
        }
    }
}

internal sealed class ClaimWindowFinalizationTelemetry : IDisposable
{
    internal const string MeterName = "Paquetenvia.Orders.ClaimWindowFinalization";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _finalized;

    public ClaimWindowFinalizationTelemetry()
    {
        _cycles = _meter.CreateCounter<long>("orders.claim_window_finalization.cycles");
        _finalized = _meter.CreateCounter<long>("orders.claim_window_finalization.finalized");
    }

    public void CycleCompleted(string outcome, int finalized)
    {
        _cycles.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (finalized > 0)
        {
            _finalized.Add(finalized);
        }
    }

    public void Dispose() => _meter.Dispose();
}
