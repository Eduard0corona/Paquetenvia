using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

/// <summary>
/// Schedules one bounded retention cycle every <c>OutboxRetention:PollInterval</c>. A failed
/// cycle is reported and simply retried on the next schedule; it never stops the Worker.
/// </summary>
internal sealed class OutboxRetentionHostedService(
    OutboxRetentionService service,
    IOptions<OutboxRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Outbox retention is disabled; no purge function will be called.");
            return;
        }

        logger.LogInformation(
            "Outbox retention scheduled with dry_run={DryRun} poll_interval={PollInterval} initial_delay={InitialDelay}.",
            settings.DryRun,
            settings.PollInterval,
            settings.InitialDelay);
        try
        {
            await Task.Delay(settings.InitialDelay, timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await service.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Lane failures are isolated and reported by the service; this only guards
                    // the schedule so an unexpected fault cannot stop the host.
                    logger.LogError("Outbox retention cycle failed with outcome {Outcome}.", "CYCLE_FAILURE");
                }

                await Task.Delay(settings.PollInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
