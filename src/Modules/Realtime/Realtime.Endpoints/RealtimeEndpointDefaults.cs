namespace Realtime.Endpoints;

public static class RealtimeEndpointDefaults
{
    public const string OperationsPath = "/hubs/operations";
    public const string DriverPath = "/hubs/driver";
    public const string TrackingPath = "/hubs/tracking";
    public const string CorsPolicy = "Realtime.TrustedOrigins";
    public const string RateLimitPolicy = "Realtime.Connections";
    public const string ActiveIdentityPolicy = "Identity.Active";
}
