using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orders.Application.Tracking;

namespace Orders.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdersEndpoints(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddPublicTrackingPathRedaction();
        services.AddExceptionHandler<PublicTrackingTechnicalExceptionHandler>();
        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<PublicTrackingOptions>>((cors, tracking) =>
                cors.AddPolicy(
                    PublicTrackingEndpointDefaults.CorsPolicy,
                    policy => ConfigureCors(policy, tracking.Value.AllowedOrigins)));
        // SEC-004: the PublicTrackingLookup limiter, its uniform 429 and its counter are registered with every other
        // policy by the host (Paqueteria.Api.Http.ApiRateLimitPolicies).
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
