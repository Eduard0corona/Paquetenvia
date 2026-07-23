using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Orders;
using Orders.Application.Tracking;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Tracking;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OrdersOptions>()
            .Bind(configuration.GetSection(OrdersOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Orders:Provider is unsupported.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Orders:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.PageSize is >= 1 and <= 200,
                "Orders:PageSize must be between 1 and 200.")
            .Validate(options => options.IdempotencyLifetimeMinutes is >= 1 and <= 10_080,
                "Orders:IdempotencyLifetimeMinutes must be between 1 and 10080.")
            .Validate(options => options.PublicIdCollisionRetryCount is >= 1 and <= 10,
                "Orders:PublicIdCollisionRetryCount must be between 1 and 10.")
            .Validate(options => options.ClaimWindowHours is >= 1 and <= 720,
                "Orders:ClaimWindowHours must be between 1 and 720.")
            .Validate(options => options.TransitionMetadataMaximumBytes is >= 256 and <= 16_384,
                "Orders:TransitionMetadataMaximumBytes must be between 256 and 16384.")
            .Validate(options => options.Provider != OrdersProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Orders:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        var section = configuration.GetSection(PublicTrackingOptions.SectionName);
        services
            .AddOptions<PublicTrackingOptions>()
            .Bind(section)
            .Validate(options => Enum.IsDefined(options.Provider),
                "PublicTracking:Provider must be Disabled or PostgreSql.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "PublicTracking:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != PublicTrackingProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "PublicTracking:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.AddSingleton<DisabledPublicTrackingProjectionReader>();
        services.AddScoped<PostgreSqlPublicTrackingProjectionReader>();
        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL public tracking provider requires a configured connection string.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<OrdersDbContext>((serviceProvider, dbOptions) =>
        {
            var ordersOptions = serviceProvider.GetRequiredService<IOptions<OrdersOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.CommandTimeout(ordersOptions.CommandTimeoutSeconds);
                        postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<OrdersDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.TryAddSingleton<IOrderPublicIdGenerator, CryptographicOrderPublicIdGenerator>();
        services.TryAddSingleton<IOrderCreationFailureInjector, NoOpOrderCreationFailureInjector>();
        services.TryAddSingleton<IOrderTransitionFailureInjector, NoOpOrderTransitionFailureInjector>();
        services.TryAddSingleton<IOrderTransitionAuthorizer, OrderTransitionAuthorizer>();
        services.TryAddSingleton<OrderTransitionGuardRegistry>();
        services.TryAddScoped<IOrderTransitionAuthorizationReader, PostgreSqlOrderTransitionAuthorizationReader>();
        services.TryAddScoped<IOrderTransitionReplayAuthorizationReader,
            PostgreSqlOrderTransitionReplayAuthorizationReader>();
        services.TryAddScoped<IOrderQuoteAcceptanceGuardReader, PostgreSqlOrderQuoteAcceptanceGuardReader>();
        services.TryAddScoped<IOrderAssignmentGuardReader, PostgreSqlOrderAssignmentGuardReader>();
        services.TryAddScoped<IOrderProofGuardReader, PostgreSqlOrderProofGuardReader>();
        services.TryAddScoped<IOrderIncidentGuardReader, PostgreSqlOrderIncidentGuardReader>();
        services.TryAddScoped<IOrderCodGuardReader, PostgreSqlOrderCodGuardReader>();
        services.AddSingleton<DisabledOrderService>();
        services.AddSingleton<DisabledOrderTransitionService>();
        services.AddScoped<QuoteSnapshotToOrderCoordinator>();
        services.AddScoped<PostgreSqlOrderTransitionService>();
        services.AddScoped<IOrderService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<OrdersOptions>>().Value.Provider switch
            {
                OrdersProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<QuoteSnapshotToOrderCoordinator>(),
                _ => serviceProvider.GetRequiredService<DisabledOrderService>(),
            });
        services.AddScoped<IOrderTransitionService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<OrdersOptions>>().Value.Provider switch
            {
                OrdersProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlOrderTransitionService>(),
                _ => serviceProvider.GetRequiredService<DisabledOrderTransitionService>(),
            });
        services.AddScoped<IPublicTrackingProjectionReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PublicTrackingOptions>>().Value.Provider switch
            {
                PublicTrackingProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlPublicTrackingProjectionReader>(),
                _ => serviceProvider.GetRequiredService<DisabledPublicTrackingProjectionReader>(),
            });

        return services;
    }
}
