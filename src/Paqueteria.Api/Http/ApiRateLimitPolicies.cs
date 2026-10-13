using Drivers.Endpoints;
using Orders.Endpoints;
using Paqueteria.Infrastructure.Security.RateLimiting;
using Realtime.Endpoints;

namespace Paqueteria.Api.Http;

/// <summary>
/// SEC-004 (phase 4 PR 2): every rate-limit policy the API serves, with its partition, its 429 headers and counter,
/// and the module accessor its numbers come from (each reads that module's validated options). Modules only name a
/// policy (<c>RequireRateLimiting</c>) and expose its window; none registers a limiter.
/// The numbers are the ones each module used before SEC-004:
/// <list type="table">
/// <item><term><c>PublicTrackingLookup</c></term><description><c>PublicTracking:LookupPermitLimit</c> per
/// <c>LookupWindowSeconds</c> (60 per 60 s) per client network; the uniform 429, with <c>Retry-After</c> like every
/// other 429 (owner decision of 2026-10-13), and the <c>public_tracking.rate_limit.rejected</c> counter.
/// </description></item>
/// <item><term><c>Realtime.Connections</c></term><description><c>Realtime:ConnectionPermitLimit</c> per
/// <c>ConnectionWindowSeconds</c> (20 per 60 s) for negotiate and connection requests of the three hubs together,
/// per subject, or per client network when anonymous.</description></item>
/// <item><term><c>Drivers.LocationIngestion</c></term><description><c>Drivers:LocationIngestion:BatchPermitLimit</c>
/// per <c>WindowSeconds</c> (30 per 60 s) per subject, or per client network when anonymous.</description></item>
/// </list>
/// Recipient calls are not a limiter policy: their limit (<c>Drivers:RecipientCalls</c>) is counted in the database
/// per driver and order, and their 429 uses the same <c>TooManyRequests</c> shape.
/// </summary>
public static class ApiRateLimitPolicies
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services) =>
        services.AddPaqueteriaRateLimiting(
            new RateLimitPolicy(
                PublicTrackingEndpointDefaults.RateLimitPolicy,
                RateLimitPartitionKind.ClientNetwork,
                static provider => Window(PublicTrackingEndpointDefaults.RateLimit(provider)))
            {
                CountRejection = PublicTrackingEndpointDefaults.CountRateLimitRejection,
            },
            new RateLimitPolicy(
                RealtimeEndpointDefaults.RateLimitPolicy,
                RateLimitPartitionKind.SubjectOrClientNetwork,
                static provider => Window(RealtimeEndpointDefaults.ConnectionRateLimit(provider))),
            new RateLimitPolicy(
                DriverLocationEndpointOptions.RateLimitPolicy,
                RateLimitPartitionKind.SubjectOrClientNetwork,
                static provider => Window(DriverLocationEndpointOptions.RateLimit(provider))));

    private static FixedWindowLimit Window((int PermitLimit, TimeSpan Window) limit) =>
        new(limit.PermitLimit, limit.Window);
}
