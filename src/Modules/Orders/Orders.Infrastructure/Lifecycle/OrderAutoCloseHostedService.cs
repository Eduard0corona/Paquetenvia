using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>ORD-AUTO-CLOSE-2026-10-10 Worker host; it never schedules anything unless enabled.</summary>
internal sealed class OrderAutoCloseHostedService(
    IJobScheduler scheduler,
    OrderAutoCloseJob job,
    IOptions<OrderAutoCloseOptions> options,
    ILogger<OrderAutoCloseHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Order auto-close is disabled.");
            return Task.CompletedTask;
        }

        return scheduler.RunAsync(job, stoppingToken);
    }
}
