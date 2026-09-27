namespace Paqueteria.Application.Scheduling;

/// <summary>
/// AI-03 scheduled-job port. ADR-034 approves a periodic <c>BackgroundService</c> hosted by
/// <c>Paqueteria.Worker</c> as the MVP implementation: jobs own their work, the scheduler owns
/// only timing and cancellation.
/// </summary>
public interface IJobScheduler
{
    /// <summary>Runs <paramref name="job"/> every <see cref="IScheduledJob.Interval"/> until cancelled.</summary>
    Task RunAsync(IScheduledJob job, CancellationToken cancellationToken);
}

public interface IScheduledJob
{
    string Name { get; }

    TimeSpan Interval { get; }

    /// <summary>One bounded cycle. It must be safe to repeat after a failure or a restart.</summary>
    Task RunOnceAsync(CancellationToken cancellationToken);
}
