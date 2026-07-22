using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Organizations.Application;
using Organizations.Application.Auditing;
using Organizations.Application.OrganizationContexts;
using Organizations.Infrastructure.Auditing;
using Organizations.Infrastructure.OrganizationContexts;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Infrastructure.Tenancy;
using Organizations.Application.Provisioning;
using Organizations.Infrastructure.Provisioning;

namespace Organizations.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrganizationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TenancyOptions>()
            .Bind(configuration.GetSection(TenancyOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider),
                "Tenancy:Provider must be Disabled or PostgreSql.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "Tenancy:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.Provider != TenancyProviderKind.PostgreSql ||
                    !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "Tenancy:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException("PostgreSQL tenancy requires a configured connection string.")));

        services.AddScoped<TenantDatabaseExecutionState>();
        services.AddScoped<TenantTransactionGuardInterceptor>();
        services.AddScoped<TenantSaveChangesGuardInterceptor>();
        services.AddDbContext<OrganizationsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    postgres =>
                    {
                        postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                        postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
                        postgres.EnableRetryOnFailure();
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantTransactionGuardInterceptor>(),
                    serviceProvider.GetRequiredService<TenantSaveChangesGuardInterceptor>()));
        services.AddScoped<TenantTransactionContext<OrganizationsDbContext>>();
        services.AddSingleton<DisabledOrganizationContextReader>();
        services.AddSingleton<DisabledPlatformAdminTenantActivationAudit>();
        services.AddScoped<PostgreSqlOrganizationContextReader>();
        services.AddScoped<PostgreSqlPlatformAdminTenantActivationAudit>();
        services.AddScoped<IOrganizationContextReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<TenancyOptions>>().Value.Provider switch
            {
                TenancyProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlOrganizationContextReader>(),
                _ => serviceProvider.GetRequiredService<DisabledOrganizationContextReader>(),
            });
        services.AddScoped<IPlatformAdminTenantActivationAudit>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<TenancyOptions>>().Value.Provider switch
            {
                TenancyProviderKind.PostgreSql => serviceProvider.GetRequiredService<PostgreSqlPlatformAdminTenantActivationAudit>(),
                _ => serviceProvider.GetRequiredService<DisabledPlatformAdminTenantActivationAudit>(),
            });
        services.TryAddScoped<IInitialOrganizationProvisioningAuthorizer, DenyInitialOrganizationProvisioningAuthorizer>();
        services.TryAddScoped<IProvisioningFailureInjector, NoOpProvisioningFailureInjector>();
        services.AddScoped<IInitialOrganizationProvisioner, PostgreSqlInitialOrganizationProvisioner>();
        return services;
    }
}
