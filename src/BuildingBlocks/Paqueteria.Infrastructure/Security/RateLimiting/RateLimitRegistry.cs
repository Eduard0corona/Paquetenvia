using System.Collections.Frozen;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Infrastructure.Security.RateLimiting;

/// <summary>How a policy partitions its budget. A partition key is never logged, traced or used as a metric tag.</summary>
public enum RateLimitPartitionKind
{
    /// <summary>The client network (<see cref="ClientNetworkPartition"/>): the IPv4 address or the IPv6 /64.</summary>
    ClientNetwork,

    /// <summary>
    /// The hash of the authenticated subject (<see cref="SubjectPartition"/>); the client network when the request
    /// carries no subject (the limiter runs before authorization, so an anonymous call to a private endpoint still
    /// spends a budget, its network's).
    /// </summary>
    SubjectOrClientNetwork,
}

/// <summary>A fixed window: <see cref="PermitLimit"/> requests per <see cref="Window"/>, no queue.</summary>
public readonly record struct FixedWindowLimit(int PermitLimit, TimeSpan Window);

/// <summary>
/// One named rate-limit policy. <see cref="Limit"/> reads the numbers from validated options on every request (the
/// owning module's, or the host's <c>RateLimits</c> section); a partition keeps the numbers it was created with.
/// </summary>
/// <param name="Name">
/// The name endpoints pass to <c>RequireRateLimiting</c>; also the only metric tag and the only value of the OBS-002
/// <c>Policy</c> log dimension.
/// </param>
public sealed record RateLimitPolicy(
    string Name,
    RateLimitPartitionKind Partition,
    Func<IServiceProvider, FixedWindowLimit> Limit)
{
    /// <summary>
    /// An extra module counter run on each rejection (public tracking keeps its own). It gets the request services
    /// only, never the request, so it cannot read the user, the address or the path.
    /// </summary>
    public Action<IServiceProvider>? CountRejection { get; init; }

    /// <summary>
    /// SEC-004 PR 3: the registered policy whose fixed window this one spends instead of a window of its own, so
    /// endpoints under two policies share one budget per partition (CSV preview and commit, owner answer Q1). The
    /// owner must have its own window, the same partition kind and the same <see cref="Limit"/> delegate. A rejection
    /// by the shared window is counted under the owner's name.
    /// </summary>
    public string? SharesWindowOf { get; init; }

    /// <summary>
    /// SEC-004 PR 3: at most this many requests of this policy in flight at once per partition ("1 importación a la
    /// vez", owner answer Q1). A request over it is rejected under this policy's name, with this policy's window as
    /// <c>Retry-After</c>, and spends nothing from the window. Null: no in-flight rule.
    /// </summary>
    public int? InFlightLimit { get; init; }
}

/// <summary>
/// SEC-004 (phase 4 PR 2): the one registry of the API's rate-limit policies. It holds the named policies and their
/// partition keys, and its single rejection writer (<see cref="RateLimitRejectionWriter"/>) answers every 429. Since
/// PR 3 it also holds the windows two policies share and the in-flight rules (<see cref="RateLimitBudgets"/>).
/// </summary>
public sealed class RateLimitPolicyRegistry
{
    private readonly FrozenDictionary<string, RateLimitPolicy> _policies;
    private readonly FrozenSet<string> _windowOwners;

    public RateLimitPolicyRegistry(IEnumerable<RateLimitPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var byName = new Dictionary<string, RateLimitPolicy>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentException.ThrowIfNullOrWhiteSpace(policy.Name);
            ArgumentNullException.ThrowIfNull(policy.Limit);
            if (!Enum.IsDefined(policy.Partition))
            {
                throw new ArgumentOutOfRangeException(nameof(policies), policy.Partition, "Unknown partition kind.");
            }

            if (policy.InFlightLimit is < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(policies),
                    policy.InFlightLimit,
                    $"Rate-limit policy {policy.Name} needs an in-flight limit of at least 1.");
            }

            if (!byName.TryAdd(policy.Name, policy))
            {
                throw new ArgumentException($"Rate-limit policy {policy.Name} is registered twice.", nameof(policies));
            }
        }

        if (byName.Count == 0)
        {
            throw new ArgumentException("At least one rate-limit policy is required.", nameof(policies));
        }

        foreach (var policy in byName.Values.Where(policy => policy.SharesWindowOf is not null))
        {
            if (!byName.TryGetValue(policy.SharesWindowOf!, out var owner) || ReferenceEquals(owner, policy))
            {
                throw new ArgumentException(
                    $"Rate-limit policy {policy.Name} shares the window of itself or of an unregistered policy.",
                    nameof(policies));
            }

            if (owner.SharesWindowOf is not null ||
                owner.Partition != policy.Partition ||
                !ReferenceEquals(owner.Limit, policy.Limit))
            {
                throw new ArgumentException(
                    $"Rate-limit policy {policy.Name} must share the window of a policy with its own window, the same partition kind and the same limit.",
                    nameof(policies));
            }
        }

        _policies = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _windowOwners = byName.Values
            .Where(policy => policy.SharesWindowOf is not null)
            .Select(policy => policy.SharesWindowOf!)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<RateLimitPolicy> Policies => _policies.Values;

    /// <summary>
    /// Whether the policy's state lives in the registry's <see cref="RateLimitBudgets"/> (it shares a window, owns a
    /// shared window or has an in-flight rule) instead of the framework's own fixed window partition.
    /// </summary>
    public bool UsesBudgets(RateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.SharesWindowOf is not null ||
            policy.InFlightLimit is not null ||
            _windowOwners.Contains(policy.Name);
    }

    public RateLimitPolicy? Find(string? name) =>
        name is not null && _policies.TryGetValue(name, out var policy) ? policy : null;

    /// <summary>The policy an endpoint requires (<c>RequireRateLimiting(name)</c>), if any.</summary>
    public RateLimitPolicy? For(Endpoint? endpoint) =>
        Find(endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
}

/// <summary>The partition key of a request under a policy: always an opaque hash, never an address or subject.</summary>
public static class RateLimitPartitionKeys
{
    /// <summary>The subject claim of every authentication scheme the API accepts (AuthCenter and the mock).</summary>
    public const string SubjectClaim = "sub";

    public static string For(RateLimitPartitionKind kind, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return kind switch
        {
            RateLimitPartitionKind.ClientNetwork => ClientNetworkPartition.Key(context.Connection.RemoteIpAddress),
            RateLimitPartitionKind.SubjectOrClientNetwork =>
                context.User.FindFirst(SubjectClaim)?.Value is { } subject && !string.IsNullOrWhiteSpace(subject)
                    ? SubjectPartition.Key(subject)
                    : ClientNetworkPartition.Key(context.Connection.RemoteIpAddress),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown partition kind."),
        };
    }
}

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>
    /// The only <c>AddRateLimiter</c> of the solution (an architecture test keeps it so): registers
    /// <paramref name="policies"/> as fixed windows without a queue, partitioned by
    /// <see cref="RateLimitPartitionKeys"/>, and <see cref="RateLimitRejectionWriter"/> as the one rejection writer.
    /// The host calls it once with the full policy table.
    /// </summary>
    public static IServiceCollection AddPaqueteriaRateLimiting(
        this IServiceCollection services,
        params RateLimitPolicy[] policies)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RateLimitPolicyRegistry)))
        {
            throw new InvalidOperationException("The rate-limit policies are registered once, by the host.");
        }

        var registry = new RateLimitPolicyRegistry(policies);
        services.AddSingleton(registry);
        services.AddSingleton(_ => new RateLimitBudgets(registry));
        services.AddMetrics();
        services.AddSingleton<RateLimitTelemetry>();
        // OBS-002 (owner answer Q8): one summary line per policy and minute with rejections; registry names only.
        services.AddRateLimitRejectionSummary(registry.Policies.Select(policy => policy.Name));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = RateLimitRejectionWriter.WriteAsync;
            foreach (var policy in registry.Policies)
            {
                options.AddPolicy(policy.Name, context => Partition(registry, policy, context));
            }
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(
        RateLimitPolicyRegistry registry,
        RateLimitPolicy policy,
        HttpContext context)
    {
        var limit = policy.Limit(context.RequestServices);
        var key = RateLimitPartitionKeys.For(policy.Partition, context);
        if (registry.UsesBudgets(policy))
        {
            var budgets = context.RequestServices.GetRequiredService<RateLimitBudgets>();
            return RateLimitPartition.Get(key, partition => budgets.Limiter(policy, partition, limit));
        }

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => FixedWindow(limit));
    }

    /// <summary>Every window of the registry: a fixed window without a queue, replenished automatically.</summary>
    internal static FixedWindowRateLimiterOptions FixedWindow(FixedWindowLimit limit) => new()
    {
        PermitLimit = limit.PermitLimit,
        Window = limit.Window,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    };
}
