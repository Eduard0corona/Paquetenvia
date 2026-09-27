using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Csv;
using Orders.Application.Lifecycle;
using Orders.Application.Orders;
using Orders.Application.Tracking;
using Orders.Infrastructure.Csv;
using Orders.Infrastructure.Lifecycle;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Tracking;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Scheduling;
using Paqueteria.Infrastructure.Tenancy;
using Paqueteria.Contracts.Tracking;

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

        // ORD-002 evaluates ASSIGNED and re-delivery with the same DSP-002 policy section.
        services.AddOptions<OrderTransitionDriverEligibilityOptions>()
            .Bind(configuration.GetSection(OrderTransitionDriverEligibilityOptions.SectionName));

        var section = configuration.GetSection(PublicTrackingOptions.SectionName);
        services
            .AddOptions<PublicTrackingOptions>()
            .Bind(section)
            .Validate(options => Enum.IsDefined(options.Provider),
                "PublicTracking:Provider must be Disabled or PostgreSql.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "PublicTracking:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => double.IsFinite(options.TokenLifetimeHours) &&
                    options.TokenLifetimeHours >= 5d / 60d &&
                    options.TokenLifetimeHours <= 24d * 30d,
                "PublicTracking:TokenLifetimeHours must be between 5 minutes and 30 days.")
            .Validate(options => options.TokenCollisionRetryCount is >= 1 and <= 10,
                "PublicTracking:TokenCollisionRetryCount must be between 1 and 10.")
            .Validate(options => options.LookupPermitLimit is >= 1 and <= 1_000,
                "PublicTracking:LookupPermitLimit must be between 1 and 1000.")
            .Validate(options => options.LookupWindowSeconds is >= 1 and <= 300,
                "PublicTracking:LookupWindowSeconds must be between 1 and 300.")
            .Validate(options => AreValidOrigins(options.AllowedOrigins),
                "PublicTracking:AllowedOrigins must contain distinct absolute HTTP(S) origins.")
            .Validate(options => options.Provider != PublicTrackingProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "PublicTracking:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.AddSingleton<DisabledPublicTrackingProjectionReader>();
        services.AddSingleton<DisabledPublicTrackingTokenService>();
        services.AddSingleton<PublicTrackingTelemetry>();
        services.AddSingleton<IPublicTrackingTelemetry>(serviceProvider =>
            serviceProvider.GetRequiredService<PublicTrackingTelemetry>());
        services.AddScoped<PostgreSqlPublicTrackingProjectionReader>();
        services.AddScoped<PostgreSqlPublicTrackingTokenService>();
        services.TryAddSingleton<TrackingTokenHasher>();
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
        services.TryAddScoped<IOrderCustodyGuardReader, PostgreSqlOrderCustodyGuardReader>();
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
        services.AddSingleton<DisabledCsvOrderImportBatchIdempotencyStore>();
        services.AddScoped<PostgreSqlCsvOrderImportBatchIdempotencyStore>();
        services.AddScoped<ICsvOrderImportBatchIdempotencyStore>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<OrdersOptions>>().Value.Provider switch
            {
                OrdersProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlCsvOrderImportBatchIdempotencyStore>(),
                _ => serviceProvider.GetRequiredService<DisabledCsvOrderImportBatchIdempotencyStore>(),
            });
        services.AddScoped<ICsvOrderImportCommitService>(serviceProvider =>
            new CsvOrderImportCommitService(
                serviceProvider.GetRequiredService<IOrderService>(),
                serviceProvider.GetRequiredService<ICsvOrderImportBatchIdempotencyStore>()));
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
        services.AddScoped<IPublicTrackingTokenService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PublicTrackingOptions>>().Value.Provider switch
            {
                PublicTrackingProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlPublicTrackingTokenService>(),
                _ => serviceProvider.GetRequiredService<DisabledPublicTrackingTokenService>(),
            });

        return services;
    }

    /// <summary>
    /// LIF-001/ADR-034 Worker composition: the claim-window finalization job behind
    /// <see cref="IJobScheduler"/>. It stays idle unless <c>Orders:ClaimWindowFinalization:Enabled</c>.
    /// </summary>
    public static IServiceCollection AddOrdersClaimWindowFinalization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ClaimWindowFinalizationOptions>()
            .Bind(configuration.GetSection(ClaimWindowFinalizationOptions.SectionName))
            .Validate(ClaimWindowFinalizationOptions.IsValid,
                "Orders:ClaimWindowFinalization contains an invalid bounded option.")
            .Validate(options => !options.Enabled ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString(
                        ClaimWindowFinalizationOptions.WorkerConnectionStringName)),
                "Orders:ClaimWindowFinalization:Enabled requires ConnectionStrings:PaqueteriaWorker.")
            .ValidateOnStart();
        services.AddSingleton(_ => new OrdersWorkerDataSource(
            configuration.GetConnectionString(ClaimWindowFinalizationOptions.WorkerConnectionStringName) ?? string.Empty));
        services.AddSingleton<IExpiredClaimWindowFinalizer>(serviceProvider =>
            new PostgreSqlExpiredClaimWindowFinalizer(
                serviceProvider.GetRequiredService<OrdersWorkerDataSource>().DataSource));
        services.AddSingleton<ClaimWindowFinalizationCycle>();
        services.AddSingleton<ClaimWindowFinalizationTelemetry>();
        services.AddSingleton<ClaimWindowFinalizationJob>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobScheduler, PeriodicJobScheduler>();
        services.AddHostedService<ClaimWindowFinalizationHostedService>();
        services.AddHealthChecks().AddCheck<ClaimWindowFinalizationHealthCheck>(
            "orders_claim_window_finalization",
            tags: ["ready"]);
        return services;
    }

    private static bool AreValidOrigins(IReadOnlyCollection<string>? origins)
    {
        if (origins is null ||
            origins.Count != origins.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            return false;
        }

        return origins.All(static origin =>
            !origin.Contains('*', StringComparison.Ordinal) &&
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            uri.AbsolutePath == "/" &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment));
    }
}
