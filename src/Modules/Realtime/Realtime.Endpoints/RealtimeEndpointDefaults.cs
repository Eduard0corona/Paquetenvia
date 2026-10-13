using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Realtime.Application.Configuration;

namespace Realtime.Endpoints;

public static class RealtimeEndpointDefaults
{
    public const string OperationsPath = "/hubs/operations";
    public const string DriverPath = "/hubs/driver";
    public const string TrackingPath = "/hubs/tracking";
    public const string CorsPolicy = "Realtime.TrustedOrigins";
    public const string RateLimitPolicy = "Realtime.Connections";
    public const string ActiveIdentityPolicy = "Identity.Active";

    /// <summary>
    /// SEC-004: the <see cref="RateLimitPolicy"/> window, <c>Realtime:ConnectionPermitLimit</c> per
    /// <c>ConnectionWindowSeconds</c> (validated at start). The host registers the policy with every other one.
    /// </summary>
    public static (int PermitLimit, TimeSpan Window) ConnectionRateLimit(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<RealtimeOptions>>().Value;
        return (options.ConnectionPermitLimit, TimeSpan.FromSeconds(options.ConnectionWindowSeconds));
    }
}
