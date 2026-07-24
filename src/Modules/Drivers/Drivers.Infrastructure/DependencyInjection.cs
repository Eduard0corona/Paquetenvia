using Drivers.Application.Eligibility;
using Drivers.Infrastructure.Eligibility;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
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
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Drivers:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != DriversProviderKind.PostgreSql ||
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Drivers:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .Validate(options => options.Provider != DriversProviderKind.PostgreSql ||
                IsComplete(options.Eligibility),
                "Drivers:Eligibility must define a valid document and capacity policy for every vehicle type.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL drivers provider requires a configured connection string.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<DriversDbContext>((serviceProvider, dbOptions) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DriversOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.CommandTimeout(options.CommandTimeoutSeconds);
                        postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<DriversDbContext>>();
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
        if (string.IsNullOrWhiteSpace(options.PolicyVersion) ||
            options.NonExpiringDocumentTypes.Any(type => !DocumentTypes.Contains(type)))
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
}
