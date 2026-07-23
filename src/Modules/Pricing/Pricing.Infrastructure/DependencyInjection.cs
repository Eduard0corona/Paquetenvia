using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Application.Quotes;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Quotes;

namespace Pricing.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPricingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PricingOptions>()
            .Bind(configuration.GetSection(PricingOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Pricing:Provider is unsupported.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Pricing:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != PricingProviderKind.PostgreSql ||
                    options.QuoteLifetimeMinutes is >= 1 and <= 1_440,
                "Pricing:QuoteLifetimeMinutes must be between 1 and 1440 for PostgreSql.")
            .Validate(options => options.Provider != PricingProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(options.PricingPolicyVersion),
                "Pricing:PricingPolicyVersion is required for PostgreSql.")
            .Validate(options => options.Provider != PricingProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Pricing:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.AddSingleton(serviceProvider =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>()
                .GetConnectionString("Paqueteria")
                ?? throw new InvalidOperationException("PostgreSQL pricing requires a configured connection string.");
            return new PricingDataSource(new NpgsqlDataSourceBuilder(connectionString).Build());
        });
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<PricingDbContext>((serviceProvider, dbOptions) =>
        {
            var pricingOptions = serviceProvider.GetRequiredService<IOptions<PricingOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRequiredService<PricingDataSource>().Value,
                    postgres =>
                    {
                        postgres.CommandTimeout(pricingOptions.CommandTimeoutSeconds);
                        postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<PricingDbContext>>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.AddSingleton<DisabledQuoteService>();
        services.AddScoped<PostgreSqlQuoteService>();
        services.AddScoped<IQuoteService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<PricingOptions>>().Value.Provider switch
            {
                PricingProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlQuoteService>(),
                _ => serviceProvider.GetRequiredService<DisabledQuoteService>(),
            });
        return services;
    }
}

internal sealed class PricingDataSource(NpgsqlDataSource value) : IAsyncDisposable
{
    internal NpgsqlDataSource Value { get; } = value;
    public ValueTask DisposeAsync() => Value.DisposeAsync();
}
