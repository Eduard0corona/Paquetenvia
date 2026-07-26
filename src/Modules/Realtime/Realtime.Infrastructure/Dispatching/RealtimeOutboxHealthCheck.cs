using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Realtime.Application.Configuration;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class RealtimeOutboxHealthCheck(
    RealtimeWorkerConnectionFactory connections,
    IOptions<RealtimeOptions> realtimeOptions,
    IOptions<OutboxDispatcherOptions> dispatcherOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (dispatcherOptions.Value.Provider == OutboxDispatcherProviderKind.Disabled)
        {
            return HealthCheckResult.Degraded(
                "Realtime outbox dispatch is disabled; no rows are claimed.");
        }

        if (realtimeOptions.Value.Provider != RealtimeProviderKind.SignalR ||
            realtimeOptions.Value.Backplane != RealtimeBackplaneKind.InProcess)
        {
            return HealthCheckResult.Unhealthy(
                "Realtime outbox dispatch requires SignalR with the in-process backplane.");
        }

        try
        {
            await using var connection = await connections.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await PostgreSqlRealtimeOutboxStore.SetWorkerRoleAsync(
                connection,
                transaction,
                cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT
                  current_user='paqueteria_worker'
                  AND EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_roles
                    WHERE rolname=current_user
                      AND rolbypassrls=false
                  )
                  AND to_regprocedure('security.claim_outbox(text,integer,interval)') IS NOT NULL
                  AND to_regprocedure('security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)') IS NOT NULL
                  AND to_regprocedure('security.requeue_stale_outbox(interval,integer,integer)') IS NOT NULL
                  AND to_regprocedure('security.claim_location_outbox(text,integer,interval)') IS NOT NULL
                  AND to_regprocedure('security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)') IS NOT NULL
                  AND to_regprocedure('security.requeue_stale_location_outbox(interval,integer,integer)') IS NOT NULL
                  AND has_function_privilege(
                    current_user,
                    'security.claim_outbox(text,integer,interval)',
                    'EXECUTE')
                  AND has_function_privilege(
                    current_user,
                    'security.claim_location_outbox(text,integer,interval)',
                    'EXECUTE')
                  AND has_function_privilege(
                    current_user,
                    'security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)',
                    'EXECUTE')
                  AND has_function_privilege(
                    current_user,
                    'security.requeue_stale_outbox(interval,integer,integer)',
                    'EXECUTE')
                  AND has_function_privilege(
                    current_user,
                    'security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)',
                    'EXECUTE')
                  AND has_function_privilege(
                    current_user,
                    'security.requeue_stale_location_outbox(interval,integer,integer)',
                    'EXECUTE');
                """,
                connection,
                transaction);
            var valid = await command.ExecuteScalarAsync(cancellationToken) is true;
            await transaction.RollbackAsync(cancellationToken);
            return valid
                ? HealthCheckResult.Healthy(
                    "Worker database and canonical outbox functions are available.")
                : HealthCheckResult.Unhealthy(
                    "Canonical outbox functions or execute grants are unavailable.");
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy(
                "Worker database readiness failed.",
                exception);
        }
    }
}
