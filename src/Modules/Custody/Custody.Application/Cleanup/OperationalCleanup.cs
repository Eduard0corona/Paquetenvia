namespace Custody.Application.Cleanup;

/// <summary>
/// OPS-003 port to the two OPS-003-CLEANUP-ROLE functions. Each call is one bounded, atomic,
/// cross-tenant batch executed by <c>paqueteria_cleanup_executor</c>; only a count comes back.
/// </summary>
public interface IOperationalCleanupGateway
{
    /// <summary>
    /// <c>security.purge_expired_idempotency_keys</c>: deletes (or, in dry-run, counts) at most
    /// <paramref name="batchSize"/> keys that expired before <paramref name="expiredBefore"/> and are
    /// older than the fixed 72-hour floor the function enforces itself.
    /// </summary>
    Task<int> PurgeExpiredIdempotencyKeysAsync(
        DateTimeOffset expiredBefore,
        int batchSize,
        bool dryRun,
        CancellationToken cancellationToken);

    /// <summary><c>security.expire_proof_upload_sessions</c>: marks at most <paramref name="batchSize"/> sessions EXPIRED.</summary>
    Task<int> ExpireProofUploadSessionsAsync(int batchSize, CancellationToken cancellationToken);
}

public static class OperationalCleanupLimits
{
    public const int MaximumIdempotencyBatchSize = 5_000;
    public const int MaximumSessionBatchSize = 1_000;
    public const int MaximumBatchesPerCycle = 100;
}

/// <summary>
/// One cycle's bounds. A destructive cycle runs at most <see cref="MaxBatchesPerCycle"/> batches;
/// a dry-run is always a single call, because repeating it would only count the same rows again.
/// </summary>
public sealed record OperationalCleanupPolicy
{
    public OperationalCleanupPolicy(int batchSize, int maximumBatchSize, int maxBatchesPerCycle, bool dryRun)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, maximumBatchSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchesPerCycle, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBatchesPerCycle, OperationalCleanupLimits.MaximumBatchesPerCycle);
        BatchSize = batchSize;
        MaxBatchesPerCycle = maxBatchesPerCycle;
        DryRun = dryRun;
    }

    public int BatchSize { get; }

    public int MaxBatchesPerCycle { get; }

    public bool DryRun { get; }
}

/// <summary><paramref name="Drained"/> is true when a batch came back shorter than the batch size.</summary>
public sealed record OperationalCleanupCycleResult(int Batches, long Affected, bool Drained);

/// <summary>
/// Batches until one comes back short or the per-cycle cap is reached. The database mutation is the
/// idempotency mechanism, so a cycle may always be repeated after a failure or a restart.
/// </summary>
public static class OperationalCleanupCycle
{
    public static async Task<OperationalCleanupCycleResult> RunAsync(
        OperationalCleanupPolicy policy,
        Func<int, CancellationToken, Task<int>> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(batch);
        var ceiling = policy.DryRun ? 1 : policy.MaxBatchesPerCycle;
        var affected = 0L;
        for (var index = 1; index <= ceiling; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await batch(policy.BatchSize, cancellationToken);
            if (count < 0 || count > policy.BatchSize)
            {
                throw new InvalidOperationException("OPS003_CLEANUP_COUNT_OUT_OF_RANGE");
            }

            affected += count;
            if (count < policy.BatchSize)
            {
                return new OperationalCleanupCycleResult(index, affected, Drained: true);
            }
        }

        return new OperationalCleanupCycleResult(ceiling, affected, Drained: false);
    }
}
