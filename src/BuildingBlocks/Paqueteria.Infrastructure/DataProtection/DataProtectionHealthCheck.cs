using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Paqueteria.Infrastructure.DataProtection;

internal sealed class DataProtectionHealthCheck(
    IOptions<DataProtectionOptions> options,
    IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.Provider != DataProtectionProviderKind.PostgreSql)
        {
            return HealthCheckResult.Healthy("data_protection_local_keyring");
        }

        if (services.GetService(typeof(DataProtectionDataSource)) is not DataProtectionDataSource dataSource)
        {
            return HealthCheckResult.Unhealthy("data_protection_keyring_unconfigured");
        }

        try
        {
            await using var connection = await dataSource.Value
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var role = new NpgsqlCommand(
                $"SET LOCAL ROLE {options.Value.RuntimeRole}",
                connection,
                transaction))
            {
                await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = new NpgsqlCommand(
                """
                SELECT to_regclass('platform.data_protection_keys') IS NOT NULL
                  AND has_table_privilege('platform.data_protection_keys','SELECT')
                  AND has_table_privilege('platform.data_protection_keys','INSERT')
                """,
                connection,
                transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            };
            var reachable = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return reachable
                ? HealthCheckResult.Healthy("data_protection_shared_keyring")
                : HealthCheckResult.Unhealthy("data_protection_keyring_unreachable");
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("data_protection_keyring_unreachable");
        }
    }
}
