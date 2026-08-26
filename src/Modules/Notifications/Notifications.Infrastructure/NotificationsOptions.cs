namespace Notifications.Infrastructure;

public enum NotificationsDispatcherProviderKind
{
    Disabled,
    PostgreSql,
}

public enum SyntheticInAppOutcome
{
    Success,
    TransientFailure,
    PermanentFailure,
    AmbiguousTimeout,
}

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public NotificationsDispatcherProviderKind Provider { get; set; }
    public string WorkerId { get; set; } = "ntf001-local";
    public int BatchSize { get; set; } = 10;
    public int MaximumConcurrency { get; set; } = 4;
    public int PollIntervalMilliseconds { get; set; } = 250;
    public int LeaseSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 5;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaximumSeconds { get; set; } = 60;
    public int StaleRecoveryIntervalSeconds { get; set; } = 30;
    public SyntheticInAppOutcome SyntheticOutcome { get; set; } = SyntheticInAppOutcome.Success;
}

internal static class NotificationsOptionsValidator
{
    public static bool IsValid(NotificationsOptions value) =>
        Enum.IsDefined(value.Provider) &&
        Enum.IsDefined(value.SyntheticOutcome) &&
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
