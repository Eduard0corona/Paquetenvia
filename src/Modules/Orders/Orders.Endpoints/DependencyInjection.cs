using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orders.Application.Tracking;
using Paqueteria.Application.Security;
using System.Threading.RateLimiting;

namespace Orders.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdersEndpoints(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddExceptionHandler<PublicTrackingTechnicalExceptionHandler>();
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<PublicTrackingOptions>>((cors, tracking) =>
                cors.AddPolicy(
                    PublicTrackingEndpointDefaults.CorsPolicy,
                    policy => ConfigureCors(policy, tracking.Value.AllowedOrigins)));
        services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiter.OnRejected = static async (context, cancellationToken) =>
            {
                if (!PublicTrackingEndpointDefaults.IsLookupPath(
                        context.HttpContext.Request.Path))
                {
                    return;
                }

                context.HttpContext.RequestServices
                    .GetRequiredService<IPublicTrackingTelemetry>()
                    .RateLimitRejected();
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new PublicTrackingProblemResponse(
                        "about:blank",
                        "Too Many Requests",
                        StatusCodes.Status429TooManyRequests),
                    cancellationToken);
            };
            rateLimiter.AddPolicy(
                PublicTrackingEndpointDefaults.RateLimitPolicy,
                context =>
                {
                    var options = context.RequestServices
                        .GetRequiredService<IOptions<PublicTrackingOptions>>()
                        .Value;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        ClientNetworkPartition.Key(context.Connection.RemoteIpAddress),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.LookupPermitLimit,
                            Window = TimeSpan.FromSeconds(options.LookupWindowSeconds),
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        });
                });
        });
        return services;
    }

    private static void ConfigureCors(
        CorsPolicyBuilder policy,
        IReadOnlyCollection<string> origins)
    {
        if (origins.Count == 0)
        {
            policy.SetIsOriginAllowed(static _ => false);
            return;
        }

        policy
            .WithOrigins(origins.ToArray())
            .WithMethods("GET")
            .WithHeaders("Accept");
    }
}

internal sealed class PublicTrackingTechnicalExceptionHandler(
    IPublicTrackingTelemetry telemetry) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not PublicTrackingInfrastructureException)
        {
            return false;
        }

        telemetry.LookupFailed("provider_unavailable");
        await Results.Json(
            new PublicTrackingProblemResponse(
                "about:blank",
                "Service Unavailable",
                StatusCodes.Status503ServiceUnavailable),
            statusCode: StatusCodes.Status503ServiceUnavailable,
            contentType: "application/problem+json").ExecuteAsync(httpContext);
        return true;
    }
}
