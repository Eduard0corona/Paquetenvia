using Dispatch.Application.Assignments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application;

namespace Dispatch.Infrastructure.Lifecycle;

public static class AssignmentLifecycleErrorCodes
{
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string ReactionFailed = "REACTION_FAILED";
    public const string MaxAttemptsExhausted = "MAX_ATTEMPTS_EXHAUSTED";
}

/// <summary>
/// Processes one DISPATCH lane row: a malformed row is dead-lettered, a failed reaction is retried
/// with bounded exponential backoff and dead-lettered once its attempts are exhausted, and a
/// successful reaction settles inside the reaction transaction.
/// </summary>
public sealed class AssignmentLifecycleProcessor(
    IDispatchOutboxStore store,
    IServiceScopeFactory scopes,
    IOptions<AssignmentLifecycleOptions> options,
    IClock clock,
    ILogger<AssignmentLifecycleProcessor> logger)
{
    public async Task<AssignmentReactionOutcome?> ProcessAsync(
        ClaimedDispatchOutboxMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Topic != OrderStatusChangedFact.Topic ||
            message.AggregateType != "Order" ||
            !OrderStatusChangedFact.TryParse(
                message.Id,
                message.OwnerOrganizationId,
                message.AggregateId,
                message.AggregateVersion,
                message.Payload,
                out var fact))
        {
            await store.SettleAsync(
                message.Id,
                message.LeaseToken,
                "DEAD",
                AssignmentLifecycleErrorCodes.InvalidPayload,
                null,
                cancellationToken);
            logger.LogWarning("Dispatch lifecycle message rejected with outcome {Outcome}.", "INVALID_PAYLOAD");
            return null;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAssignmentLifecycleReactor>()
                .ReactAsync(message, fact!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning("Dispatch lifecycle reaction failed with outcome {Outcome}.", "REACTION_FAILED");
            var exhausted = message.Attempts >= options.Value.MaximumAttempts;
            await store.SettleAsync(
                message.Id,
                message.LeaseToken,
                exhausted ? "DEAD" : "RETRY",
                exhausted ? AssignmentLifecycleErrorCodes.MaxAttemptsExhausted : AssignmentLifecycleErrorCodes.ReactionFailed,
                exhausted ? null : clock.UtcNow.Add(Backoff(message.Attempts)),
                cancellationToken);
            return null;
        }
    }

    private TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(
            options.Value.RetryMaximumSeconds,
            options.Value.RetryBaseSeconds * Math.Pow(2, Math.Clamp(attempts - 1, 0, 16)));
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>D8-OUTBOX-LANE-DISPATCH Worker loop: requeue stale leases, claim, react.</summary>
public sealed class AssignmentLifecycleDispatcher(
    IDispatchOutboxStore store,
    AssignmentLifecycleProcessor processor,
    IOptions<AssignmentLifecycleOptions> options,
    ILogger<AssignmentLifecycleDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Provider != AssignmentLifecycleProviderKind.PostgreSql)
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
                    await store.RequeueStaleAsync(
                        options.Value.BatchSize,
                        options.Value.MaximumAttempts,
                        stoppingToken);
                    nextRecovery = DateTimeOffset.UtcNow.AddSeconds(options.Value.StaleRecoveryIntervalSeconds);
                }

                var claimed = await store.ClaimAsync(
                    options.Value.EffectiveWorkerId,
                    options.Value.BatchSize,
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                    stoppingToken);
                await Parallel.ForEachAsync(
                    claimed,
                    new ParallelOptions
                    {
                        CancellationToken = stoppingToken,
                        MaxDegreeOfParallelism = options.Value.MaximumConcurrency,
                    },
                    async (message, token) =>
                    {
                        try
                        {
                            await processor.ProcessAsync(message, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            logger.LogWarning(
                                "Dispatch lifecycle message processing failed with outcome {Outcome}.",
                                "MESSAGE_FAILURE");
                        }
                    });
                if (claimed.Count == 0)
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
                logger.LogError("Dispatch lifecycle loop failed with outcome {Outcome}.", "LOOP_FAILURE");
                await Task.Delay(options.Value.PollIntervalMilliseconds, stoppingToken);
            }
        }
    }
}

/// <summary>Ready only when the resolver routes the reaction topic to DISPATCH and keeps REALTIME intact.</summary>
public sealed class AssignmentLifecycleHealthCheck(
    IDispatchOutboxStore store,
    IOptions<AssignmentLifecycleOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.Provider == AssignmentLifecycleProviderKind.Disabled)
        {
            return HealthCheckResult.Healthy("Dispatch lifecycle consumer is disabled.");
        }

        try
        {
            var dispatch = await store.ResolveConsumerAsync(OrderStatusChangedFact.Topic, cancellationToken);
            var realtime = await store.ResolveConsumerAsync("orders.status-changed", cancellationToken);
            return dispatch == "DISPATCH" && realtime == "REALTIME"
                ? HealthCheckResult.Healthy("Dispatch lifecycle routing is available.")
                : HealthCheckResult.Unhealthy("Dispatch lifecycle routing returned an invalid owner.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Dispatch lifecycle routing is unavailable.", exception);
        }
    }
}
