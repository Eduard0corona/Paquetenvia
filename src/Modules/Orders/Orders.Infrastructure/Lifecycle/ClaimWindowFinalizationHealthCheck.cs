using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Orders.Infrastructure.Lifecycle;

internal sealed class ClaimWindowFinalizationHealthCheck(
    OrdersWorkerDataSource workerDataSource,
    IOptions<ClaimWindowFinalizationOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("Claim-window finalization is disabled.");
        }

        try
        {
            await using var connection = await workerDataSource.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction))
            {
                await role.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(
                """
                SELECT COALESCE(has_function_privilege(
                  to_regprocedure('security.finalize_expired_orders(integer)'),'EXECUTE'),false);
                """,
                connection,
                transaction);
            var executable = await command.ExecuteScalarAsync(cancellationToken) is true;
            await transaction.RollbackAsync(cancellationToken);
            return executable
                ? HealthCheckResult.Healthy("Claim-window finalization function is executable.")
                : HealthCheckResult.Unhealthy("Claim-window finalization function is unavailable to the Worker.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Claim-window finalization database is unavailable.", exception);
        }
    }
}

/// <summary>
/// The Worker credential of the Orders jobs, never the API's. SCL-007: the Worker's one <c>paqueteria_worker</c> pool,
/// shared with every other Worker module and built on first use.
/// </summary>
internal sealed class OrdersWorkerDataSource([FromKeyedServices(RuntimeDatabaseRole.Worker)] RuntimeDataSource pool)
{
    public NpgsqlDataSource DataSource => pool.DataSource;
}
