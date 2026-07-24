using Dispatch.Application.Assignments;
using Dispatch.Application.Stops;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Stops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Orders;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDispatchInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DispatchOptions>()
            .Bind(configuration.GetSection(DispatchOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Dispatch:Provider is unsupported.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.AssignmentPolicyVersion),
                "Dispatch:AssignmentPolicyVersion is required.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Dispatch:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.IdempotencyLifetimeMinutes is >= 1 and <= 10_080,
                "Dispatch:IdempotencyLifetimeMinutes must be between 1 and 10080.")
            .Validate(options => options.Provider != DispatchProviderKind.PostgreSql ||
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Dispatch:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .Validate(options => options.Provider != DispatchProviderKind.PostgreSql ||
                string.Equals(
                    configuration["Drivers:Provider"],
                    "PostgreSql",
                    StringComparison.OrdinalIgnoreCase),
                "Dispatch:Provider=PostgreSql requires Drivers:Provider=PostgreSql.")
            .ValidateOnStart();

        services.AddOptions<DispatchDriverEligibilityOptions>()
            .Bind(configuration.GetSection("Drivers:Eligibility"))
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL dispatch provider requires a configured connection string.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<DispatchDbContext>((serviceProvider, dbOptions) =>
        {
            var dispatchOptions = serviceProvider.GetRequiredService<IOptions<DispatchOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.CommandTimeout(dispatchOptions.CommandTimeoutSeconds);
                        postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable(
                            "__ef_migrations_history_dispatch",
                            "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<DispatchDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.TryAddSingleton<IDispatchAssignmentAuthorizer, DispatchAssignmentAuthorizer>();
        services.TryAddSingleton<OrderTransitionGuardRegistry>();
        services.TryAddSingleton<IAssignmentFailureInjector, NoOpAssignmentFailureInjector>();
        services.TryAddScoped<IDispatchAuthorizationReader, PostgreSqlDispatchAuthorizationReader>();
        services.TryAddScoped<IDispatchDriverEligibilityReader, PostgreSqlDispatchDriverEligibilityReader>();
        services.TryAddScoped<IAssignmentReplayEvidenceReader, PostgreSqlAssignmentReplayEvidenceReader>();
        services.AddSingleton<DisabledAssignmentService>();
        services.AddSingleton<DisabledDriverStopsQuery>();
        services.AddScoped<PostgreSqlAssignmentToOrderCoordinator>();
        services.AddScoped<PostgreSqlDriverStopsQuery>();
        services.AddScoped<IAssignmentService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<DispatchOptions>>().Value.Provider switch
            {
                DispatchProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlAssignmentToOrderCoordinator>(),
                _ => serviceProvider.GetRequiredService<DisabledAssignmentService>(),
            });
        services.AddScoped<IDriverStopsQuery>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<DispatchOptions>>().Value.Provider switch
            {
                DispatchProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlDriverStopsQuery>(),
                _ => serviceProvider.GetRequiredService<DisabledDriverStopsQuery>(),
            });
        return services;
    }
}
