using System.Collections.Frozen;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Paqueteria.Application.Security;

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
/// One named rate-limit policy. <see cref="Limit"/> reads the numbers from the owning module's validated options on
/// every request, as each module did before SEC-004; a partition keeps the numbers it was created with.
/// </summary>
/// <param name="Name">The name endpoints pass to <c>RequireRateLimiting</c>; also the only metric dimension.</param>
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
}

/// <summary>
/// SEC-004 (phase 4 PR 2): the one registry of the API's rate-limit policies. It holds the named policies and their
/// partition keys, and its single rejection writer (<see cref="RateLimitRejectionWriter"/>) answers every 429.
/// </summary>
public sealed class RateLimitPolicyRegistry
{
    private readonly FrozenDictionary<string, RateLimitPolicy> _policies;

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

            if (!byName.TryAdd(policy.Name, policy))
            {
                throw new ArgumentException($"Rate-limit policy {policy.Name} is registered twice.", nameof(policies));
            }
        }

        if (byName.Count == 0)
        {
            throw new ArgumentException("At least one rate-limit policy is required.", nameof(policies));
        }

        _policies = byName.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<RateLimitPolicy> Policies => _policies.Values;

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
        services.AddMetrics();
        services.AddSingleton<RateLimitTelemetry>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = RateLimitRejectionWriter.WriteAsync;
            foreach (var policy in registry.Policies)
            {
                options.AddPolicy(policy.Name, context => Partition(policy, context));
            }
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(RateLimitPolicy policy, HttpContext context)
    {
        var limit = policy.Limit(context.RequestServices);
        return RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitionKeys.For(policy.Partition, context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }
}
