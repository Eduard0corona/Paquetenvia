using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;
using Realtime.Application.Configuration;
using Realtime.Endpoints.Connection;
using Realtime.Endpoints.Hubs;

namespace Realtime.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddRealtimeEndpoints(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRealtimeAccessTokenRedaction();
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<RealtimeOptions>>((cors, realtime) =>
                cors.AddPolicy(
                    RealtimeEndpointDefaults.CorsPolicy,
                    policy => ConfigureCors(policy, realtime.Value.AllowedOrigins)));
        // SEC-004: the Realtime.Connections limiter (negotiate and connection requests of the three hubs together) is
        // registered with every other policy by the host (Paqueteria.Api.Http.ApiRateLimitPolicies).
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
            .RequireRateLimiting(RealtimeEndpointDefaults.RateLimitPolicy)
            // SEC-004: the tracking hub's 429 is the public tracking uniform body, without traceId (Invariant 5).
            .WithMetadata(PublicTrackingRateLimitRejection.Instance);
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
            .WithHeaders(
                "Authorization",
                "Content-Type",
                "X-Requested-With",
                "X-SignalR-User-Agent")
            .AllowCredentials();
    }
}
