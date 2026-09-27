using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

/// <summary>
/// OPS-004 bounded retention of terminal outbox rows. The job is off by default and, once
/// enabled, starts in dry-run so an operator sees eligible counts before anything is deleted.
/// </summary>
public sealed class OutboxRetentionOptions
{
    public const string SectionName = "OutboxRetention";

    /// <summary>
    /// The <c>ConnectionStrings</c> entry of the Worker login. Only <c>paqueteria_worker</c> holds
    /// EXECUTE on the purge functions, so the job never runs with the API credential.
    /// </summary>
    public const string ConnectionStringName = "PaqueteriaWorker";

    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Interval of the shared <see cref="PeriodicJobScheduler"/>. The first cycle runs when the
    /// Worker starts; the ceiling is the scheduler's, so an out-of-range value fails at startup.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    public int CommandTimeoutSeconds { get; set; } = 30;

    public OutboxRetentionLaneOptions Business { get; set; } = new()
    {
        ProcessedRetention = TimeSpan.FromDays(7),
        DeadRetention = TimeSpan.FromDays(30),
        BatchSize = 1_000,
        MaxBatchesPerRun = 10,
    };

    public OutboxRetentionLaneOptions Location { get; set; } = new()
    {
        ProcessedRetention = TimeSpan.FromDays(1),
        DeadRetention = TimeSpan.FromDays(7),
        BatchSize = 5_000,
        MaxBatchesPerRun = 10,
    };

    public OutboxRetentionLaneOptions For(OutboxRetentionLane lane) => lane switch
    {
        OutboxRetentionLane.Business => Business,
        OutboxRetentionLane.Location => Location,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown outbox retention lane."),
    };
}

public sealed class OutboxRetentionLaneOptions
{
    public TimeSpan ProcessedRetention { get; set; }

    public TimeSpan DeadRetention { get; set; }

    public int BatchSize { get; set; }

    /// <summary>
    /// Per-run ceiling. A run stops after this many destructive batches even when eligible rows
    /// remain, so maintenance cannot monopolize PostgreSQL; the remainder waits for the next run.
    /// </summary>
    public int MaxBatchesPerRun { get; set; }
}

public sealed class OutboxRetentionOptionsValidator : IValidateOptions<OutboxRetentionOptions>
{
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumPollInterval = PeriodicJobScheduler.MaximumInterval;
    public static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(3_650);
    public const int MaximumBatchesPerRun = 100;

    /// <summary>
    /// Cutoffs come from the Worker clock while AI-06 checks them against PostgreSQL's
    /// <c>clock_timestamp()</c>. A retention equal to the normative minimum would fail with 22023
    /// whenever the Worker clock runs even a millisecond ahead, so every retention must exceed its
    /// minimum by this margin. It is a startup rule, not a runtime correction of the cutoffs.
    /// </summary>
    public static readonly TimeSpan ClockSkewMargin = TimeSpan.FromMinutes(5);

    public ValidateOptionsResult Validate(string? name, OutboxRetentionOptions options)
    {
        var errors = Errors(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    public static IReadOnlyList<string> Errors(OutboxRetentionOptions? options)
    {
        if (options is null)
        {
            return ["OutboxRetention configuration is required."];
        }

        var errors = new List<string>();
        if (options.PollInterval < MinimumPollInterval || options.PollInterval > MaximumPollInterval)
        {
            errors.Add($"OutboxRetention:PollInterval must be between {MinimumPollInterval} and {MaximumPollInterval}.");
        }

        if (options.CommandTimeoutSeconds is < 1 or > 300)
        {
            errors.Add("OutboxRetention:CommandTimeoutSeconds must be between 1 and 300.");
        }

        foreach (var contract in OutboxRetentionLaneContract.All)
        {
            var lane = options.For(contract.Lane);
            var prefix = $"OutboxRetention:{contract.Lane}";
            if (lane is null)
            {
                errors.Add($"{prefix} is required.");
                continue;
            }

            if (lane.ProcessedRetention < contract.MinimumProcessedRetention + ClockSkewMargin || lane.ProcessedRetention > MaximumRetention)
            {
                errors.Add($"{prefix}:ProcessedRetention must be between the normative minimum {contract.MinimumProcessedRetention} plus the clock-skew margin {ClockSkewMargin} and {MaximumRetention}.");
            }

            if (lane.DeadRetention < contract.MinimumDeadRetention + ClockSkewMargin || lane.DeadRetention > MaximumRetention)
            {
                errors.Add($"{prefix}:DeadRetention must be between the normative minimum {contract.MinimumDeadRetention} plus the clock-skew margin {ClockSkewMargin} and {MaximumRetention}.");
            }

            if (lane.BatchSize < 1 || lane.BatchSize > contract.MaximumBatchSize)
            {
                errors.Add($"{prefix}:BatchSize must be between 1 and {contract.MaximumBatchSize}.");
            }

            if (lane.MaxBatchesPerRun is < 1 or > MaximumBatchesPerRun)
            {
                errors.Add($"{prefix}:MaxBatchesPerRun must be between 1 and {MaximumBatchesPerRun}.");
            }
        }

        return errors;
    }
}
