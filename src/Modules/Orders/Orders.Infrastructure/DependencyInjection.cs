using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Orders.Application.Tracking;
using Orders.Infrastructure.Tracking;
using Microsoft.Extensions.Options;

namespace Orders.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrdersInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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
        services.AddScoped<IPublicTrackingProjectionReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PublicTrackingOptions>>().Value.Provider switch
            {
                PublicTrackingProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlPublicTrackingProjectionReader>(),
                _ => serviceProvider.GetRequiredService<DisabledPublicTrackingProjectionReader>(),
            });

        return services;
    }
}
