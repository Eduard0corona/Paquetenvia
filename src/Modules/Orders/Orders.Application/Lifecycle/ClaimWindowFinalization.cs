namespace Orders.Application.Lifecycle;

/// <summary>
/// ADR-034 port to <c>security.finalize_expired_orders(integer)</c>. One call atomically
/// finalizes at most <paramref name="batchSize"/> CLOSED orders whose claim window elapsed
/// strictly before the database clock, across tenants, and returns how many it finalized.
/// </summary>
public interface IExpiredClaimWindowFinalizer
{
    Task<int> FinalizeExpiredBatchAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed record ClaimWindowFinalizationPolicy
{
    public const int MaximumBatchSize = 1_000;
    public const int MaximumBatchesPerCycle = 100;

    public ClaimWindowFinalizationPolicy(int batchSize, int maxBatchesPerCycle)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaximumBatchSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatchesPerCycle, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBatchesPerCycle, MaximumBatchesPerCycle);
        BatchSize = batchSize;
        MaxBatchesPerCycle = maxBatchesPerCycle;
    }

    public int BatchSize { get; }

    public int MaxBatchesPerCycle { get; }
}

/// <summary><paramref name="Drained"/> is true when a batch found nothing left to finalize.</summary>
public sealed record ClaimWindowFinalizationCycleResult(int Batches, int Finalized, bool Drained);

/// <summary>
/// One bounded LIF-001 cycle: batches until one finalizes nothing or the per-cycle cap is reached.
/// The database mutation is the idempotency mechanism, so a cycle may always be repeated.
/// </summary>
public sealed class ClaimWindowFinalizationCycle(IExpiredClaimWindowFinalizer finalizer)
{
    public async Task<ClaimWindowFinalizationCycleResult> RunAsync(
        ClaimWindowFinalizationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var finalized = 0;
        for (var batch = 1; batch <= policy.MaxBatchesPerCycle; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await finalizer.FinalizeExpiredBatchAsync(policy.BatchSize, cancellationToken);
            if (count < 0 || count > policy.BatchSize)
            {
                throw new InvalidOperationException("LIF001_FINALIZED_COUNT_OUT_OF_RANGE");
            }

            if (count == 0)
            {
                return new ClaimWindowFinalizationCycleResult(batch, finalized, Drained: true);
            }

            finalized += count;
        }

        return new ClaimWindowFinalizationCycleResult(policy.MaxBatchesPerCycle, finalized, Drained: false);
    }
}
