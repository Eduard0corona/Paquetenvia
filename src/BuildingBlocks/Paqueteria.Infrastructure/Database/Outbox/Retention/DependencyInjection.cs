using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the OPS-004 outbox retention job. It only calls the AI-06 purge functions with
    /// the Worker role and stays inert while <c>OutboxRetention:Enabled=false</c>.
    /// </summary>
    public static IServiceCollection AddOutboxRetention(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OutboxRetentionOptions>()
            .Bind(configuration.GetSection(OutboxRetentionOptions.SectionName))
            .Validate(
                options => !options.Enabled ||
                    !string.IsNullOrWhiteSpace(
                        configuration.GetConnectionString(OutboxRetentionOptions.ConnectionStringName)),
                $"OutboxRetention:Enabled=true requires ConnectionStrings:{OutboxRetentionOptions.ConnectionStringName}.")
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OutboxRetentionOptions>, OutboxRetentionOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        // SCL-007: the Worker's one paqueteria_worker pool, shared with every other Worker module.
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        services.AddSingleton<IOutboxPurgeGateway>(serviceProvider => new PostgreSqlOutboxPurgeGateway(
            serviceProvider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Worker),
            serviceProvider.GetRequiredService<IOptions<OutboxRetentionOptions>>().Value.CommandTimeoutSeconds));
        services.AddSingleton<OutboxRetentionTelemetry>();
        services.AddSingleton<OutboxRetentionService>();
        services.AddSingleton<OutboxRetentionJob>();
        services.TryAddSingleton<IJobScheduler, PeriodicJobScheduler>();
        services.AddHostedService<OutboxRetentionHostedService>();
        return services;
    }
}
