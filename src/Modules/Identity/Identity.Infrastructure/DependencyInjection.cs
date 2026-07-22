using Identity.Application.Authentication;
using Identity.Application.Bootstrap;
using Identity.Infrastructure.Bootstrap;
using Identity.Infrastructure.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton<IIdentityProvider, MockIdentityProvider>();

        var section = configuration.GetSection(IdentityBootstrapOptions.SectionName);
        services
            .AddOptions<IdentityBootstrapOptions>()
            .Bind(section)
            .Validate(options => Enum.IsDefined(options.Provider),
                "IdentityBootstrap:Provider must be Disabled, Mock or PostgreSql.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "IdentityBootstrap:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != IdentityBootstrapProviderKind.Mock ||
                    environment.IsDevelopment() || environment.IsEnvironment("Testing"),
                "IdentityBootstrap:Provider=Mock is permitted only in Development or Testing.")
            .Validate(options => options.Provider != IdentityBootstrapProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "IdentityBootstrap:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.AddSingleton<MockIdentityContextResolver>();
        services.AddSingleton<DisabledIdentityContextResolver>();
        services.AddScoped<PostgreSqlIdentityContextResolver>();
        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL identity bootstrap provider requires a configured connection string.")));
        services.AddScoped<IIdentityContextResolver>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<IdentityBootstrapOptions>>().Value.Provider switch
            {
                IdentityBootstrapProviderKind.Mock => serviceProvider.GetRequiredService<MockIdentityContextResolver>(),
                IdentityBootstrapProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlIdentityContextResolver>(),
                _ => serviceProvider.GetRequiredService<DisabledIdentityContextResolver>(),
            });

        return services;
    }
}
