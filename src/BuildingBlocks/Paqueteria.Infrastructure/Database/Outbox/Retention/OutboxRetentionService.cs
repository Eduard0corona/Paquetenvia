using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

public readonly record struct OutboxRetentionCutoffs(DateTimeOffset ProcessedBefore, DateTimeOffset DeadBefore)
{
    /// <summary>
    /// Cutoffs are fixed once per lane run. Truncating to PostgreSQL's microsecond precision only
    /// moves them further into the past, so the value logged is exactly the value evaluated.
    /// </summary>
    public static OutboxRetentionCutoffs Calculate(DateTimeOffset now, OutboxRetentionLaneOptions lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        var utcNow = now.ToUniversalTime();
        return new(
            UtcMicrosecondPrecision.Normalize(utcNow - lane.ProcessedRetention),
            UtcMicrosecondPrecision.Normalize(utcNow - lane.DeadRetention));
    }
}

/// <summary>
/// Operational evidence of one lane run. It is logged as a structured record and returned so a
/// caller of the explicit dry-run path sees the same values the scheduled job reports.
/// </summary>
public sealed record OutboxRetentionLaneReport(
    OutboxRetentionLane Lane,
    bool DryRun,
    DateTimeOffset ProcessedBefore,
    DateTimeOffset DeadBefore,
    int BatchSize,
    int MaxBatchesPerRun,
    int Batches,
    long AffectedRows,
    bool Exhausted,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Outcome,
    string? ErrorClass);

public sealed record OutboxRetentionRunReport(IReadOnlyList<OutboxRetentionLaneReport> Lanes);

/// <summary>
/// Runs one bounded retention cycle over both outbox lanes through the approved purge functions.
/// </summary>
/// <remarks>
/// Lanes are independent: each has its own cutoffs, batch size and per-run ceiling, and a failure
/// in one lane is reported without preventing the other. The job never reads, updates or deletes
/// outbox rows itself and never changes a message state.
/// </remarks>
internal sealed class OutboxRetentionService(
    IOutboxPurgeGateway gateway,
    IOptions<OutboxRetentionOptions> options,
    OutboxRetentionTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<OutboxRetentionService> logger)
{
    private static readonly EventId LaneCompleted = new(4004, "OutboxRetentionLaneCompleted");

    /// <summary>The scheduled path: deletes only when <c>OutboxRetention:DryRun=false</c>.</summary>
    public Task<OutboxRetentionRunReport> RunOnceAsync(CancellationToken cancellationToken) =>
        RunAsync(options.Value.DryRun, cancellationToken);

    /// <summary>
    /// The explicit dry-run path. It always passes <c>p_dry_run=true</c>, whatever the configured
    /// mode; the destructive mode can only be selected through configuration.
    /// </summary>
    public Task<OutboxRetentionRunReport> DryRunAsync(CancellationToken cancellationToken) =>
        RunAsync(dryRun: true, cancellationToken);

    private async Task<OutboxRetentionRunReport> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var reports = new List<OutboxRetentionLaneReport>(OutboxRetentionLaneContract.All.Count);
        foreach (var contract in OutboxRetentionLaneContract.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reports.Add(await RunLaneAsync(contract, options.Value.For(contract.Lane), dryRun, cancellationToken));
        }

        return new OutboxRetentionRunReport(reports.AsReadOnly());
    }

    private async Task<OutboxRetentionLaneReport> RunLaneAsync(
        OutboxRetentionLaneContract contract,
        OutboxRetentionLaneOptions lane,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var startedTimestamp = timeProvider.GetTimestamp();
        var cutoffs = OutboxRetentionCutoffs.Calculate(startedAt, lane);

        // A dry-run repeated within the same run would count the same candidates again, so it is
        // one call; the destructive loop stops at the ceiling or when a batch comes back short.
        var ceiling = dryRun ? 1 : lane.MaxBatchesPerRun;
        var batches = 0;
        var affected = 0L;
        var exhausted = false;
        try
        {
            while (batches < ceiling)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await gateway.PurgeAsync(
                    new OutboxPurgeRequest(contract.Lane, cutoffs.ProcessedBefore, cutoffs.DeadBefore, lane.BatchSize, dryRun),
                    cancellationToken);
                batches++;
                affected += count;
                telemetry.BatchCompleted(contract.Name, dryRun, count);
                if (count < lane.BatchSize)
                {
                    exhausted = true;
                    break;
                }
            }

            return Complete(OutboxRetentionOutcomes.Success, errorClass: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(OutboxRetentionOutcomes.Cancelled, errorClass: null);
            throw;
        }
        catch (Exception exception)
        {
            var errorClass = ErrorClass(exception);
            telemetry.Failed(contract.Name, dryRun, errorClass);
            return Complete(OutboxRetentionOutcomes.Failure, errorClass);
        }

        OutboxRetentionLaneReport Complete(string outcome, string? errorClass)
        {
            var report = new OutboxRetentionLaneReport(
                contract.Lane,
                dryRun,
                cutoffs.ProcessedBefore,
                cutoffs.DeadBefore,
                lane.BatchSize,
                lane.MaxBatchesPerRun,
                batches,
                affected,
                exhausted,
                startedAt,
                timeProvider.GetUtcNow(),
                outcome,
                errorClass);
            telemetry.RunCompleted(
                contract.Name,
                dryRun,
                outcome,
                timeProvider.GetElapsedTime(startedTimestamp),
                report.CompletedAt);
            Log(contract, report);
            return report;
        }
    }

    private void Log(OutboxRetentionLaneContract contract, OutboxRetentionLaneReport report) =>
        logger.Log(
            report.Outcome == OutboxRetentionOutcomes.Failure ? LogLevel.Error : LogLevel.Information,
            LaneCompleted,
            "Outbox retention lane {Lane} finished with outcome {Outcome}. dry_run={DryRun} processed_before={ProcessedBefore} dead_before={DeadBefore} batch_size={BatchSize} max_batches_per_run={MaxBatchesPerRun} batches={Batches} affected_rows={AffectedRows} exhausted={Exhausted} started_at={StartedAt} completed_at={CompletedAt} error_class={ErrorClass}",
            contract.Name,
            report.Outcome,
            report.DryRun,
            Timestamp(report.ProcessedBefore),
            Timestamp(report.DeadBefore),
            report.BatchSize,
            report.MaxBatchesPerRun,
            report.Batches,
            report.AffectedRows,
            report.Exhausted,
            Timestamp(report.StartedAt),
            Timestamp(report.CompletedAt),
            report.ErrorClass);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // Bounded error classes: an SQLSTATE (22023 marks a cutoff inside the normative window) or a
    // coarse transport class. Exception messages are never used as metric dimensions.
    private static string ErrorClass(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState,
        NpgsqlException { IsTransient: true } => "transient",
        NpgsqlException => "npgsql",
        TimeoutException => "timeout",
        _ => "unexpected",
    };
}
