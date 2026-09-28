using Microsoft.Extensions.Logging;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Infrastructure.Scheduling;

/// <summary>
/// ADR-034 MVP <see cref="IJobScheduler"/>: one cycle, then a fixed bounded delay, inside the
/// hosting <c>BackgroundService</c>. A failed cycle never ends the schedule because every job
/// restarts from durable state; cancellation ends it cleanly.
/// </summary>
/// <remarks>
/// OBS-002: every cycle writes one <see cref="TelemetryEvents.ScheduledJobCycle"/> line with the fixed
/// job name, <c>success</c> or <c>failure</c> and its duration. The exception itself is never logged
/// here: jobs log their own bounded error class.
/// </remarks>
public sealed class PeriodicJobScheduler(
    TimeProvider timeProvider,
    ILogger<PeriodicJobScheduler> logger) : IJobScheduler
{
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);

    public async Task RunAsync(IScheduledJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Interval <= TimeSpan.Zero || job.Interval > MaximumInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(job),
                $"Scheduled job {job.Name} interval must be positive and at most {MaximumInterval}.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var started = timeProvider.GetTimestamp();
            try
            {
                await job.RunOnceAsync(cancellationToken);
                LogCycle(job.Name, ScheduledJobOutcomes.Success, started);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                LogCycle(job.Name, ScheduledJobOutcomes.Failure, started);
            }

            try
            {
                await Task.Delay(job.Interval, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void LogCycle(string job, string outcome, long started) =>
        logger.Log(
            outcome == ScheduledJobOutcomes.Success ? LogLevel.Information : LogLevel.Error,
            TelemetryEvents.ScheduledJobCycle,
            "Scheduled job {Job} cycle finished with outcome {Outcome} in {DurationMs} ms.",
            job,
            outcome,
            (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
}
