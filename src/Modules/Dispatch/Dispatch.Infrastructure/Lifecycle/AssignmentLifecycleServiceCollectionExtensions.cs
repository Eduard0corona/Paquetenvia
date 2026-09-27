using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Lifecycle;

public static class AssignmentLifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Worker consumer of the DISPATCH outbox lane (D8, D8-OUTBOX-LANE-DISPATCH). It uses
    /// only the <c>PaqueteriaWorker</c> credential, which runs as <c>paqueteria_worker</c> (NOBYPASSRLS).
    /// </summary>
    public static IServiceCollection AddDispatchAssignmentLifecycleWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AssignmentLifecycleOptions>()
            .Bind(configuration.GetSection(AssignmentLifecycleOptions.SectionName))
            .Validate(AssignmentLifecycleOptions.IsValid,
                "Dispatch:AssignmentLifecycle contains an invalid bounded option.")
            .Validate(options => options.Provider != AssignmentLifecycleProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("PaqueteriaWorker")),
                "Dispatch:AssignmentLifecycle:Provider=PostgreSql requires ConnectionStrings:PaqueteriaWorker.")
            .ValidateOnStart();
        services.AddSingleton(_ => new DispatchWorkerConnectionFactory(
            configuration.GetConnectionString("PaqueteriaWorker") ?? string.Empty));
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.AddSingleton<IDispatchOutboxStore, PostgreSqlDispatchOutboxStore>();
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<DispatchDbContext>((serviceProvider, dbOptions) =>
            dbOptions.UseNpgsql(serviceProvider.GetRequiredService<DispatchWorkerConnectionFactory>().DataSource)
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>()));
        services.TryAddScoped<WorkerTenantTransactionContext<DispatchDbContext>>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.AddScoped<IAssignmentLifecycleReactor, PostgreSqlAssignmentLifecycleReactor>();
        services.AddSingleton<AssignmentLifecycleProcessor>();
        services.AddHostedService<AssignmentLifecycleDispatcher>();
        services.AddHealthChecks().AddCheck<AssignmentLifecycleHealthCheck>(
            "dispatch_assignment_lifecycle",
            tags: ["ready"]);
        return services;
    }
}
