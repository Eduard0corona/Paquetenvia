using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;

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

                    var messages = await RealtimeOutboxClaiming.ClaimBusinessAsync(
                        store,
                        options.Value.WorkerId,
                        lane,
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

                    await RealtimeOutboxBatchDrain.ProcessAsync(
                        messages,
                        stoppingToken,
                        OutboxDispatcherPolicy.DrainTimeout(options.Value),
                        processor.ProcessBusinessAsync);
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

                    var messages = await RealtimeOutboxClaiming.ClaimLocationAsync(
                        store,
                        options.Value.WorkerId,
                        lane,
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

                    await RealtimeOutboxBatchDrain.ProcessAsync(
                        messages,
                        stoppingToken,
                        OutboxDispatcherPolicy.DrainTimeout(options.Value),
                        processor.ProcessLocationAsync);
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

internal static class RealtimeOutboxClaiming
{
    public static Task<IReadOnlyList<ClaimedBusinessOutboxMessage>> ClaimBusinessAsync(
        IRealtimeOutboxStore store,
        string workerId,
        OutboxLaneOptions lane,
        CancellationToken cancellationToken) =>
        store.ClaimBusinessAsync(
            workerId,
            OutboxDispatcherPolicy.EffectiveClaimSize(lane),
            TimeSpan.FromSeconds(lane.LeaseSeconds),
            cancellationToken);

    public static Task<IReadOnlyList<ClaimedLocationOutboxMessage>> ClaimLocationAsync(
        IRealtimeOutboxStore store,
        string workerId,
        OutboxLaneOptions lane,
        CancellationToken cancellationToken) =>
        store.ClaimLocationAsync(
            workerId,
            OutboxDispatcherPolicy.EffectiveClaimSize(lane),
            TimeSpan.FromSeconds(lane.LeaseSeconds),
            cancellationToken);
}

internal static class RealtimeOutboxBatchDrain
{
    public static async Task ProcessAsync<T>(
        IReadOnlyList<T> messages,
        CancellationToken stoppingToken,
        TimeSpan drainTimeout,
        Func<T, CancellationToken, Task> process)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(drainTimeout, TimeSpan.Zero);

        using var drain = new CancellationTokenSource();
        using var registration = stoppingToken.Register(() => drain.CancelAfter(drainTimeout));
        var started = new List<Task>(messages.Count);
        foreach (var message in messages)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            started.Add(process(message, drain.Token));
        }

        await Task.WhenAll(started);
    }
}
