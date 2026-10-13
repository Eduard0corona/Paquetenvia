using System.Collections.Frozen;
using System.Diagnostics;
using System.Threading.RateLimiting;

namespace Paqueteria.Infrastructure.Security.RateLimiting;

/// <summary>
/// SEC-004 PR 3: the limiter state the registry keeps itself, for what the framework's per-policy partitions cannot
/// express: a fixed window that two policies spend together (CSV preview and commit share 10 per minute, owner
/// answer Q1) and an in-flight rule ("1 importación a la vez": one CSV commit per user at a time). There is one
/// partitioned limiter per shared window and one per in-flight rule, keyed by the same opaque partition key as every
/// other policy; each drops its idle partitions itself. The framework holds a <see cref="BudgetLimiter"/> per policy
/// and partition, a view over this state. The host disposes it.
/// </summary>
public sealed class RateLimitBudgets : IDisposable
{
    private readonly FrozenDictionary<string, PartitionedRateLimiter<WindowRequest>> _windows;
    private readonly FrozenDictionary<string, PartitionedRateLimiter<InFlightRequest>> _inFlight;

    public RateLimitBudgets(RateLimitPolicyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var policies = registry.Policies.Where(registry.UsesBudgets).ToArray();
        _windows = policies
            .Select(WindowOwner)
            .Distinct(StringComparer.Ordinal)
            .ToFrozenDictionary(
                static name => name,
                static _ => PartitionedRateLimiter.Create<WindowRequest, string>(
                    static request => RateLimitPartition.GetFixedWindowLimiter(
                        request.Key,
                        _ => RateLimitingServiceCollectionExtensions.FixedWindow(request.Limit)),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        _inFlight = policies
            .Where(static policy => policy.InFlightLimit is not null)
            .ToFrozenDictionary(
                static policy => policy.Name,
                static _ => PartitionedRateLimiter.Create<InFlightRequest, string>(
                    static request => RateLimitPartition.GetConcurrencyLimiter(
                        request.Key,
                        _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = request.PermitLimit,
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        }),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    /// <summary>The policy whose window <paramref name="policy"/> spends: its own, or the one it shares.</summary>
    public static string WindowOwner(RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.SharesWindowOf ?? policy.Name;
    }

    /// <summary>The limiter the framework holds for one partition of <paramref name="policy"/>.</summary>
    internal RateLimiter Limiter(RateLimitPolicy policy, string key, FixedWindowLimit limit)
    {
        var owner = WindowOwner(policy);
        var inFlight = policy.InFlightLimit is { } permits
            ? new InFlightRule(_inFlight[policy.Name], new InFlightRequest(key, permits), policy.Name)
            : null;
        return new BudgetLimiter(inFlight, _windows[owner], new WindowRequest(key, limit), owner);
    }

    public void Dispose()
    {
        foreach (var limiter in _inFlight.Values)
        {
            limiter.Dispose();
        }

        foreach (var limiter in _windows.Values)
        {
            limiter.Dispose();
        }
    }

    internal readonly record struct WindowRequest(string Key, FixedWindowLimit Limit);

    internal readonly record struct InFlightRequest(string Key, int PermitLimit);

    private sealed record InFlightRule(
        PartitionedRateLimiter<InFlightRequest> Limiter,
        InFlightRequest Request,
        string Policy);

    /// <summary>
    /// One partition of a budget policy: first its in-flight permit (if it has the rule), then a permit of the window
    /// it spends. A rejection names the policy that rejected it (<see cref="RateLimitLeaseMetadata.RejectedBy"/>):
    /// the in-flight policy, or the window's owner with the window's own <c>Retry-After</c>. A request the in-flight
    /// rule rejects spends nothing from the window. The state is in <see cref="RateLimitBudgets"/>, so disposing this
    /// view loses nothing; it reports itself idle once no lease it gave out is outstanding, and the framework then
    /// drops it.
    /// </summary>
    private sealed class BudgetLimiter(
        InFlightRule? inFlight,
        PartitionedRateLimiter<WindowRequest> window,
        WindowRequest windowRequest,
        string windowOwner) : RateLimiter
    {
        private long _lastActivity = Stopwatch.GetTimestamp();
        private int _outstanding;

        public override TimeSpan? IdleDuration =>
            Volatile.Read(ref _outstanding) > 0 ? null : Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity));

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            RateLimitLease? held = null;
            if (inFlight is not null)
            {
                held = inFlight.Limiter.AttemptAcquire(inFlight.Request, permitCount);
                if (!held.IsAcquired)
                {
                    held.Dispose();
                    return new RejectedLease(inFlight.Policy, retryAfter: null);
                }
            }

            var spent = window.AttemptAcquire(windowRequest, permitCount);
            if (!spent.IsAcquired)
            {
                TimeSpan? retryAfter = spent.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : null;
                spent.Dispose();
                held?.Dispose();
                return new RejectedLease(windowOwner, retryAfter);
            }

            Interlocked.Increment(ref _outstanding);
            return new AcquiredLease(this, spent, held);
        }

        // No queue anywhere (QueueLimit 0): waiting is attempting.
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult(AttemptAcquireCore(permitCount));

        private void Released()
        {
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            Interlocked.Decrement(ref _outstanding);
        }

        private sealed class AcquiredLease(BudgetLimiter owner, RateLimitLease window, RateLimitLease? inFlight)
            : RateLimitLease
        {
            private int _disposed;

            public override bool IsAcquired => true;

            public override IEnumerable<string> MetadataNames => [];

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                metadata = null;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                inFlight?.Dispose();
                window.Dispose();
                owner.Released();
            }
        }
    }

    private sealed class RejectedLease(string rejectedBy, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => retryAfter is null
            ? [RateLimitLeaseMetadata.RejectedBy.Name]
            : [RateLimitLeaseMetadata.RejectedBy.Name, MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = metadataName == RateLimitLeaseMetadata.RejectedBy.Name
                ? rejectedBy
                : metadataName == MetadataName.RetryAfter.Name ? retryAfter : null;
            return metadata is not null;
        }
    }
}

/// <summary>Metadata of a rejected lease from the registry's budgets.</summary>
public static class RateLimitLeaseMetadata
{
    /// <summary>
    /// The registered policy that rejected the request when it is not the endpoint's own (the shared window's owner,
    /// or the in-flight rule's policy). A fixed name from the registry, never a partition key.
    /// </summary>
    public static MetadataName<string> RejectedBy { get; } = MetadataName.Create<string>("paqueteria.rate_limit.rejected_by");
}
