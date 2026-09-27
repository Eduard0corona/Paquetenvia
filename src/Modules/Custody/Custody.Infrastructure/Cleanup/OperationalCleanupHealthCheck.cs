using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Custody.Infrastructure.Cleanup;

/// <summary>
/// Ready only when every enabled OPS-003 job can reach its function as <c>paqueteria_worker</c>; it
/// reads catalog privileges and never calls a cleanup function.
/// </summary>
internal sealed class OperationalCleanupHealthCheck(
    OperationalCleanupDataSource dataSource,
    IOptions<OperationalCleanupOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.AnyEnabled)
        {
            return HealthCheckResult.Healthy("Operational cleanup is disabled.");
        }

        try
        {
            await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction))
            {
                await role.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(
                """
                SELECT (NOT @keys OR COALESCE(has_function_privilege(
                          to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)'),
                          'EXECUTE'),false))
                   AND (NOT @sessions OR COALESCE(has_function_privilege(
                          to_regprocedure('security.expire_proof_upload_sessions(integer)'),'EXECUTE'),false));
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("keys", settings.IdempotencyKeys.Enabled);
            command.Parameters.AddWithValue("sessions", settings.ProofUploadSessions.Enabled);
            var executable = await command.ExecuteScalarAsync(cancellationToken) is true;
            await transaction.RollbackAsync(cancellationToken);
            return executable
                ? HealthCheckResult.Healthy("Operational cleanup functions are executable.")
                : HealthCheckResult.Unhealthy("Operational cleanup functions are unavailable to the Worker.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Operational cleanup database is unavailable.", exception);
        }
    }
}
