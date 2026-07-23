using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
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
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
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
            .Validate(options => options.GeocodingProvider != GeocodingProviderKind.Mock || IsNonProduction(environment),
                "The mock geocoding provider is allowed only in Development or Testing.")
            .Validate(options => options.PiiProtector != LocationPiiProtectorKind.Mock || IsNonProduction(environment),
                "The mock PII protector is allowed only in Development or Testing.")
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
        services.AddSingleton<DisabledLocationPiiProtector>();
        services.AddSingleton<DeterministicMockLocationPiiProtector>();
        services.AddSingleton<DisabledLocationService>();
        services.AddScoped<PostgreSqlLocationService>();
        services.AddScoped<IGeocodingProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.GeocodingProvider switch
            {
                GeocodingProviderKind.Manual => serviceProvider.GetRequiredService<ManualGeocodingProvider>(),
                GeocodingProviderKind.Mock => serviceProvider.GetRequiredService<DeterministicMockGeocodingProvider>(),
                _ => serviceProvider.GetRequiredService<DisabledGeocodingProvider>(),
            });
        services.AddScoped<ILocationPiiProtector>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<LocationsOptions>>().Value.PiiProtector switch
            {
                LocationPiiProtectorKind.Mock => serviceProvider.GetRequiredService<DeterministicMockLocationPiiProtector>(),
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

    private static bool IsNonProduction(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}

internal sealed class LocationsDataSource(NpgsqlDataSource value) : IAsyncDisposable
{
    internal NpgsqlDataSource Value { get; } = value;

    public ValueTask DisposeAsync() => Value.DisposeAsync();
}
