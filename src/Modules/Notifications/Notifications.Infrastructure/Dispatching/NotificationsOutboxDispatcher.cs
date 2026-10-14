using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scaling;
using Paqueteria.Infrastructure.Observability;

namespace Notifications.Infrastructure.Dispatching;

/// <summary>
/// NTF-001 Worker loop: recover stale leases, claim the owned and the unowned rows, process them. Between polls it
/// waits adaptively (SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14): at once after work, then from the poll interval up to the
/// maximum idle wait while both claims come back empty. Each claimed message runs in the trace that wrote it and, from
/// OBS-003-WORKER-PSEUDONYM-2026-10-14, in the organization context of its owner (pseudonym and role kind SYSTEM).
/// </summary>
internal sealed class NotificationsOutboxDispatcher(
    INotificationsStore store,
    NotificationsOutboxProcessor processor,
    IOptions<NotificationsOptions> options,
    OutboxLaneMonitor lanes,
    IOrganizationPseudonymizer pseudonymizer,
    TimeProvider timeProvider,
    ILogger<NotificationsOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider != NotificationsDispatcherProviderKind.PostgreSql)
        {
            return;
        }

        // SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14: this lane's own wait between polls.
        var idle = new OutboxIdleWait(
            options.Value.PollIntervalMilliseconds,
            options.Value.MaximumIdleWaitMilliseconds);
        var nextRecovery = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            lanes.ReportIfDue(OutboxLanes.Notifications);
            // OBS-003: each dispatch cycle is a new root trace whose id is in the cycle's log lines.
            using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Notifications);
            try
            {
                if (timeProvider.GetUtcNow() >= nextRecovery)
                {
                    var recovered = await store.RecoverStaleAsync(
                        options.Value.EffectiveWorkerId,
                        options.Value.BatchSize,
                        options.Value.MaximumAttempts,
                        TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                        stoppingToken);
                    await store.RecoverStaleUnownedAsync(
                        options.Value.BatchSize,
                        options.Value.MaximumAttempts,
                        stoppingToken);
                    RecordClaimed(recovered);
                    await ProcessBatchAsync(recovered, processor.ProcessRecoveredAsync, stoppingToken);
                    nextRecovery = timeProvider.GetUtcNow().AddSeconds(options.Value.StaleRecoveryIntervalSeconds);
                }

                var owned = await store.ClaimNotificationsAsync(
                    options.Value.EffectiveWorkerId,
                    options.Value.BatchSize,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                    stoppingToken);
                var unowned = await store.ClaimUnownedAsync(
                    options.Value.EffectiveWorkerId,
                    options.Value.BatchSize,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                    stoppingToken);
                RecordClaimed(owned);
                RecordClaimed(unowned);
                await ProcessBatchAsync(owned, processor.ProcessOwnedAsync, stoppingToken);
                await ProcessBatchAsync(unowned, processor.ProcessUnownedAsync, stoppingToken);
                // SCL-007: a poll whose claims returned work (owned or unowned, a full or a partial batch) polls again
                // at once and starts the wait over; an empty poll waits longer each time, up to the maximum idle wait.
                var wait = idle.After(owned.Count + unowned.Count);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                lanes.LoopFailed(OutboxLanes.Notifications);
                logger.LogError("Notifications dispatcher loop failed with outcome {Outcome}.", "LOOP_FAILURE");
                await Task.Delay(idle.Shortest, timeProvider, stoppingToken);
            }
        }
    }

    private void RecordClaimed(
        IReadOnlyList<Notifications.Application.Dispatching.ClaimedNotificationOutboxMessage> messages) =>
        lanes.Claimed(
            OutboxLanes.Notifications,
            messages.Count,
            messages.Count == 0 ? null : messages.Min(static message => message.AvailableAt));

    private async Task ProcessBatchAsync(
        IReadOnlyList<Notifications.Application.Dispatching.ClaimedNotificationOutboxMessage> messages,
        Func<Notifications.Application.Dispatching.ClaimedNotificationOutboxMessage, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            messages,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.Value.MaximumConcurrency,
            },
            async (message, token) =>
            {
                // OBS-003 PR 7: each message is processed in the trace that wrote it (its trace_parent), so its log
                // lines carry the trace id of the originating request; the cycle keeps its own root trace.
                using var activity = TraceCorrelation.StartOutboxMessage(OutboxLanes.Notifications, message.TraceParent);
                // OBS-003-WORKER-PSEUDONYM-2026-10-14: every line of the message carries the pseudonym of its owner
                // organization and the role kind SYSTEM (never the id; never a metric tag or an OBS-002 dimension).
                using var organization = TelemetryOrganizationContext.EnterSystem(pseudonymizer, message.OwnerOrganizationId);
                try
                {
                    await handler(message, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    logger.LogWarning("Notifications message processing failed with outcome {Outcome}.", "MESSAGE_FAILURE");
                }
            });
    }
}
