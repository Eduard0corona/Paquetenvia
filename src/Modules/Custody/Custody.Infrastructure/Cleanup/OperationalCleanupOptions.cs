using Custody.Application.Cleanup;
using Paqueteria.Infrastructure.Scheduling;

namespace Custody.Infrastructure.Cleanup;

/// <summary>
/// OPS-003 Worker settings. Both jobs are off until explicitly enabled on a Worker with
/// <c>ConnectionStrings:PaqueteriaWorker</c>, and the idempotency purge starts in dry-run so an
/// operator sees eligible counts before anything is deleted. The 72-hour floor is not a setting:
/// the database function enforces it whatever the job passes.
/// </summary>
public sealed class OperationalCleanupOptions
{
    public const string SectionName = "OperationalCleanup";
    public const string WorkerConnectionStringName = "PaqueteriaWorker";
    public const int MaximumPollIntervalSeconds = 3_600;
    public const int MaximumCommandTimeoutSeconds = 300;

    public int CommandTimeoutSeconds { get; set; } = 30;

    public IdempotencyKeyCleanupOptions IdempotencyKeys { get; set; } = new();

    public ProofUploadSessionCleanupOptions ProofUploadSessions { get; set; } = new();

    public BffSessionCleanupOptions BffSessions { get; set; } = new();

    public bool AnyEnabled => IdempotencyKeys.Enabled || ProofUploadSessions.Enabled || BffSessions.Enabled;

    public static IReadOnlyList<string> Errors(OperationalCleanupOptions? options)
    {
        if (options is null)
        {
            return ["OperationalCleanup configuration is required."];
        }

        var errors = new List<string>();
        if (options.CommandTimeoutSeconds is < 1 or > MaximumCommandTimeoutSeconds)
        {
            errors.Add($"OperationalCleanup:CommandTimeoutSeconds must be between 1 and {MaximumCommandTimeoutSeconds}.");
        }

        if (options.IdempotencyKeys is not { } keys)
        {
            errors.Add("OperationalCleanup:IdempotencyKeys is required.");
        }
        else
        {
            Validate("IdempotencyKeys", keys.PollIntervalSeconds, keys.BatchSize,
                OperationalCleanupLimits.MaximumIdempotencyBatchSize, keys.MaxBatchesPerCycle, errors);
        }

        if (options.ProofUploadSessions is not { } sessions)
        {
            errors.Add("OperationalCleanup:ProofUploadSessions is required.");
        }
        else
        {
            Validate("ProofUploadSessions", sessions.PollIntervalSeconds, sessions.BatchSize,
                OperationalCleanupLimits.MaximumSessionBatchSize, sessions.MaxBatchesPerCycle, errors);
        }

        if (options.BffSessions is not { } bff)
        {
            errors.Add("OperationalCleanup:BffSessions is required.");
        }
        else
        {
            Validate("BffSessions", bff.PollIntervalSeconds, bff.BatchSize,
                OperationalCleanupLimits.MaximumBffSessionBatchSize, bff.MaxBatchesPerCycle, errors);
        }

        return errors;
    }

    private static void Validate(
        string name,
        int pollIntervalSeconds,
        int batchSize,
        int maximumBatchSize,
        int maxBatchesPerCycle,
        List<string> errors)
    {
        if (pollIntervalSeconds < 1 || pollIntervalSeconds > PeriodicJobScheduler.MaximumInterval.TotalSeconds)
        {
            errors.Add($"OperationalCleanup:{name}:PollIntervalSeconds must be between 1 and {MaximumPollIntervalSeconds}.");
        }

        if (batchSize < 1 || batchSize > maximumBatchSize)
        {
            errors.Add($"OperationalCleanup:{name}:BatchSize must be between 1 and {maximumBatchSize}.");
        }

        if (maxBatchesPerCycle < 1 || maxBatchesPerCycle > OperationalCleanupLimits.MaximumBatchesPerCycle)
        {
            errors.Add($"OperationalCleanup:{name}:MaxBatchesPerCycle must be between 1 and {OperationalCleanupLimits.MaximumBatchesPerCycle}.");
        }
    }
}

public sealed class IdempotencyKeyCleanupOptions
{
    public bool Enabled { get; set; }

    public bool DryRun { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 900;

    public int BatchSize { get; set; } = 1_000;

    public int MaxBatchesPerCycle { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public OperationalCleanupPolicy ToPolicy() =>
        new(BatchSize, OperationalCleanupLimits.MaximumIdempotencyBatchSize, MaxBatchesPerCycle, DryRun);
}

public sealed class ProofUploadSessionCleanupOptions
{
    public bool Enabled { get; set; }

    public int PollIntervalSeconds { get; set; } = 60;

    public int BatchSize { get; set; } = 500;

    public int MaxBatchesPerCycle { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public OperationalCleanupPolicy ToPolicy() =>
        new(BatchSize, OperationalCleanupLimits.MaximumSessionBatchSize, MaxBatchesPerCycle, dryRun: false);
}

/// <summary>
/// BFF-SESSION-TABLE-SHAPE purge of revoked and expired BFF sessions. Off until enabled; there is no
/// dry-run because the function never reaches a live session.
/// </summary>
public sealed class BffSessionCleanupOptions
{
    public bool Enabled { get; set; }

    public int PollIntervalSeconds { get; set; } = 300;

    public int BatchSize { get; set; } = 500;

    public int MaxBatchesPerCycle { get; set; } = 10;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    public OperationalCleanupPolicy ToPolicy() =>
        new(BatchSize, OperationalCleanupLimits.MaximumBffSessionBatchSize, MaxBatchesPerCycle, dryRun: false);
}
