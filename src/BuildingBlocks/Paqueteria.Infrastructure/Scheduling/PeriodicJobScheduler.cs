using Microsoft.Extensions.Logging;
using Paqueteria.Application.Scheduling;

namespace Paqueteria.Infrastructure.Scheduling;

/// <summary>
/// ADR-034 MVP <see cref="IJobScheduler"/>: one cycle, then a fixed bounded delay, inside the
/// hosting <c>BackgroundService</c>. A failed cycle never ends the schedule because every job
/// restarts from durable state; cancellation ends it cleanly.
/// </summary>
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
            try
            {
                await job.RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                logger.LogError("Scheduled job {JobName} cycle failed with outcome {Outcome}.", job.Name, "CYCLE_FAILURE");
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
}
