using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Paqueteria.Application;
using Paqueteria.Application.Idempotency;

namespace Paqueteria.Infrastructure;

/// <summary>
/// AI-05 <c>x-offline-operation-age</c> setting. Only the tolerance for a client clock running
/// ahead is configurable (0 to 300 seconds, default 300); the 72-hour maximum age is fixed by
/// OPS-003-OFFLINE-72H and has no setting.
/// </summary>
public sealed class OfflineOperationOptions
{
    public const string SectionName = "OfflineOperations";

    public int ClockToleranceSeconds { get; set; } =
        (int)OfflineOperationAgePolicy.DefaultClockTolerance.TotalSeconds;

    internal static bool IsValid(OfflineOperationOptions options) =>
        options.ClockToleranceSeconds >= 0 &&
        options.ClockToleranceSeconds <= OfflineOperationAgePolicy.MaximumClockTolerance.TotalSeconds;
}

public static class OfflineOperationRegistration
{
    /// <summary>
    /// Registers the shared <see cref="OfflineOperationAgePolicy"/> once, however many modules ask
    /// for it, and validates the tolerance at startup so a bad value never reaches a request.
    /// </summary>
    public static IServiceCollection AddOfflineOperationAgePolicy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(OfflineOperationAgePolicy)))
        {
            return services;
        }

        services.AddOptions<OfflineOperationOptions>()
            .Bind(configuration.GetSection(OfflineOperationOptions.SectionName))
            .Validate(
                OfflineOperationOptions.IsValid,
                "OfflineOperations:ClockToleranceSeconds must be between 0 and 300.")
            .ValidateOnStart();
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(serviceProvider => new OfflineOperationAgePolicy(TimeSpan.FromSeconds(
            serviceProvider.GetRequiredService<IOptions<OfflineOperationOptions>>().Value.ClockToleranceSeconds)));
        return services;
    }
}
