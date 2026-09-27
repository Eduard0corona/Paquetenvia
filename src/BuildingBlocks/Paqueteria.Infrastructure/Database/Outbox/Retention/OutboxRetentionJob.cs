using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

/// <summary>
/// OPS-004 <see cref="IScheduledJob"/>: one invocation is exactly one bounded retention cycle over
/// both lanes. Timing, repetition and cancellation belong to <see cref="IJobScheduler"/>; lane
/// failures are isolated and reported by <see cref="OutboxRetentionService"/>.
/// </summary>
internal sealed class OutboxRetentionJob(
    OutboxRetentionService service,
    IOptions<OutboxRetentionOptions> options) : IScheduledJob
{
    public const string JobName = "outbox.retention";

    public string Name => JobName;

    public TimeSpan Interval => options.Value.PollInterval;

    public Task RunOnceAsync(CancellationToken cancellationToken) => service.RunOnceAsync(cancellationToken);
}
