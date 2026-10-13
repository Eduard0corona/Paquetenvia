using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Drivers.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddDriversEndpoints(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // SEC-004: the Drivers.LocationIngestion limiter is registered with every other policy by the host
        // (Paqueteria.Api.Http.ApiRateLimitPolicies), which reads these validated options through RateLimit.
        services.AddOptions<DriverLocationEndpointOptions>()
            .Bind(configuration.GetSection(DriverLocationEndpointOptions.SectionName))
            .Validate(
                value => value.BatchPermitLimit is >= 1 and <= 10_000 &&
                    value.WindowSeconds is >= 1 and <= 3600 &&
                    value.MaximumRequestBodyBytes is >= 4096 and <= 1_048_576,
                "Drivers:LocationIngestion is invalid.")
            .ValidateOnStart();
        return services;
    }
}

public sealed class DriverLocationEndpointOptions
{
    public const string SectionName = "Drivers:LocationIngestion";
    public const string RateLimitPolicy = "Drivers.LocationIngestion";

    public int BatchPermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 60;
    public long MaximumRequestBodyBytes { get; set; } = 32_768;

    /// <summary>
    /// SEC-004: the <see cref="RateLimitPolicy"/> window, <see cref="BatchPermitLimit"/> per <see cref="WindowSeconds"/>
    /// (validated at start). The host registers the policy with every other one.
    /// </summary>
    public static (int PermitLimit, TimeSpan Window) RateLimit(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<DriverLocationEndpointOptions>>().Value;
        return (options.BatchPermitLimit, TimeSpan.FromSeconds(options.WindowSeconds));
    }
}
