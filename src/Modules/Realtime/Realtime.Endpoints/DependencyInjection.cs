using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Realtime.Application.Configuration;
using Realtime.Endpoints.Connection;
using Realtime.Endpoints.Hubs;
using System.Threading.RateLimiting;

namespace Realtime.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddRealtimeEndpoints(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<RealtimeOptions>>((cors, realtime) =>
                cors.AddPolicy(
                    RealtimeEndpointDefaults.CorsPolicy,
                    policy => ConfigureCors(policy, realtime.Value.AllowedOrigins)));
        services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiter.AddPolicy(
                RealtimeEndpointDefaults.RateLimitPolicy,
                context =>
                {
                    var options = context.RequestServices.GetRequiredService<IOptions<RealtimeOptions>>().Value;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        GetPartitionKey(context),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.ConnectionPermitLimit,
                            Window = TimeSpan.FromSeconds(options.ConnectionWindowSeconds),
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        });
                });
        });
        services.AddSignalR()
            .AddJsonProtocol(protocol =>
            {
                protocol.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                protocol.PayloadSerializerOptions.DefaultIgnoreCondition =
                    JsonIgnoreCondition.WhenWritingNull;
            });
        return services;
    }

    public static IApplicationBuilder UseRealtimeConnectionGate(this IApplicationBuilder app) =>
        app.UseMiddleware<RealtimeConnectionGateMiddleware>();

    public static IApplicationBuilder UseRealtimePrivateAccessTokens(this IApplicationBuilder app) =>
        app.UseMiddleware<RealtimePrivateAccessTokenMiddleware>();

    public static IEndpointRouteBuilder MapRealtimeHubs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<OperationsHub>(RealtimeEndpointDefaults.OperationsPath)
            .RequireAuthorization(RealtimeEndpointDefaults.ActiveIdentityPolicy)
            .RequireCors(RealtimeEndpointDefaults.CorsPolicy)
            .RequireRateLimiting(RealtimeEndpointDefaults.RateLimitPolicy);
        endpoints.MapHub<DriverHub>(RealtimeEndpointDefaults.DriverPath)
            .RequireAuthorization(RealtimeEndpointDefaults.ActiveIdentityPolicy)
            .RequireCors(RealtimeEndpointDefaults.CorsPolicy)
            .RequireRateLimiting(RealtimeEndpointDefaults.RateLimitPolicy);
        endpoints.MapHub<TrackingHub>(RealtimeEndpointDefaults.TrackingPath)
            .AllowAnonymous()
            .RequireCors(RealtimeEndpointDefaults.CorsPolicy)
            .RequireRateLimiting(RealtimeEndpointDefaults.RateLimitPolicy);
        return endpoints;
    }

    private static void ConfigureCors(CorsPolicyBuilder policy, IReadOnlyCollection<string> origins)
    {
        if (origins.Count == 0)
        {
            policy.SetIsOriginAllowed(static _ => false);
            return;
        }

        policy
            .WithOrigins(origins.ToArray())
            .WithMethods("GET", "POST")
            .WithHeaders("Authorization", "Content-Type", "X-Requested-With")
            .AllowCredentials();
    }

    private static string GetPartitionKey(HttpContext context)
    {
        var identity = context.User.FindFirst("sub")?.Value;
        var source = !string.IsNullOrEmpty(identity)
            ? $"identity:{identity}"
            : $"network:{context.Connection.RemoteIpAddress}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}
