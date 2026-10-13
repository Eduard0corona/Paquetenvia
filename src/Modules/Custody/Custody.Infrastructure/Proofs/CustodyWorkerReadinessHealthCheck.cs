using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Custody.Infrastructure.Proofs;

/// <summary>
/// Ready only when the Worker's shared <c>paqueteria_worker</c> pool (SCL-007) assumes <c>paqueteria_worker</c> and that
/// role does not bypass row level security.
/// </summary>
public sealed class CustodyWorkerReadinessHealthCheck(
    [FromKeyedServices(RuntimeDatabaseRole.Worker)] RuntimeDataSource dataSource) : IHealthCheck
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
