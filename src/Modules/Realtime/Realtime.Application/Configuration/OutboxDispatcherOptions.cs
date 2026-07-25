namespace Realtime.Application.Configuration;

public enum OutboxDispatcherProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Realtime:OutboxDispatcher";

    public OutboxDispatcherProviderKind Provider { get; set; } = OutboxDispatcherProviderKind.Disabled;
    public string WorkerId { get; set; } = "rtm002-local";
    public int PublishTimeoutSeconds { get; set; } = 5;
    public int RetryBaseSeconds { get; set; } = 2;
    public int RetryMaximumSeconds { get; set; } = 60;
    public int StaleRequeueIntervalSeconds { get; set; } = 30;
    public OutboxLaneOptions Business { get; set; } = new()
    {
        BatchSize = 10,
        MaximumConcurrency = 4,
        PollIntervalMilliseconds = 250,
        LeaseSeconds = 120,
        MaximumAttempts = 10,
    };

    public OutboxLaneOptions Location { get; set; } = new()
    {
        BatchSize = 25,
        MaximumConcurrency = 8,
        PollIntervalMilliseconds = 500,
        LeaseSeconds = 120,
        MaximumAttempts = 5,
    };
}

public sealed class OutboxLaneOptions
{
    public int BatchSize { get; set; }
    public int MaximumConcurrency { get; set; }
    public int PollIntervalMilliseconds { get; set; }
    public int LeaseSeconds { get; set; }
    public int MaximumAttempts { get; set; }
}

public static class OutboxDispatcherOptionsValidator
{
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

        return IsValidLane(options.Business) && IsValidLane(options.Location);
    }

    private static bool IsValidLane(OutboxLaneOptions? lane) =>
        lane is not null &&
        lane.BatchSize is >= 1 and <= 100 &&
        lane.MaximumConcurrency is >= 1 and <= 32 &&
        lane.PollIntervalMilliseconds is >= 50 and <= 60_000 &&
        lane.LeaseSeconds is >= 15 and <= 600 &&
        lane.MaximumAttempts is >= 1 and <= 100;
}
