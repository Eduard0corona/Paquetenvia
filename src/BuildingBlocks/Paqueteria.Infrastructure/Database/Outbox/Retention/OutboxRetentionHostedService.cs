using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

/// <summary>
/// ADR-034 Worker host for OPS-004: it hands <see cref="OutboxRetentionJob"/> to the shared
/// <see cref="IJobScheduler"/> and never schedules anything unless enabled. A failed cycle is
/// retried on the next interval by the scheduler; it never stops the Worker.
/// </summary>
internal sealed class OutboxRetentionHostedService(
    IJobScheduler scheduler,
    OutboxRetentionJob job,
    IOptions<OutboxRetentionOptions> options,
    ILogger<OutboxRetentionHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Outbox retention is disabled; no purge function will be called.");
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Outbox retention scheduled with dry_run={DryRun} poll_interval={PollInterval}.",
            settings.DryRun,
            settings.PollInterval);
        return scheduler.RunAsync(job, stoppingToken);
    }
}
