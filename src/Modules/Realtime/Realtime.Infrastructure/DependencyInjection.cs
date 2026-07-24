using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Realtime.Application.Authorization;
using Realtime.Application.Configuration;
using Realtime.Application.Observability;
using Realtime.Application.Publishing;
using Realtime.Infrastructure.Authorization;
using Realtime.Infrastructure.Observability;
using Realtime.Infrastructure.Publishing;

namespace Realtime.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRealtimeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider),
                "Realtime:Provider must be Disabled or SignalR.")
            .Validate(options => Enum.IsDefined(options.Backplane) &&
                    options.Backplane == RealtimeBackplaneKind.InProcess,
                "Realtime:Backplane only supports InProcess while GATE-013 is open.")
            .Validate(options => options.ConnectionPermitLimit is >= 1 and <= 1_000,
                "Realtime:ConnectionPermitLimit must be between 1 and 1000.")
            .Validate(options => options.ConnectionWindowSeconds is >= 1 and <= 300,
                "Realtime:ConnectionWindowSeconds must be between 1 and 300.")
            .Validate(options => options.AuthorizationCommandTimeoutSeconds is >= 1 and <= 60,
                "Realtime:AuthorizationCommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.AuthorizationRetryCount is >= 1 and <= 3,
                "Realtime:AuthorizationRetryCount must be between 1 and 3.")
            .Validate(options => options.MaximumDriverAssignmentGroups is >= 1 and <= 500,
                "Realtime:MaximumDriverAssignmentGroups must be between 1 and 500.")
            .Validate(options => IsValidReconnectPolicy(options.ReconnectDelaysMilliseconds),
                "Realtime:ReconnectDelaysMilliseconds must be bounded and start at zero.")
            .Validate(options => options.Provider != RealtimeProviderKind.SignalR ||
                    IsValidOrigins(options.AllowedOrigins),
                "Realtime:AllowedOrigins must contain distinct trusted HTTP(S) origins when SignalR is enabled.")
            .Validate(options => options.Provider != RealtimeProviderKind.SignalR ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Realtime:Provider=SignalR requires ConnectionStrings:Paqueteria.")
            .Validate(options => options.Provider != RealtimeProviderKind.SignalR ||
                    environment.IsEnvironment("Testing") ||
                    string.Equals(
                        configuration["PublicTracking:Provider"],
                        "PostgreSql",
                        StringComparison.OrdinalIgnoreCase),
                "Realtime:Provider=SignalR requires the productive PostgreSQL public tracking projection.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A SignalR realtime provider requires a configured connection string.")));
        services.AddSingleton<DisabledRealtimeConnectionAuthorizer>();
        services.AddScoped<PostgreSqlRealtimeConnectionAuthorizer>();
        services.AddScoped<IRealtimeConnectionAuthorizer>(provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RealtimeOptions>>()
                    .Value.Provider == RealtimeProviderKind.SignalR
                ? provider.GetRequiredService<PostgreSqlRealtimeConnectionAuthorizer>()
                : provider.GetRequiredService<DisabledRealtimeConnectionAuthorizer>());
        services.AddSingleton<RealtimeTelemetry>();
        services.AddSingleton<IRealtimeTelemetry>(provider =>
            provider.GetRequiredService<RealtimeTelemetry>());
        services.AddSingleton<DisabledRealtimePublisher>();
        services.AddSingleton<SignalRRealtimePublisher>();
        services.AddSingleton<IRealtimePublisher>(provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RealtimeOptions>>()
                    .Value.Provider == RealtimeProviderKind.SignalR
                ? provider.GetRequiredService<SignalRRealtimePublisher>()
                : provider.GetRequiredService<DisabledRealtimePublisher>());
        services.AddHealthChecks()
            .AddCheck<RealtimeHealthCheck>("realtime_configuration", tags: ["ready"]);
        return services;
    }

    private static bool IsValidReconnectPolicy(IReadOnlyList<int>? delays) =>
        delays is { Count: >= 1 and <= 8 } &&
        delays[0] == 0 &&
        delays.All(static delay => delay is >= 0 and <= 60_000);

    private static bool IsValidOrigins(IReadOnlyCollection<string>? origins)
    {
        if (origins is null || origins.Count == 0 ||
            origins.Count != origins.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            return false;
        }

        foreach (var origin in origins)
        {
            if (origin.Contains('*', StringComparison.Ordinal) ||
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }
        }

        return true;
    }
}
