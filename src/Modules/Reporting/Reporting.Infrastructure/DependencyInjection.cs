using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Reporting.Application.Operations;
using Reporting.Infrastructure.Operations;

namespace Reporting.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddReportingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OperationsDashboardOptions>()
            .Bind(configuration.GetSection(OperationsDashboardOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider),
                "OperationsDashboard:Provider is unsupported.")
            .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 60,
                "OperationsDashboard:CommandTimeoutSeconds must be between 1 and 60.")
            .Validate(options => options.DefaultPageSize is >= 1 and <= 100,
                "OperationsDashboard:DefaultPageSize must be between 1 and 100.")
            .Validate(options => options.MaximumPageSize is >= 1 and <= 100 &&
                options.DefaultPageSize <= options.MaximumPageSize,
                "OperationsDashboard page sizes are invalid.")
            .Validate(options => options.MaximumDateRangeDays is >= 1 and <= 31,
                "OperationsDashboard:MaximumDateRangeDays must be between 1 and 31.")
            .Validate(options => options.Provider != OperationsDashboardProviderKind.PostgreSql ||
                !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Paqueteria")),
                "OperationsDashboard:Provider=PostgreSql requires ConnectionStrings:Paqueteria.")
            .ValidateOnStart();

        services.TryAddSingleton(serviceProvider => NpgsqlDataSource.Create(
            serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Paqueteria")
            ?? throw new InvalidOperationException(
                "A PostgreSQL operations dashboard requires a configured connection string.")));
        services.TryAddSingleton<IOperationsDashboardTelemetry, OperationsDashboardTelemetry>();
        services.AddSingleton<DisabledOperationsDashboardReader>();
        services.AddScoped<PostgreSqlOperationsDashboardReader>();
        services.AddScoped<IOperationsDashboardReader>(serviceProvider =>
            serviceProvider.GetRequiredService<IOptions<OperationsDashboardOptions>>().Value.Provider switch
            {
                OperationsDashboardProviderKind.PostgreSql =>
                    serviceProvider.GetRequiredService<PostgreSqlOperationsDashboardReader>(),
                _ => serviceProvider.GetRequiredService<DisabledOperationsDashboardReader>(),
            });
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "operations_dashboard",
            serviceProvider => new OperationsDashboardHealthCheck(
                serviceProvider.GetRequiredService<IOptions<OperationsDashboardOptions>>()),
            failureStatus: HealthStatus.Degraded,
            tags: ["ready"]));
        return services;
    }
}

internal sealed class OperationsDashboardHealthCheck(
    IOptions<OperationsDashboardOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(options.Value.Provider == OperationsDashboardProviderKind.PostgreSql
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Operations dashboard is disabled."));
}
