using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Persistence;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Security;
using Paqueteria.Infrastructure.Tenancy;

namespace Notifications.Infrastructure.RecipientNotices.Panel;

public static class RecipientNoticePanelDependencyInjection
{
    /// <summary>
    /// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007) API composition: <c>listOrderRecipientNotices</c> and
    /// <c>stopOrderRecipientNotices</c> on the API's one <c>paqueteria_app</c> pool. AI-24 gives them no switch: the list
    /// reads what exists (nothing while <c>Notifications:RecipientNotices:Enabled</c> is false) and the stop needs the
    /// contact lookup key (503 without it). The destination phone and e-mail are opened only through the Locations
    /// openers with the PII key; nothing here reaches the notice key, the Notifications lane or a provider.
    /// </summary>
    public static IServiceCollection AddRecipientNoticePanel(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddContactLookup(configuration);
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Application);
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<NotificationsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                    serviceProvider.GetRuntimeDataSource(RuntimeDatabaseRole.Application),
                    postgres =>
                    {
                        postgres.MigrationsAssembly(typeof(NotificationsDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_notifications", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>()));
        services.AddScoped<TenantTransactionContext<NotificationsDbContext>>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IRecipientNoticePanelStore, PostgreSqlRecipientNoticePanelStore>();
        services.AddScoped<IRecipientNoticePanelService, RecipientNoticePanelService>();
        return services;
    }
}
