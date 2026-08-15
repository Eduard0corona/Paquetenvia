using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Dispatching;

internal sealed class NotificationsOutboxDispatcher(
    INotificationsStore store,
    NotificationsOutboxProcessor processor,
    IOptions<NotificationsOptions> options,
    ILogger<NotificationsOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider != NotificationsDispatcherProviderKind.PostgreSql)
        {
            return;
        }

        var nextRecovery = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextRecovery)
                {
                    var recovered = await store.RecoverStaleAsync(
                        options.Value.WorkerId,
                        options.Value.BatchSize,
                        options.Value.MaximumAttempts,
                        TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                        stoppingToken);
                    await store.RecoverStaleUnownedAsync(
                        options.Value.BatchSize,
                        options.Value.MaximumAttempts,
                        stoppingToken);
                    await ProcessBatchAsync(recovered, processor.ProcessRecoveredAsync, stoppingToken);
                    nextRecovery = DateTimeOffset.UtcNow.AddSeconds(options.Value.StaleRecoveryIntervalSeconds);
                }

                var owned = await store.ClaimNotificationsAsync(
                    options.Value.WorkerId,
                    options.Value.BatchSize,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                    stoppingToken);
                var unowned = await store.ClaimUnownedAsync(
                    options.Value.WorkerId,
                    options.Value.BatchSize,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                    stoppingToken);
                await ProcessBatchAsync(owned, processor.ProcessOwnedAsync, stoppingToken);
                await ProcessBatchAsync(unowned, processor.ProcessUnownedAsync, stoppingToken);
                if (owned.Count == 0 && unowned.Count == 0)
                {
                    await Task.Delay(options.Value.PollIntervalMilliseconds, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogError("Notifications dispatcher loop failed with outcome {Outcome}.", "LOOP_FAILURE");
                await Task.Delay(options.Value.PollIntervalMilliseconds, stoppingToken);
            }
        }
    }

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
