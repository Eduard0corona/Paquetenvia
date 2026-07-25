using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;

namespace Drivers.Endpoints;

public static class DependencyInjection
{
    public static IServiceCollection AddDriversEndpoints(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DriverLocationEndpointOptions>()
            .Bind(configuration.GetSection(DriverLocationEndpointOptions.SectionName))
            .Validate(
                value => value.BatchPermitLimit is >= 1 and <= 10_000 &&
                    value.WindowSeconds is >= 1 and <= 3600 &&
                    value.MaximumRequestBodyBytes is >= 4096 and <= 1_048_576,
                "Drivers:LocationIngestion is invalid.")
            .ValidateOnStart();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(
                DriverLocationEndpointOptions.RateLimitPolicy,
                context =>
                {
                    var configured = context.RequestServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<DriverLocationEndpointOptions>>()
                        .Value;
                    return RateLimitPartition.GetFixedWindowLimiter(
                        PartitionKey(context),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = configured.BatchPermitLimit,
                            Window = TimeSpan.FromSeconds(configured.WindowSeconds),
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            AutoReplenishment = true,
                        });
                });
        });
        return services;
    }

    private static string PartitionKey(HttpContext context)
    {
        var subject = context.User.FindFirst("sub")?.Value;
        var material = !string.IsNullOrWhiteSpace(subject)
            ? $"identity:{subject}"
            : $"network:{context.Connection.RemoteIpAddress}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

public sealed class DriverLocationEndpointOptions
{
    public const string SectionName = "Drivers:LocationIngestion";
    public const string RateLimitPolicy = "Drivers.LocationIngestion";

    public int BatchPermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 60;
    public long MaximumRequestBodyBytes { get; set; } = 32_768;
}
