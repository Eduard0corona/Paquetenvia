using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: Healthy while the job is disabled; when enabled, the Worker credential must be able to
/// execute the owner discovery function as <c>paqueteria_worker</c>.
/// </summary>
internal sealed class OrderAutoCloseHealthCheck(
    OrdersWorkerDataSource workerDataSource,
    IOptions<OrderAutoCloseOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            return HealthCheckResult.Healthy("Order auto-close is disabled.");
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
                  to_regprocedure('security.list_auto_close_owner_organizations(uuid,integer)'),'EXECUTE'),false);
                """,
                connection,
                transaction);
            var executable = await command.ExecuteScalarAsync(cancellationToken) is true;
            await transaction.RollbackAsync(cancellationToken);
            return executable
                ? HealthCheckResult.Healthy("Order auto-close discovery function is executable.")
                : HealthCheckResult.Unhealthy("Order auto-close discovery function is unavailable to the Worker.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Order auto-close database is unavailable.", exception);
        }
    }
}
