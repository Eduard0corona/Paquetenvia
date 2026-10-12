using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Orders.Application.Tracking;
using Paqueteria.Infrastructure.Observability;
using Realtime.Application.Authorization;
using Realtime.Application.Configuration;
using Realtime.Application.Dispatching;
using Realtime.Application.Observability;
using Realtime.Application.Publishing;
using Realtime.Infrastructure.Authorization;
using Realtime.Infrastructure.Dispatching;
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
            .Validate(options => options.TrackingMaximumConnectionLifetimeSeconds is >= 5 and <= 300,
                "Realtime:TrackingMaximumConnectionLifetimeSeconds must be between 5 and 300.")
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

    public static IServiceCollection AddRealtimeOutboxDispatchers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OutboxDispatcherOptions>()
            .Bind(configuration.GetSection(OutboxDispatcherOptions.SectionName))
            .Validate(
                OutboxDispatcherOptionsValidator.IsValid,
                "Realtime:OutboxDispatcher contains an invalid bounded option.")
            .Validate(
                options => options.Provider != OutboxDispatcherProviderKind.PostgreSql ||
                    string.Equals(
                        configuration["Realtime:Provider"],
                        "SignalR",
                        StringComparison.OrdinalIgnoreCase),
                "Realtime:OutboxDispatcher:Provider=PostgreSql requires Realtime:Provider=SignalR.")
            .Validate(
                options => options.Provider != OutboxDispatcherProviderKind.PostgreSql ||
                    string.Equals(
                        configuration["Realtime:Backplane"],
                        "InProcess",
                        StringComparison.OrdinalIgnoreCase),
                "Realtime:OutboxDispatcher:Provider=PostgreSql requires Realtime:Backplane=InProcess while GATE-013 is open.")
            // RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the dispatchers use their own least-privilege login
            // (member of paqueteria_realtime only), never the Worker credential.
            .Validate(
                options => options.Provider != OutboxDispatcherProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(
                        configuration.GetConnectionString(OutboxDispatcherOptions.ConnectionStringName)),
                $"Realtime:OutboxDispatcher:Provider=PostgreSql requires ConnectionStrings:{OutboxDispatcherOptions.ConnectionStringName}.")
            .Validate(
                options => options.Provider != OutboxDispatcherProviderKind.PostgreSql ||
                    IsDedicatedRealtimeCredential(configuration),
                $"ConnectionStrings:{OutboxDispatcherOptions.ConnectionStringName} must be a valid connection string whose login differs from ConnectionStrings:Paqueteria and ConnectionStrings:PaqueteriaWorker.")
            .ValidateOnStart();

        services.AddSingleton(_ => new RealtimeDispatcherConnectionFactory(
            configuration.GetConnectionString(OutboxDispatcherOptions.ConnectionStringName) ?? string.Empty));
        services.AddSingleton<IRealtimeOutboxStore, PostgreSqlRealtimeOutboxStore>();
        services.AddSingleton<IRealtimeOutboxEvidenceReader, PostgreSqlRealtimeOutboxEvidenceReader>();
        services.TryAddSingleton<IRealtimeOutboxFailureInjector, NoOpRealtimeOutboxFailureInjector>();
        services.AddSingleton<PublicOrderStatusPolicy>();
        services.AddSingleton<RealtimeOutboxTelemetry>();
        services.AddOutboxLaneMonitor();
        services.AddSingleton<RealtimeOutboxProcessor>();
        services.AddHostedService<BusinessOutboxDispatcher>();
        services.AddHostedService<LocationOutboxDispatcher>();
        services.AddHealthChecks()
            .AddCheck<RealtimeOutboxHealthCheck>(
                "realtime_outbox_dispatcher",
                tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the realtime credential is its own login. It must parse, name a
    /// login, and that login must not be the API's (<c>ConnectionStrings:Paqueteria</c>) or the Worker's
    /// (<c>ConnectionStrings:PaqueteriaWorker</c>) when either is configured in this host.
    /// </summary>
    internal static bool IsDedicatedRealtimeCredential(IConfiguration configuration)
    {
        var realtime = LoginOf(configuration.GetConnectionString(OutboxDispatcherOptions.ConnectionStringName));
        if (realtime is null)
        {
            return false;
        }

        foreach (var name in new[] { "Paqueteria", "PaqueteriaWorker" })
        {
            var other = configuration.GetConnectionString(name);
            if (!string.IsNullOrWhiteSpace(other) &&
                string.Equals(LoginOf(other), realtime, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? LoginOf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var username = new NpgsqlConnectionStringBuilder(connectionString).Username;
            return string.IsNullOrWhiteSpace(username) ? null : username;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException)
        {
            return null;
        }
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
