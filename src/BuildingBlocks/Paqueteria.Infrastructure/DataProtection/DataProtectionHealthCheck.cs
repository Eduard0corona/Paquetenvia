using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Paqueteria.Infrastructure.DataProtection;

/// <summary>
/// SCL-001 readiness: a replica that cannot reach the shared key ring must never report ready,
/// because it would otherwise serve requests with per-node key material and invalidate payloads
/// protected by its peers.
/// </summary>
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

            // Runtime logins are NOINHERIT, so the canonical role is assumed explicitly. A login
            // that lost the role membership fails here and stays unready.
            await using (var role = new NpgsqlCommand(
                $"SET LOCAL ROLE {options.Value.RuntimeRole}",
                connection,
                transaction))
            {
                await role.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The migration lane is checked before the privileges: PostgreSQL does not promise to
            // short-circuit AND, so probing privileges on a missing table is not a stable signal.
            if (await ScalarAsync(
                    "SELECT to_regclass('platform.data_protection_keys') IS NOT NULL",
                    connection,
                    transaction,
                    cancellationToken).ConfigureAwait(false) is not true)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return HealthCheckResult.Unhealthy("data_protection_keyring_migration_missing");
            }

            // Reading the ring the way the repository reads it proves SELECT is actually
            // reachable through row level security; a revoked grant raises here.
            await ScalarAsync(
                """
                SELECT count(*)::integer
                FROM platform.data_protection_keys
                WHERE application_name = @application_name
                """,
                connection,
                transaction,
                cancellationToken,
                new NpgsqlParameter("application_name", options.Value.ApplicationName)).ConfigureAwait(false);

            // A replica that may read but not publish its own key would degrade into a reader of
            // someone else's ring, so it is not ready either.
            var appendable = await ScalarAsync(
                "SELECT has_table_privilege('platform.data_protection_keys','INSERT')",
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false) is true;
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return appendable
                ? HealthCheckResult.Healthy("data_protection_shared_keyring")
                : HealthCheckResult.Unhealthy("data_protection_keyring_not_appendable");
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("data_protection_keyring_unreachable");
        }
    }

    private async Task<object?> ScalarAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
