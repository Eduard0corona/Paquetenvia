using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Geocoding.GoogleMaps;
using Locations.Infrastructure.Locations;
using Locations.Infrastructure.Persistence;
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
            .Validate(options => Enum.IsDefined(options.Provider) && Enum.IsDefined(options.GeocodingProvider) && Enum.IsDefined(options.PiiProtector),
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
            .Validate(options => options.Provider != LocationsProviderKind.PostgreSql ||
                    options.GeocodingProvider != GeocodingProviderKind.Disabled,
                "PostgreSQL location creation requires a geocoding provider.")
            .Validate(options => options.Provider != LocationsProviderKind.PostgreSql ||
                    options.PiiProtector != LocationPiiProtectorKind.Disabled,
                "PostgreSQL location creation requires a PII protector.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(
                serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
                ?? throw new InvalidOperationException("PostgreSQL locations require a configured connection string."));
            dataSourceBuilder.UseNetTopologySuite();
            return new LocationsDataSource(dataSourceBuilder.Build());
        });
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<LocationsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                    serviceProvider.GetRequiredService<LocationsDataSource>().Value,
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

internal sealed class LocationsDataSource(NpgsqlDataSource value) : IAsyncDisposable
{
    internal NpgsqlDataSource Value { get; } = value;

    public ValueTask DisposeAsync() => Value.DisposeAsync();
}
