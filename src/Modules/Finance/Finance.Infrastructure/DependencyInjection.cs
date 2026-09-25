using Finance.Application.Cod;
using Finance.Application.Financials;
using Finance.Infrastructure.Cod;
using Finance.Infrastructure.Financials;
using Finance.Infrastructure.Persistence;
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

namespace Finance.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFinanceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FinanceOptions>()
            .Bind(configuration.GetSection(FinanceOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Finance:Provider is unsupported.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Finance:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.IdempotencyLifetimeMinutes is >= 1 and <= 10_080,
                "Finance:IdempotencyLifetimeMinutes must be between 1 and 10080.")
            .Validate(options => options.Provider != FinanceProviderKind.PostgreSql ||
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Finance:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL finance provider requires a configured connection string.")));
        services.TryAddScoped<TenantDatabaseExecutionState>();
        services.TryAddScoped<TenantTransactionGuardInterceptor>();
        services.TryAddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<FinanceDbContext>((serviceProvider, dbOptions) =>
        {
            var financeOptions = serviceProvider.GetRequiredService<IOptions<FinanceOptions>>().Value;
            dbOptions.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.MigrationsAssembly(typeof(FinanceDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_finance", "platform");
                        postgres.CommandTimeout(financeOptions.CommandTimeoutSeconds);
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>());
        });
        services.AddScoped<TenantTransactionContext<FinanceDbContext>>();
        services.AddScoped<FinanceTenantGateway>();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAuditPayloadRedactor, AuditPayloadRedactor>();
        services.TryAddScoped<IAppendOnlyAuditWriter, PostgreSqlAppendOnlyAuditWriter>();
        services.TryAddSingleton<IFinanceFailureInjector, NoOpFinanceFailureInjector>();

        services.AddSingleton<DisabledCodTransactionService>();
        services.AddSingleton<DisabledOrderFinancialsService>();
        services.AddScoped<PostgreSqlCodTransactionService>();
        services.AddScoped<PostgreSqlOrderFinancialsService>();
        services.AddScoped<ICodTransactionService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<FinanceOptions>>().Value.Provider switch
            {
                FinanceProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlCodTransactionService>(),
                _ => serviceProvider.GetRequiredService<DisabledCodTransactionService>(),
            });
        services.AddScoped<IOrderFinancialsService>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<FinanceOptions>>().Value.Provider switch
            {
                FinanceProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlOrderFinancialsService>(),
                _ => serviceProvider.GetRequiredService<DisabledOrderFinancialsService>(),
            });
        return services;
    }
}
