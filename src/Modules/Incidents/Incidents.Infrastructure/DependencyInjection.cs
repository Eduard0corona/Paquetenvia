using Incidents.Application.Incidents;
using Incidents.Infrastructure.Incidents;
using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Incidents.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIncidentsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<IncidentsOptions>()
            .Bind(configuration.GetSection(IncidentsOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.PiiProtector),
                "Incidents:PiiProtector contains an unsupported value.")
            .Validate(options => options.PiiProtector != IncidentPiiProtectorKind.Mock ||
                    IsMockProviderAllowed(environment),
                "The mock incident PII protector is DEV_SYNTHETIC_ONLY outside Development and " +
                "Testing; it is not a Staging or Production pattern.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.PiiKeyVersion) &&
                    options.PiiKeyVersion.Length <= 64,
                "Incidents:PiiKeyVersion must be a non-empty identifier of at most 64 characters.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Incidents:CommandTimeoutSeconds must be between 1 and 60.")
            // The SLA windows, the retrospective window, the tolerated skew and the evidence
            // ceiling are validated together: an incoherent combination fails the start rather
            // than opening incidents under a policy nobody approved.
            .Validate(options => options.OperationalPolicy.IsValid,
                "Incidents operational parameters are outside the bounded MVP-1 surface: the SLA " +
                "windows must be positive, ordered from CRITICAL to LOW, the retrospective window " +
                "must be 1 to 72 hours, the skew 0 to 60 minutes, and the evidence ceiling must stay between 1 and the " +
                "maximum AI-05 publishes.")
            .ValidateOnStart();

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

        services.AddSingleton<DisabledIncidentPiiProtector>();
        services.AddSingleton<DeterministicMockIncidentPiiProtector>();
        // Unconfigured means unprotected, and unprotected means INC-001 refuses to persist a
        // description at all, so the absent setting fails closed instead of leaking plaintext.
        services.AddScoped<IIncidentPiiProtector>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<IncidentsOptions>>().Value.PiiProtector switch
            {
                IncidentPiiProtectorKind.Mock =>
                    serviceProvider.GetRequiredService<DeterministicMockIncidentPiiProtector>(),
                _ => serviceProvider.GetRequiredService<DisabledIncidentPiiProtector>(),
            });
        services.AddScoped<IIncidentService, PostgreSqlIncidentService>();
        // openIncident's configurable occurrence age (OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-
        // 2026-09-27), built from the options validated above so the endpoint and the service judge
        // occurred_at with the same limits.
        services.AddSingleton(serviceProvider => new IncidentOccurrenceAgePolicy(
            serviceProvider.GetRequiredService<IOptions<IncidentsOptions>>().Value.OperationalPolicy));
        return services;
    }

    private static bool IsMockProviderAllowed(IHostEnvironment environment) =>
        environment.IsDevelopment() ||
        environment.IsEnvironment("Testing") ||
        SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName);
}
