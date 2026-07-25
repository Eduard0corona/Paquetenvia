using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Realtime.Application.Configuration;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class BusinessOutboxDispatcher(
    IRealtimeOutboxStore store,
    RealtimeOutboxProcessor processor,
    RealtimeOutboxTelemetry telemetry,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<BusinessOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider == OutboxDispatcherProviderKind.Disabled)
        {
            logger.LogInformation(
                "realtime_outbox_dispatcher_started lane={Lane} provider={Provider}",
                "business",
                "disabled");
            logger.LogInformation(
                "realtime_outbox_dispatcher_stopped lane={Lane} provider={Provider}",
                "business",
                "disabled");
            return;
        }

        logger.LogInformation(
            "realtime_outbox_dispatcher_started lane={Lane} provider={Provider}",
            "business",
            "postgresql");
        var lane = options.Value.Business;
        var nextRequeue = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var batch = telemetry.MeasureBatch("business");
                try
                {
                    if (DateTimeOffset.UtcNow >= nextRequeue)
                    {
                        var count = await store.RequeueStaleBusinessAsync(
                            lane.BatchSize,
                            lane.MaximumAttempts,
                            stoppingToken);
                        telemetry.StaleRequeued("business", count);
                        if (count > 0)
                        {
                            logger.LogInformation(
                                "realtime_outbox_stale_requeued lane={Lane} outcome={Outcome}",
                                "business",
                                "requeued");
                        }

                        nextRequeue = DateTimeOffset.UtcNow.AddSeconds(
                            options.Value.StaleRequeueIntervalSeconds);
                    }

                    var messages = await store.ClaimBusinessAsync(
                        options.Value.WorkerId,
                        lane.BatchSize,
                        TimeSpan.FromSeconds(lane.LeaseSeconds),
                        stoppingToken);
                    telemetry.Claimed(
                        "business",
                        messages.Count,
                        messages.Count == 0 ? null : messages.Min(static message => message.CreatedAt));
                    if (messages.Count == 0)
                    {
                        await Task.Delay(lane.PollIntervalMilliseconds, stoppingToken);
                        continue;
                    }

                    await Parallel.ForEachAsync(
                        messages,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = lane.MaximumConcurrency,
                            CancellationToken = CancellationToken.None,
                        },
                        (message, token) => new ValueTask(
                            processor.ProcessBusinessAsync(message, token)));
                    logger.LogInformation(
                        "realtime_outbox_batch_completed lane={Lane} outcome={Outcome}",
                        "business",
                        "completed");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    logger.LogError(
                        "realtime_outbox_message_failed lane={Lane} error_class={ErrorClass}",
                        "business",
                        "DATABASE_TRANSIENT");
                    await Task.Delay(lane.PollIntervalMilliseconds, stoppingToken);
                }
            }
        }
        finally
        {
            logger.LogInformation(
                "realtime_outbox_dispatcher_stopped lane={Lane} provider={Provider}",
                "business",
                "postgresql");
        }
    }
}

internal sealed class LocationOutboxDispatcher(
    IRealtimeOutboxStore store,
    RealtimeOutboxProcessor processor,
    RealtimeOutboxTelemetry telemetry,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<LocationOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider == OutboxDispatcherProviderKind.Disabled)
        {
            logger.LogInformation(
                "realtime_outbox_dispatcher_started lane={Lane} provider={Provider}",
                "location",
                "disabled");
            logger.LogInformation(
                "realtime_outbox_dispatcher_stopped lane={Lane} provider={Provider}",
                "location",
                "disabled");
            return;
        }

        logger.LogInformation(
            "realtime_outbox_dispatcher_started lane={Lane} provider={Provider}",
            "location",
            "postgresql");
        var lane = options.Value.Location;
        var nextRequeue = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var batch = telemetry.MeasureBatch("location");
                try
                {
                    if (DateTimeOffset.UtcNow >= nextRequeue)
                    {
                        var count = await store.RequeueStaleLocationAsync(
                            lane.BatchSize,
                            lane.MaximumAttempts,
                            stoppingToken);
                        telemetry.StaleRequeued("location", count);
                        if (count > 0)
                        {
                            logger.LogInformation(
                                "realtime_outbox_stale_requeued lane={Lane} outcome={Outcome}",
                                "location",
                                "requeued");
                        }

                        nextRequeue = DateTimeOffset.UtcNow.AddSeconds(
                            options.Value.StaleRequeueIntervalSeconds);
                    }

                    var messages = await store.ClaimLocationAsync(
                        options.Value.WorkerId,
                        lane.BatchSize,
                        TimeSpan.FromSeconds(lane.LeaseSeconds),
                        stoppingToken);
                    telemetry.Claimed(
                        "location",
                        messages.Count,
                        messages.Count == 0 ? null : messages.Min(static message => message.CreatedAt));
                    if (messages.Count == 0)
                    {
                        await Task.Delay(lane.PollIntervalMilliseconds, stoppingToken);
                        continue;
                    }

                    await Parallel.ForEachAsync(
                        messages,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = lane.MaximumConcurrency,
                            CancellationToken = CancellationToken.None,
                        },
                        (message, token) => new ValueTask(
                            processor.ProcessLocationAsync(message, token)));
                    logger.LogInformation(
                        "realtime_outbox_batch_completed lane={Lane} outcome={Outcome}",
                        "location",
                        "completed");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    logger.LogError(
                        "realtime_outbox_message_failed lane={Lane} error_class={ErrorClass}",
                        "location",
                        "DATABASE_TRANSIENT");
                    await Task.Delay(lane.PollIntervalMilliseconds, stoppingToken);
                }
            }
        }
        finally
        {
            logger.LogInformation(
                "realtime_outbox_dispatcher_stopped lane={Lane} provider={Provider}",
                "location",
                "postgresql");
        }
    }
}
