using Incidents.Application.Incidents;
using Incidents.Infrastructure.Incidents;
using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Incidents.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIncidentsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "Incidents PostgreSQL requires ConnectionStrings:Paqueteria.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<IncidentsDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.MigrationsAssembly(typeof(IncidentsDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_incidents", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<IncidentsDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.AddScoped<IIncidentService, PostgreSqlIncidentService>();
        return services;
    }
}
