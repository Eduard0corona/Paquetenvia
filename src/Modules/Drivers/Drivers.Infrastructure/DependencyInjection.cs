using Drivers.Application.DispatchContacts;
using Drivers.Application.Eligibility;
using Drivers.Application.Locations;
using Drivers.Application.Voice;
using Drivers.Infrastructure.DispatchContacts;
using Drivers.Infrastructure.Eligibility;
using Drivers.Infrastructure.Locations;
using Drivers.Infrastructure.Persistence;
using Drivers.Infrastructure.Voice;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.Infrastructure.Tenancy;

namespace Drivers.Infrastructure;

public static class DependencyInjection
{
    private static readonly string[] VehicleTypes = ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"];
    private static readonly HashSet<string> DocumentTypes =
        ["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE", "BACKGROUND_CHECK", "OTHER"];

    public static IServiceCollection AddDriversInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DriversOptions>()
            .Bind(configuration.GetSection(DriversOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Drivers:Provider is unsupported.")
            .Validate(_ => configuration.GetSection(DriversOptions.SectionName)["Eligibility:PolicyVersion"] is null,
                "Drivers:Eligibility:PolicyVersion was removed (POLICY-VERSIONS-PER-ORG-2026-10-02): each " +
                "organization versions its own driver eligibility policy.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Drivers:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != DriversProviderKind.PostgreSql ||
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Drivers:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .Validate(options => options.Provider != DriversProviderKind.PostgreSql ||
                IsComplete(options.Eligibility),
                "Drivers:Eligibility must define a valid document and capacity policy for every vehicle type.")
            .Validate(options => options.Provider != DriversProviderKind.PostgreSql ||
                IsValid(options.LocationTelemetry),
                "Drivers:LocationTelemetry is invalid.")
            .Validate(options => options.RecipientCalls is { } calls && calls.IsValid(),
                "Drivers:RecipientCalls needs MaximumPerOrder 1-20, OrderWindowMinutes 1-60, " +
                "MaximumPerDriverPerHour 1-200 (not below MaximumPerOrder), IdempotencyLifetimeMinutes 60-10080 and " +
                "StaleRequestSeconds 60-3600.")
            // DRV-DISPATCH-CONTACT-2026-10-12: the message never carries a configured value.
            .Validate(options => ConfiguredDriverDispatchContactDirectory.AreValid(options.DispatchContacts),
                "Drivers:DispatchContacts needs, in every entry, an OrganizationId written as a canonical lowercase " +
                "UUID (not the nil UUID) and a PhoneE164 that is +52 followed by ten digits, the first one 2-9; each " +
                "organization at most once.")
            .ValidateOnStart();

        // SCL-007: the API's one paqueteria_app pool, shared with every other API module; PostGIS types on it.
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Application);
        services.AddRuntimeDataSourcePlugin(
            RuntimeDatabaseRole.Application,
            RuntimeDataSourcePlugin.NetTopologySuite,
            static builder => builder.UseNetTopologySuite());
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<DriversDbContext>((serviceProvider, dbOptions) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DriversOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRuntimeDataSource(RuntimeDatabaseRole.Application),
                    postgres =>
                    {
                        postgres.CommandTimeout(options.CommandTimeoutSeconds);
                        postgres.UseNetTopologySuite();
                        postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<DriversDbContext>>();
        services.TryAddSingleton<Paqueteria.Application.IClock, Paqueteria.Infrastructure.SystemClock>();
        services.TryAddSingleton<IDriverLocationAuthorizer, DriverLocationAuthorizer>();
        services.TryAddSingleton<IDriverLocationFailureInjector, NoOpDriverLocationFailureInjector>();
        services.AddSingleton<DisabledDriverLocationIngestionService>();
        services.AddScoped<PostgreSqlDriverLocationIngestionService>();
        services.AddScoped<IDriverLocationIngestionService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<DriversOptions>>().Value.Provider switch
            {
                DriversProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlDriverLocationIngestionService>(),
                _ => serviceProvider.GetRequiredService<DisabledDriverLocationIngestionService>(),
            });
        // VOICE-001-MASKED-CALLS-2026-10-11: the mode comes from IVoiceBridgeStatus (AddPaqueteriaVoiceBridge, API
        // only); everything below is resolved lazily, so a host without the bridge never builds it.
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.AddSingleton<SyntheticDriverPhoneProtector>();
        services.AddSingleton<DisabledDriverPhoneProtector>();
        services.AddSingleton(serviceProvider =>
            new EnvelopeDriverPhoneProtector(serviceProvider.GetRequiredService<IPiiEnvelopeProtector>()));
        services.AddSingleton<SyntheticRecipientCallPhoneResolver>();
        services.AddSingleton<DisabledRecipientCallPhoneResolver>();
        services.AddSingleton(serviceProvider =>
            new EnvelopeRecipientCallPhoneResolver(serviceProvider.GetRequiredService<IPiiEnvelopeProtector>()));
        services.AddScoped<IDriverPhoneProtector>(serviceProvider =>
            serviceProvider.GetRequiredService<IVoiceBridgeStatus>().Mode switch
            {
                VoiceBridgeMode.Live => serviceProvider.GetRequiredService<EnvelopeDriverPhoneProtector>(),
                VoiceBridgeMode.Synthetic => serviceProvider.GetRequiredService<SyntheticDriverPhoneProtector>(),
                _ => serviceProvider.GetRequiredService<DisabledDriverPhoneProtector>(),
            });
        services.AddScoped<IRecipientCallPhoneResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<IVoiceBridgeStatus>().Mode switch
            {
                VoiceBridgeMode.Live => serviceProvider.GetRequiredService<EnvelopeRecipientCallPhoneResolver>(),
                VoiceBridgeMode.Synthetic => serviceProvider.GetRequiredService<SyntheticRecipientCallPhoneResolver>(),
                _ => serviceProvider.GetRequiredService<DisabledRecipientCallPhoneResolver>(),
            });
        services.AddSingleton<DisabledDriverPhoneService>();
        services.AddSingleton<DisabledRecipientCallService>();
        services.AddScoped<PostgreSqlDriverPhoneService>();
        services.AddScoped<PostgreSqlRecipientCallService>();
        services.AddScoped<IDriverPhoneService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<DriversOptions>>().Value.Provider switch
            {
                DriversProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlDriverPhoneService>(),
                _ => serviceProvider.GetRequiredService<DisabledDriverPhoneService>(),
            });
        services.AddScoped<IRecipientCallService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<DriversOptions>>().Value.Provider switch
            {
                DriversProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlRecipientCallService>(),
                _ => serviceProvider.GetRequiredService<DisabledRecipientCallService>(),
            });
        // DRV-DISPATCH-CONTACT-2026-10-12: configuration only, whatever the provider.
        services.TryAddSingleton<IDriverDispatchContactDirectory, ConfiguredDriverDispatchContactDirectory>();
        services.AddSingleton<DisabledDriverEligibilityService>();
        services.AddScoped<PostgreSqlDriverEligibilityService>();
        services.AddScoped<IDriverEligibilityService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<DriversOptions>>().Value.Provider switch
            {
                DriversProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlDriverEligibilityService>(),
                _ => serviceProvider.GetRequiredService<DisabledDriverEligibilityService>(),
            });
        return services;
    }

    private static bool IsComplete(DriverEligibilityOptions options)
    {
        if (options.NonExpiringDocumentTypes.Any(type => !DocumentTypes.Contains(type)))
        {
            return false;
        }

        foreach (var vehicleType in VehicleTypes)
        {
            if (!options.RequiredDocumentTypesByVehicleType.TryGetValue(vehicleType, out var documentTypes) ||
                documentTypes.Count == 0 ||
                documentTypes.Any(type => !DocumentTypes.Contains(type)) ||
                !options.VehicleCapacity.TryGetValue(vehicleType, out var capacity) ||
                !IsValid(capacity))
            {
                return false;
            }
        }

        return options.RequiredDocumentTypesByVehicleType.Keys.All(VehicleTypes.Contains) &&
            options.VehicleCapacity.Keys.All(VehicleTypes.Contains);
    }

    private static bool IsValid(VehicleCapacityOptions value) =>
        value.MaximumPackageCount > 0 &&
        value.MaximumTotalWeightGrams > 0 &&
        value.MaximumSinglePackageWeightGrams > 0 &&
        value.MaximumSinglePackageWeightGrams <= value.MaximumTotalWeightGrams &&
        value.MaximumLengthMillimeters > 0 &&
        value.MaximumWidthMillimeters > 0 &&
        value.MaximumHeightMillimeters > 0;

    private static bool IsValid(DriverLocationTelemetryOptions value) => value.IsValid();
}
