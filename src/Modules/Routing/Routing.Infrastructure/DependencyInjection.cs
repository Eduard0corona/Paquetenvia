using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;
using Routing.Application.Routes;
using Routing.Infrastructure.Persistence;
using Routing.Infrastructure.Routes;

namespace Routing.Infrastructure;

public static class DependencyInjection
{
    private static readonly string[] VehicleTypes = ["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"];
    private static readonly HashSet<string> DocumentTypes =
        ["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE", "BACKGROUND_CHECK", "OTHER"];

    public static IServiceCollection AddRoutingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RoutingOptions>()
            .Bind(configuration.GetSection(RoutingOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Routing:Provider is unsupported.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Routing:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.IdempotencyLifetimeMinutes is >= 1 and <= 10_080,
                "Routing:IdempotencyLifetimeMinutes must be between 1 and 10080.")
            .Validate(options => options.Provider != RoutingProviderKind.PostgreSql ||
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Routing:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();
        services.AddOptions<RoutingDriverEligibilityOptions>()
            .Bind(configuration.GetSection("Drivers:Eligibility"))
            .Validate(options =>
                    configuration.GetValue<RoutingProviderKind>($"{RoutingOptions.SectionName}:Provider") !=
                        RoutingProviderKind.PostgreSql || IsComplete(options),
                "Drivers:Eligibility must define the DSP-001 policy used by PostgreSQL routing.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL routing provider requires a configured connection string.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<RoutingDbContext>((serviceProvider, dbOptions) =>
        {
            var routingOptions = serviceProvider.GetRequiredService<IOptions<RoutingOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.CommandTimeout(routingOptions.CommandTimeoutSeconds);
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<RoutingDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.TryAddSingleton<IRoutingFailureInjector, NoOpRoutingFailureInjector>();
        services.AddSingleton<DisabledRouteService>();
        services.AddScoped<PostgreSqlRouteService>();
        services.AddScoped<IRouteService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<RoutingOptions>>().Value.Provider switch
            {
                RoutingProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlRouteService>(),
                _ => serviceProvider.GetRequiredService<DisabledRouteService>(),
            });
        return services;
    }

    private static bool IsComplete(RoutingDriverEligibilityOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PolicyVersion) ||
            options.NonExpiringDocumentTypes.Any(type => !DocumentTypes.Contains(type))) return false;
        foreach (var vehicleType in VehicleTypes)
        {
            if (!options.RequiredDocumentTypesByVehicleType.TryGetValue(vehicleType, out var documents) ||
                documents.Count == 0 || documents.Any(type => !DocumentTypes.Contains(type)) ||
                !options.VehicleCapacity.TryGetValue(vehicleType, out var capacity) ||
                capacity.MaximumPackageCount <= 0 || capacity.MaximumTotalWeightGrams <= 0 ||
                capacity.MaximumSinglePackageWeightGrams <= 0 ||
                capacity.MaximumSinglePackageWeightGrams > capacity.MaximumTotalWeightGrams ||
                capacity.MaximumLengthMillimeters <= 0 || capacity.MaximumWidthMillimeters <= 0 ||
                capacity.MaximumHeightMillimeters <= 0) return false;
        }
        return options.RequiredDocumentTypesByVehicleType.Keys.All(VehicleTypes.Contains) &&
            options.VehicleCapacity.Keys.All(VehicleTypes.Contains);
    }
}
