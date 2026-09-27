using Custody.Application.Cleanup;
using Custody.Application.ProofUploads;
using Custody.Infrastructure.Cleanup;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.Proofs;
using Custody.Infrastructure.ProofStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Scheduling;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCustodyInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool addValidationWorker = false)
    {
        var isDevelopmentOrTesting =
            environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.AddOptions<ProofStorageOptions>()
            .Bind(configuration.GetSection(ProofStorageOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "ProofStorage:Provider is unsupported.")
            .Validate(options => Enum.IsDefined(options.ThreatScanner),
                "ProofStorage:ThreatScanner is unsupported.")
            .Validate(options => options.UploadUrlLifetimeMinutes is >= 1 and <= 30,
                "ProofStorage:UploadUrlLifetimeMinutes must be between 1 and 30.")
            .Validate(options => options.DownloadUrlLifetimeMinutes is >= 1 and <= 15,
                "ProofStorage:DownloadUrlLifetimeMinutes must be between 1 and 15.")
            .Validate(options => options.SessionLifetimeMinutes is >= 2 and <= 1_440,
                "ProofStorage:SessionLifetimeMinutes must be between 2 and 1440.")
            .Validate(options => options.ProcessingIntervalSeconds is >= 1 and <= 300,
                "ProofStorage:ProcessingIntervalSeconds must be between 1 and 300.")
            .Validate(options => options.StaleValidationSeconds is >= 1 and <= 86_400,
                "ProofStorage:StaleValidationSeconds must be between 1 and 86400.")
            .Validate(options => options.MaximumConcurrency is >= 1 and <= 32,
                "ProofStorage:MaximumConcurrency must be between 1 and 32.")
            .Validate(options => options.MaximumBytes is >= 1 and <= 100 * 1024 * 1024,
                "ProofStorage:MaximumBytes must be between 1 and 104857600.")
            .Validate(options =>
                    options.MaximumTextBytes is >= 1 and <= 64 * 1024 &&
                    options.MaximumTextBytes <= options.MaximumBytes,
                "ProofStorage:MaximumTextBytes must be between 1 and 65536 and not exceed MaximumBytes.")
            .Validate(options =>
                    options.Provider != ProofStorageProvider.S3Compatible ||
                    (IsAbsoluteHttpUri(options.ServiceUrl) &&
                     IsAbsoluteHttpUri(options.PublicPresignUrl) &&
                     !string.IsNullOrWhiteSpace(options.Region) &&
                     !string.IsNullOrWhiteSpace(options.Bucket)),
                "ProofStorage:S3Compatible requires service URLs, region and bucket.")
            .Validate(options =>
                    options.QuarantinePrefix == "quarantine/" &&
                    options.FinalPrefix == "proofs/",
                "ProofStorage prefixes are fixed by POD-001.")
            .Validate(options =>
                    isDevelopmentOrTesting ||
                    options.ThreatScanner != ProofThreatScannerProvider.Synthetic,
                "The synthetic proof scanner is restricted to Development and Testing.")
            .Validate(options =>
                    isDevelopmentOrTesting ||
                    options.Provider != ProofStorageProvider.S3Compatible ||
                    (new Uri(options.ServiceUrl).Scheme == Uri.UriSchemeHttps &&
                     new Uri(options.PublicPresignUrl).Scheme == Uri.UriSchemeHttps),
                "ProofStorage:S3Compatible requires HTTPS outside Development and Testing.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "Custody PostgreSQL requires ConnectionStrings:Paqueteria.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<CustodyDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.MigrationsAssembly(typeof(CustodyDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_custody", "platform");
                        postgres.UseNetTopologySuite();
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<CustodyDbContext>>();
        services.AddScoped<WorkerTenantTransactionContext<CustodyDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddOfflineOperationAgePolicy(configuration);
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.AddSingleton<DisabledProofObjectStorage>();
        services.AddSingleton<S3CompatibleProofObjectStorage>();
        services.AddSingleton<IProofObjectStorage>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<ProofStorageOptions>>().Value.Provider switch
            {
                ProofStorageProvider.S3Compatible =>
                    serviceProvider.GetRequiredService<S3CompatibleProofObjectStorage>(),
                _ => serviceProvider.GetRequiredService<DisabledProofObjectStorage>(),
            });
        services.AddSingleton<DisabledProofThreatScanner>();
        services.AddSingleton<StrictSyntheticThreatScanner>();
        services.AddSingleton<IProofThreatScanner>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<ProofStorageOptions>>()
                .Value.ThreatScanner switch
            {
                ProofThreatScannerProvider.Synthetic =>
                    serviceProvider.GetRequiredService<StrictSyntheticThreatScanner>(),
                _ => serviceProvider.GetRequiredService<DisabledProofThreatScanner>(),
            });
        services.AddSingleton<IProofTelemetry, ProofTelemetry>();
        services.AddScoped<IProofValidationProcessor, ProofValidationProcessor>();
        services.AddScoped<IProofUploadSessionService, PostgreSqlProofUploadSessionService>();
        services.AddScoped<IProofFinalizationService, PostgreSqlProofFinalizationService>();
        services.AddScoped<IProofDownloadService, PostgreSqlProofDownloadService>();
        services.AddHealthChecks()
            .AddCheck<ProofStorageHealthCheck>("proof_storage", tags: ["ready"])
            .AddCheck<ProofScannerHealthCheck>("proof_scanner", tags: ["ready"]);
        if (addValidationWorker)
        {
            services.AddHealthChecks()
                .AddCheck<CustodyWorkerReadinessHealthCheck>(
                    "custody_worker_database_role",
                    tags: ["ready"]);
            services.AddHostedService<ProofValidationWorker>();
        }

        return services;
    }

    /// <summary>
    /// OPS-003 Worker composition: the idempotency-key purge and the proof upload-session expiry on
    /// the shared <see cref="IJobScheduler"/>. Both stay idle unless enabled under
    /// <c>OperationalCleanup</c>, and only reach the OPS-003-CLEANUP-ROLE functions as the Worker role.
    /// </summary>
    public static IServiceCollection AddCustodyOperationalCleanup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<OperationalCleanupOptions>()
            .Bind(configuration.GetSection(OperationalCleanupOptions.SectionName))
            .Validate(
                options => OperationalCleanupOptions.Errors(options).Count == 0,
                "OperationalCleanup contains an invalid bounded option.")
            .Validate(
                options => !options.AnyEnabled ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString(
                        OperationalCleanupOptions.WorkerConnectionStringName)),
                "OperationalCleanup requires ConnectionStrings:PaqueteriaWorker when a job is enabled.")
            .ValidateOnStart();
        services.AddSingleton(_ => new OperationalCleanupDataSource(
            configuration.GetConnectionString(OperationalCleanupOptions.WorkerConnectionStringName) ?? string.Empty));
        services.AddSingleton<IOperationalCleanupGateway>(serviceProvider =>
        {
            var dataSource = serviceProvider.GetRequiredService<OperationalCleanupDataSource>();
            return new PostgreSqlOperationalCleanupGateway(
                () => dataSource.Value,
                serviceProvider.GetRequiredService<IOptions<OperationalCleanupOptions>>().Value.CommandTimeoutSeconds);
        });
        services.AddSingleton<OperationalCleanupTelemetry>();
        services.AddSingleton<IdempotencyKeyPurgeJob>();
        services.AddSingleton<ProofUploadSessionExpiryJob>();
        services.AddSingleton<BffSessionPurgeJob>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobScheduler, PeriodicJobScheduler>();
        services.AddHostedService<OperationalCleanupHostedService>();
        services.AddHealthChecks().AddCheck<OperationalCleanupHealthCheck>(
            "custody_operational_cleanup",
            tags: ["ready"]);
        return services;
    }

    private static bool IsAbsoluteHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";
}
