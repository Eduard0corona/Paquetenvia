using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Geocoding.GoogleMaps;
using Locations.Infrastructure.Locations;
using Locations.Infrastructure.Persistence;
using Locations.Infrastructure.RecipientNotices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Locations;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.Infrastructure.Tenancy;

namespace Locations.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddLocationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<LocationsOptions>()
            .Bind(configuration.GetSection(LocationsOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider) && Enum.IsDefined(options.GeocodingProvider) &&
                    Enum.IsDefined(options.PiiProtector) && Enum.IsDefined(options.NoticeKeyProtector),
                "Locations providers contain an unsupported value.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Locations:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != LocationsProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Locations:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .Validate(options => options.GeocodingProvider != GeocodingProviderKind.Mock || IsMockProviderAllowed(environment),
                "The mock geocoding provider is allowed only in Development, Testing, or authorized DevSynthetic.")
            .Validate(options => options.GeocodingProvider != GeocodingProviderKind.GoogleMaps ||
                    GoogleMapsGeocodingOptions.IsValid(options.GoogleMaps),
                "Locations:GeocodingProvider=GoogleMaps requires Locations:GoogleMaps (ApiKey from Key Vault, https BaseUri, ComponentsCountry locked to MX, bounded resilience settings).")
            .Validate(options => options.PiiProtector != LocationPiiProtectorKind.Mock || IsMockProviderAllowed(environment),
                "The mock PII protector is DEV_SYNTHETIC_ONLY outside Development and Testing; it is not a Staging or Production pattern.")
            // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002): the notice copies follow the ADP-001 pattern - the
            // synthetic digests only where the mock PII protector is allowed, and the Key Vault notice key only next to
            // the Key Vault PII key, so a real notice copy never sits beside a mock PII copy.
            .Validate(options => options.NoticeKeyProtector != LocationNoticeKeyProtectorKind.Mock || IsMockProviderAllowed(environment),
                "The mock recipient-notice key is DEV_SYNTHETIC_ONLY outside Development and Testing; it is not a Staging or Production pattern.")
            .Validate(options => options.NoticeKeyProtector != LocationNoticeKeyProtectorKind.AzureKeyVault ||
                    options.PiiProtector == LocationPiiProtectorKind.AzureKeyVault,
                "Locations:NoticeKeyProtector=AzureKeyVault requires Locations:PiiProtector=AzureKeyVault.")
            .Validate(options => options.Provider != LocationsProviderKind.PostgreSql ||
                    options.GeocodingProvider != GeocodingProviderKind.Disabled,
                "PostgreSQL location creation requires a geocoding provider.")
            .Validate(options => options.Provider != LocationsProviderKind.PostgreSql ||
                    options.PiiProtector != LocationPiiProtectorKind.Disabled,
                "PostgreSQL location creation requires a PII protector.")
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
        services.AddDbContext<LocationsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                    serviceProvider.GetRuntimeDataSource(RuntimeDatabaseRole.Application),
                    postgres =>
                    {
                        postgres.UseNetTopologySuite();
                        postgres.MigrationsAssembly(typeof(LocationsDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_locations", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>()));
        services.AddScoped<TenantTransactionContext<LocationsDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();

        services.AddSingleton<DisabledGeocodingProvider>();
        services.AddSingleton<ManualGeocodingProvider>();
        services.AddSingleton<DeterministicMockGeocodingProvider>();
        // GATE-003-PROVIDER-GOOGLE: built lazily, only when GeocodingProvider=GoogleMaps. The named
        // client has no default loggers (the request URI carries the address and the key), no
        // redirects and no client-wide timeout: the provider bounds every attempt itself.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(GoogleMapsGeocodingProvider.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .RemoveAllLoggers();
        services.AddSingleton<GoogleMapsGeocodingProvider>();
        services.AddSingleton<DisabledLocationPiiProtector>();
        services.AddSingleton<DeterministicMockLocationPiiProtector>();
        // ADP-001: registered lazily; nothing Azure-related is built unless PiiProtector=AzureKeyVault.
        services.AddAzureKeyVaultPiiProtection(
            configuration,
            serviceProvider => serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.PiiProtector ==
                LocationPiiProtectorKind.AzureKeyVault);
        services.AddSingleton<AzureKeyVaultLocationPiiProtector>();
        // NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2): the recipient-notice key, registered lazily like the PII key. The
        // API only wraps with it: nothing registered here can open a notice copy.
        services.AddAzureKeyVaultRecipientNoticeKey(
            configuration,
            serviceProvider => serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.NoticeKeyProtector ==
                LocationNoticeKeyProtectorKind.AzureKeyVault);
        services.AddSingleton<DisabledLocationNoticeContactSealer>();
        services.AddSingleton<DeterministicMockLocationNoticeContactSealer>();
        services.AddSingleton<AzureKeyVaultLocationNoticeContactSealer>();
        services.AddScoped<ILocationNoticeContactSealer>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.NoticeKeyProtector switch
            {
                LocationNoticeKeyProtectorKind.Mock => serviceProvider.GetRequiredService<DeterministicMockLocationNoticeContactSealer>(),
                LocationNoticeKeyProtectorKind.AzureKeyVault => serviceProvider.GetRequiredService<AzureKeyVaultLocationNoticeContactSealer>(),
                _ => serviceProvider.GetRequiredService<DisabledLocationNoticeContactSealer>(),
            });
        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the values are read back the way they were protected, from the
        // same bound option, so a module that shows a location never repeats the mode, purposes or storage format.
        services.AddSingleton<DisabledLocationPiiReader>();
        services.AddSingleton<SyntheticLocationPiiReader>();
        services.AddSingleton(serviceProvider =>
            new EnvelopeLocationPiiReader(serviceProvider.GetRequiredService<IPiiEnvelopeProtector>()));
        services.AddScoped<ILocationPiiReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.PiiProtector switch
            {
                LocationPiiProtectorKind.Mock => serviceProvider.GetRequiredService<SyntheticLocationPiiReader>(),
                LocationPiiProtectorKind.AzureKeyVault => serviceProvider.GetRequiredService<EnvelopeLocationPiiReader>(),
                _ => serviceProvider.GetRequiredService<DisabledLocationPiiReader>(),
            });

        // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 1): the e-mail unsubscribe opens the destination e-mail
        // with the PII key the way it was protected, from the same bound option.
        services.AddSingleton<DisabledRecipientEmailOpener>();
        services.AddSingleton<SyntheticRecipientEmailOpener>();
        services.AddSingleton(serviceProvider =>
            new EnvelopeRecipientEmailOpener(serviceProvider.GetRequiredService<IPiiEnvelopeProtector>()));
        services.AddScoped<IRecipientEmailOpener>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.PiiProtector switch
            {
                LocationPiiProtectorKind.Mock => serviceProvider.GetRequiredService<SyntheticRecipientEmailOpener>(),
                LocationPiiProtectorKind.AzureKeyVault => serviceProvider.GetRequiredService<EnvelopeRecipientEmailOpener>(),
                _ => serviceProvider.GetRequiredService<DisabledRecipientEmailOpener>(),
            });
        // NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (T6 choice 12): "Detener avisos" opens the destination phone with the PII
        // key the way it was protected, from the same bound option, like the e-mail above.
        services.AddSingleton<DisabledRecipientPhoneOpener>();
        services.AddSingleton<SyntheticRecipientPhoneOpener>();
        services.AddSingleton(serviceProvider =>
            new EnvelopeRecipientPhoneOpener(serviceProvider.GetRequiredService<IPiiEnvelopeProtector>()));
        services.AddScoped<IRecipientPhoneOpener>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.PiiProtector switch
            {
                LocationPiiProtectorKind.Mock => serviceProvider.GetRequiredService<SyntheticRecipientPhoneOpener>(),
                LocationPiiProtectorKind.AzureKeyVault => serviceProvider.GetRequiredService<EnvelopeRecipientPhoneOpener>(),
                _ => serviceProvider.GetRequiredService<DisabledRecipientPhoneOpener>(),
            });

        services.AddSingleton<DisabledLocationService>();
        services.AddScoped<PostgreSqlLocationService>();
        services.AddScoped<IGeocodingProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.GeocodingProvider switch
            {
                GeocodingProviderKind.Manual => serviceProvider.GetRequiredService<ManualGeocodingProvider>(),
                GeocodingProviderKind.Mock => serviceProvider.GetRequiredService<DeterministicMockGeocodingProvider>(),
                GeocodingProviderKind.GoogleMaps => serviceProvider.GetRequiredService<GoogleMapsGeocodingProvider>(),
                _ => serviceProvider.GetRequiredService<DisabledGeocodingProvider>(),
            });
        services.AddScoped<ILocationPiiProtector>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.PiiProtector switch
            {
                LocationPiiProtectorKind.Mock => serviceProvider.GetRequiredService<DeterministicMockLocationPiiProtector>(),
                LocationPiiProtectorKind.AzureKeyVault => serviceProvider.GetRequiredService<AzureKeyVaultLocationPiiProtector>(),
                _ => serviceProvider.GetRequiredService<DisabledLocationPiiProtector>(),
            });
        services.AddScoped<ILocationService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.Provider switch
            {
                LocationsProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlLocationService>(),
                _ => serviceProvider.GetRequiredService<DisabledLocationService>(),
            });
        services.AddScoped<IServiceabilityEvaluator>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.Provider switch
            {
                LocationsProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlLocationService>(),
                _ => serviceProvider.GetRequiredService<DisabledLocationService>(),
            });
        services.AddScoped<IQuoteLocationResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.Provider switch
            {
                LocationsProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlLocationService>(),
                _ => serviceProvider.GetRequiredService<DisabledLocationService>(),
            });

        return services;
    }

    private static bool IsMockProviderAllowed(IHostEnvironment environment) =>
        environment.IsDevelopment() ||
        environment.IsEnvironment("Testing") ||
        SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName);
}
