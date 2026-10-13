using Paqueteria.Application.Scaling;

namespace Realtime.Application.Configuration;

public enum OutboxDispatcherProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Realtime:OutboxDispatcher";

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the connection string of the dispatchers' own login, a member of
    /// <see cref="DatabaseRole"/> only. The Worker credential (<c>ConnectionStrings:PaqueteriaWorker</c>) is never used.
    /// </summary>
    public const string ConnectionStringName = "PaqueteriaRealtime";

    /// <summary>The NOLOGIN NOBYPASSRLS role every dispatcher transaction assumes with <c>SET LOCAL ROLE</c>.</summary>
    public const string DatabaseRole = "paqueteria_realtime";

    public OutboxDispatcherProviderKind Provider { get; set; } = OutboxDispatcherProviderKind.Disabled;
    public string WorkerId { get; set; } = "rtm002-local";

    /// <summary>
    /// The identity this replica reports on <c>locked_by</c>. Claims stay safe across replicas
    /// through <c>FOR UPDATE SKIP LOCKED</c> and the lease token; qualifying the configured id
    /// keeps every claim attributable to the replica that made it.
    /// </summary>
    public string EffectiveWorkerId => InstanceIdentity.QualifyWorkerId(WorkerId);
    public int PublishTimeoutSeconds { get; set; } = 5;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaximumSeconds { get; set; } = 60;
    public int StaleRequeueIntervalSeconds { get; set; } = 30;
    public OutboxLaneOptions Business { get; set; } = new()
    {
        BatchSize = 10,
        MaximumConcurrency = 4,
        PollIntervalMilliseconds = 250,
        MaximumIdleWaitMilliseconds = 1_000,
        LeaseSeconds = 120,
        MaximumAttempts = 10,
    };

    public OutboxLaneOptions Location { get; set; } = new()
    {
        BatchSize = 25,
        MaximumConcurrency = 8,
        PollIntervalMilliseconds = 500,
        MaximumIdleWaitMilliseconds = 1_000,
        LeaseSeconds = 120,
        MaximumAttempts = 5,
    };
}

public sealed class OutboxLaneOptions
{
    public int BatchSize { get; set; }
    public int MaximumConcurrency { get; set; }

    /// <summary>The shortest wait after an empty claim, and the wait after a failed loop iteration.</summary>
    public int PollIntervalMilliseconds { get; set; }

    /// <summary>
    /// SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13: the longest wait between two claims while they come back empty. The
    /// wait starts at <see cref="PollIntervalMilliseconds"/> and doubles after each empty claim up to this value
    /// (<see cref="Realtime.Application.Dispatching.OutboxIdleWait"/>). The owner chose 1 s on both lanes.
    /// </summary>
    public int MaximumIdleWaitMilliseconds { get; set; }

    public int LeaseSeconds { get; set; }
    public int MaximumAttempts { get; set; }
}

public static class OutboxDispatcherOptionsValidator
{
    public const int SettlementSafetyMarginSeconds = 5;

    /// <summary>AI-15 <c>event_delivery_p95_seconds</c> warning (2 s), the delivery budget of both realtime lanes.</summary>
    public const int EventDeliveryBudgetMilliseconds = 2_000;

    /// <summary>
    /// SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13: the highest accepted <see cref="OutboxLaneOptions.MaximumIdleWaitMilliseconds"/>,
    /// half of <see cref="EventDeliveryBudgetMilliseconds"/>. A notice that arrives while its lane is idle can wait this long
    /// before the claim that takes it, so the other half is kept for that claim, the evidence reads, the SignalR publish
    /// and the settle. It is also the owner's value (1 s); a longer wait needs a new owner decision.
    /// </summary>
    public const int MaximumIdleWaitCeilingMilliseconds = EventDeliveryBudgetMilliseconds / 2;

    public static bool IsValid(OutboxDispatcherOptions? options)
    {
        if (options is null ||
            !Enum.IsDefined(options.Provider) ||
            string.IsNullOrWhiteSpace(options.WorkerId) ||
            options.WorkerId.Length is < 3 or > 100 ||
            options.PublishTimeoutSeconds is < 1 or > 60 ||
            options.RetryBaseSeconds is < 1 or > 60 ||
            options.RetryMaximumSeconds < options.RetryBaseSeconds ||
            options.RetryMaximumSeconds > 3_600 ||
            options.StaleRequeueIntervalSeconds is < 1 or > 3_600)
        {
            return false;
        }

        return IsValidLane(options.Business, options.PublishTimeoutSeconds) &&
            IsValidLane(options.Location, options.PublishTimeoutSeconds);
    }

    private static bool IsValidLane(OutboxLaneOptions? lane, int publishTimeoutSeconds) =>
        lane is not null &&
        lane.BatchSize is >= 1 and <= 100 &&
        lane.MaximumConcurrency is >= 1 and <= 32 &&
        lane.PollIntervalMilliseconds is >= 50 and <= 60_000 &&
        lane.MaximumIdleWaitMilliseconds >= lane.PollIntervalMilliseconds &&
        lane.MaximumIdleWaitMilliseconds <= MaximumIdleWaitCeilingMilliseconds &&
        lane.LeaseSeconds is >= 15 and <= 600 &&
        lane.MaximumAttempts is >= 1 and <= 100 &&
        lane.LeaseSeconds >= publishTimeoutSeconds + SettlementSafetyMarginSeconds;
}

public static class OutboxDispatcherPolicy
{
    public static int EffectiveClaimSize(OutboxLaneOptions lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        return Math.Min(lane.BatchSize, lane.MaximumConcurrency);
    }

    public static TimeSpan DrainTimeout(OutboxDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TimeSpan.FromSeconds(
            options.PublishTimeoutSeconds +
            OutboxDispatcherOptionsValidator.SettlementSafetyMarginSeconds);
    }
}
