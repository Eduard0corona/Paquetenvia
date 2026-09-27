using Paqueteria.Application.Scaling;

namespace Dispatch.Infrastructure.Lifecycle;

public enum AssignmentLifecycleProviderKind
{
    Disabled,
    PostgreSql,
}

/// <summary>
/// D8-OUTBOX-LANE-DISPATCH: Worker consumer of the DISPATCH outbox lane. Disabled by default, like
/// every other Worker consumer; bounds mirror the Notifications lane.
/// </summary>
public sealed class AssignmentLifecycleOptions
{
    public const string SectionName = "Dispatch:AssignmentLifecycle";

    public AssignmentLifecycleProviderKind Provider { get; set; }
    public string WorkerId { get; set; } = "d8-dispatch-local";

    /// <summary>The identity this replica reports on <c>locked_by</c>.</summary>
    public string EffectiveWorkerId => InstanceIdentity.QualifyWorkerId(WorkerId);
    public int BatchSize { get; set; } = 10;
    public int MaximumConcurrency { get; set; } = 4;
    public int PollIntervalMilliseconds { get; set; } = 250;
    public int LeaseSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 5;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaximumSeconds { get; set; } = 60;
    public int StaleRecoveryIntervalSeconds { get; set; } = 30;

    public static bool IsValid(AssignmentLifecycleOptions value) =>
        Enum.IsDefined(value.Provider) &&
        !string.IsNullOrWhiteSpace(value.WorkerId) &&
        value.WorkerId.Length <= 100 &&
        value.BatchSize is >= 1 and <= 200 &&
        value.MaximumConcurrency is >= 1 and <= 32 &&
        value.PollIntervalMilliseconds is >= 10 and <= 60_000 &&
        value.LeaseSeconds is >= 10 and <= 600 &&
        value.MaximumAttempts is >= 1 and <= 20 &&
        value.RetryBaseSeconds is >= 1 and <= 60 &&
        value.RetryMaximumSeconds is >= 1 and <= 3_600 &&
        value.RetryMaximumSeconds >= value.RetryBaseSeconds &&
        value.StaleRecoveryIntervalSeconds is >= 1 and <= 600;
}
