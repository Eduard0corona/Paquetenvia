namespace Orders.Application.Lifecycle;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: one DELIVERED order the job may try to close, read under FORCE RLS in the owner
/// organization's own tenant transaction. <see cref="Version"/> is the optimistic version the close must still find.
/// </summary>
public sealed record OrderAutoCloseCandidate(Guid OwnerOrganizationId, Guid OrderId, int Version);

public enum OrderAutoCloseOutcome
{
    /// <summary>The ORD-002 transition DELIVERED -> CLOSED committed with its event, outbox and audit rows.</summary>
    Closed,

    /// <summary>A CLOSED guard does not hold yet; the order stays DELIVERED and nothing was written.</summary>
    NotEligible,

    /// <summary>The order changed after it was read (closed by someone else, claimed, new version); nothing written.</summary>
    Superseded,
}

/// <summary>
/// The outcome of one attempt. <see cref="RuleCode"/> is the AI-05 rule code of the first CLOSED guard that did not
/// hold (for example <c>COD_NOT_RECONCILED</c>): a rule name, never an identifier, amount or personal datum.
/// </summary>
public sealed record OrderAutoCloseAttempt(OrderAutoCloseOutcome Outcome, string? RuleCode = null)
{
    public static OrderAutoCloseAttempt Closed { get; } = new(OrderAutoCloseOutcome.Closed);

    public static OrderAutoCloseAttempt Superseded { get; } = new(OrderAutoCloseOutcome.Superseded);

    public static OrderAutoCloseAttempt NotEligible(string? ruleCode) => new(OrderAutoCloseOutcome.NotEligible, ruleCode);
}

/// <summary>
/// Port to <c>security.list_auto_close_owner_organizations(uuid, integer)</c>, the only cross-tenant step: the owner
/// organizations that hold at least one DELIVERED order, strictly after <paramref name="afterOwnerOrganizationId"/>
/// (null starts from the first), in ascending order, at most <paramref name="limit"/>. No order identifier, status or
/// guard input crosses that boundary.
/// </summary>
public interface IOrderAutoCloseOwnerDiscovery
{
    Task<IReadOnlyList<Guid>> ListOwnersAsync(
        Guid? afterOwnerOrganizationId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// The DELIVERED orders one owner organization owns, read in that owner's tenant transaction (FORCE RLS), strictly
/// after <paramref name="afterOrderId"/>, in ascending order, at most <paramref name="limit"/>.
/// </summary>
public interface IOrderAutoCloseCandidateReader
{
    Task<IReadOnlyList<OrderAutoCloseCandidate>> ListDeliveredAsync(
        Guid ownerOrganizationId,
        Guid? afterOrderId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>One automatic close through the ORD-002 transition service, in its own owner tenant transaction.</summary>
public interface IOrderAutoCloseAttempter
{
    Task<OrderAutoCloseAttempt> CloseAsync(OrderAutoCloseCandidate candidate, CancellationToken cancellationToken);
}

public sealed record OrderAutoClosePolicy
{
    /// <summary>Equal to the bound <c>security.list_auto_close_owner_organizations</c> enforces on its limit.</summary>
    public const int MaximumBatchSize = 1_000;
    public const int MaximumBatchesPerCycle = 100;

    public OrderAutoClosePolicy(int batchSize, int maxBatchesPerCycle)
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

/// <summary>
/// <paramref name="Drained"/> is true when the cycle reached the end of the DELIVERED orders (the next cycle starts
/// over); false when it stopped at the per-cycle cap and the next cycle resumes after the last order it tried.
/// <paramref name="Failed"/> counts attempts that ended in an unexpected error; they wrote nothing and are retried
/// on a later pass.
/// </summary>
public sealed record OrderAutoCloseCycleResult(
    int Batches,
    int Attempted,
    int Closed,
    int NotEligible,
    int Superseded,
    int Failed,
    bool Drained,
    IReadOnlyDictionary<string, int> NotEligibleByRule,
    IReadOnlyList<string> FailureTypes);

/// <summary>
/// One bounded ORD-AUTO-CLOSE-2026-10-10 cycle. It walks the DELIVERED orders by owner organization and order id in
/// batches of <see cref="OrderAutoClosePolicy.BatchSize"/> and tries each one through
/// <see cref="IOrderAutoCloseAttempter"/>, which runs the same ORD-002 transition and guards as a manual close: this
/// class never decides whether an order may close. It stops at the end of the pass or after
/// <see cref="OrderAutoClosePolicy.MaxBatchesPerCycle"/> batches; in that case the next cycle resumes after the last
/// order tried, so orders whose guards do not hold yet can never starve the ones behind them.
/// </summary>
/// <remarks>
/// The resume point is an in-memory hint only: a restart, a second Worker or an overlap simply re-tries orders, and
/// the transition (row lock, optimistic version, state machine) closes each order at most once.
/// </remarks>
public sealed class OrderAutoCloseCycle(
    IOrderAutoCloseOwnerDiscovery discovery,
    IOrderAutoCloseCandidateReader reader,
    IOrderAutoCloseAttempter attempter)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OrderAutoCloseCandidate? _resumeAfter;

    public async Task<OrderAutoCloseCycleResult> RunAsync(
        OrderAutoClosePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RunExclusiveAsync(policy, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OrderAutoCloseCycleResult> RunExclusiveAsync(
        OrderAutoClosePolicy policy,
        CancellationToken cancellationToken)
    {
        var tally = new Tally();
        for (var batch = 1; batch <= policy.MaxBatchesPerCycle; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (candidates, reachedEnd) = await CollectAsync(policy.BatchSize, cancellationToken);
            if (candidates.Count == 0)
            {
                _resumeAfter = null;
                return tally.ToResult(batch - 1, drained: true);
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AttemptAsync(candidate, tally, cancellationToken);
                _resumeAfter = candidate;
            }

            if (reachedEnd)
            {
                _resumeAfter = null;
                return tally.ToResult(batch, drained: true);
            }
        }

        return tally.ToResult(policy.MaxBatchesPerCycle, drained: false);
    }

    /// <summary>
    /// Up to <paramref name="size"/> candidates from the resume point on: first the rest of the owner the previous
    /// batch stopped in, then the next owners in order. <c>ReachedEnd</c> is true when no owner is left after them.
    /// </summary>
    private async Task<(List<OrderAutoCloseCandidate> Candidates, bool ReachedEnd)> CollectAsync(
        int size,
        CancellationToken cancellationToken)
    {
        var candidates = new List<OrderAutoCloseCandidate>(size);
        Guid? afterOwner = null;
        if (_resumeAfter is { } resume)
        {
            await AddOwnerAsync(resume.OwnerOrganizationId, resume.OrderId);
            if (candidates.Count == size)
            {
                return (candidates, false);
            }

            afterOwner = resume.OwnerOrganizationId;
        }

        while (true)
        {
            var owners = await discovery.ListOwnersAsync(afterOwner, size, cancellationToken);
            if (owners.Count > size ||
                owners.Any(owner => owner == Guid.Empty || owner == afterOwner) ||
                owners.Distinct().Count() != owners.Count)
            {
                throw new InvalidOperationException("ORD_AUTO_CLOSE_DISCOVERY_OUT_OF_RANGE");
            }

            foreach (var owner in owners)
            {
                await AddOwnerAsync(owner, null);
                if (candidates.Count == size)
                {
                    return (candidates, false);
                }
            }

            if (owners.Count < size)
            {
                return (candidates, true);
            }

            afterOwner = owners[^1];
        }

        async Task AddOwnerAsync(Guid owner, Guid? afterOrder)
        {
            var limit = size - candidates.Count;
            var rows = await reader.ListDeliveredAsync(owner, afterOrder, limit, cancellationToken);
            if (rows.Count > limit ||
                rows.Any(row => row is null ||
                    row.OwnerOrganizationId != owner ||
                    row.OrderId == Guid.Empty ||
                    row.OrderId == afterOrder ||
                    row.Version < 1) ||
                rows.Select(row => row.OrderId).Distinct().Count() != rows.Count)
            {
                throw new InvalidOperationException("ORD_AUTO_CLOSE_CANDIDATES_OUT_OF_RANGE");
            }

            candidates.AddRange(rows);
        }
    }

    private async Task AttemptAsync(
        OrderAutoCloseCandidate candidate,
        Tally tally,
        CancellationToken cancellationToken)
    {
        tally.Attempted++;
        try
        {
            tally.Record(await attempter.CloseAsync(candidate, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // An unexpected error on one order must not stop the pass for the others: it wrote nothing (the
            // transition rolls back) and is retried on a later pass; the job reports the cycle as failed.
            tally.Fail(exception);
        }
    }

    private sealed class Tally
    {
        private readonly Dictionary<string, int> _notEligibleByRule = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _failureTypes = new(StringComparer.Ordinal);

        public int Attempted { get; set; }

        private int Closed { get; set; }

        private int NotEligible { get; set; }

        private int Superseded { get; set; }

        private int Failed { get; set; }

        public void Record(OrderAutoCloseAttempt attempt)
        {
            ArgumentNullException.ThrowIfNull(attempt);
            switch (attempt.Outcome)
            {
                case OrderAutoCloseOutcome.Closed:
                    Closed++;
                    break;
                case OrderAutoCloseOutcome.NotEligible:
                    NotEligible++;
                    var rule = string.IsNullOrWhiteSpace(attempt.RuleCode) ? "UNSPECIFIED" : attempt.RuleCode;
                    _notEligibleByRule[rule] = _notEligibleByRule.GetValueOrDefault(rule) + 1;
                    break;
                case OrderAutoCloseOutcome.Superseded:
                    Superseded++;
                    break;
                default:
                    throw new InvalidOperationException("ORD_AUTO_CLOSE_OUTCOME_UNKNOWN");
            }
        }

        public void Fail(Exception exception)
        {
            Failed++;
            _failureTypes.Add(exception.GetType().Name);
        }

        public OrderAutoCloseCycleResult ToResult(int batches, bool drained) =>
            new(
                batches,
                Attempted,
                Closed,
                NotEligible,
                Superseded,
                Failed,
                drained,
                new Dictionary<string, int>(_notEligibleByRule, StringComparer.Ordinal),
                _failureTypes.ToArray());
    }
}
