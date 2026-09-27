using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

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

/// <summary>Owns the Worker-credential data source so it never collides with the API's.</summary>
internal sealed class OrdersWorkerDataSource(string connectionString) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : "Host=127.0.0.1;Database=disabled;Username=disabled;Password=disabled");

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
