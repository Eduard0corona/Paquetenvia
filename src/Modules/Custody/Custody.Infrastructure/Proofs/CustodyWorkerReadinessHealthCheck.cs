using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Custody.Infrastructure.Proofs;

public sealed class CustodyWorkerReadinessHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SET LOCAL ROLE paqueteria_worker;
                SELECT current_user,rolbypassrls
                FROM pg_roles
                WHERE rolname=current_user;
                """,
                connection,
                transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetString(0) != "paqueteria_worker" ||
                reader.GetBoolean(1))
            {
                return HealthCheckResult.Unhealthy(
                    "Custody Worker must run as paqueteria_worker with NOBYPASSRLS.");
            }

            return HealthCheckResult.Healthy(
                "Custody Worker PostgreSQL role is paqueteria_worker with NOBYPASSRLS.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Custody Worker PostgreSQL readiness failed.",
                exception);
        }
    }
}
