using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>ADR-034 Worker host for the LIF-001 job; it never schedules anything unless enabled.</summary>
internal sealed class ClaimWindowFinalizationHostedService(
    IJobScheduler scheduler,
    ClaimWindowFinalizationJob job,
    IOptions<ClaimWindowFinalizationOptions> options,
    ILogger<ClaimWindowFinalizationHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Claim-window finalization is disabled.");
            return Task.CompletedTask;
        }

        return scheduler.RunAsync(job, stoppingToken);
    }
}
