using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Realtime.Application.Configuration;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class RealtimeOutboxHealthCheck(
    RealtimeDispatcherConnectionFactory connections,
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
            await PostgreSqlRealtimeOutboxStore.SetDispatcherRoleAsync(
                connection,
                transaction,
                cancellationToken);
            // RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: ready only as paqueteria_realtime (NOBYPASSRLS) with the six
            // lease-token functions executable, and only while the session login has the exact shape the runtime login
            // provisioning enforces: LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS and a
            // member, directly or through any chain, of paqueteria_realtime and of no other role. Membership in any
            // other role (the Worker or API runtime role, the migrator, bootstrap, maintenance or any executor) would let
            // code in the API process SET ROLE into it, so the realtime credential is never reported ready that way.
            await using var command = new NpgsqlCommand(
                """
                SELECT
                  current_user='paqueteria_realtime'
                  AND EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_roles
                    WHERE rolname=current_user
                      AND rolbypassrls=false
                  )
                  AND EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_roles login
                    WHERE login.rolname=session_user
                      AND login.rolcanlogin
                      AND NOT login.rolsuper
                      AND NOT login.rolbypassrls
                      AND NOT login.rolcreaterole
                      AND NOT login.rolcreatedb
                      AND NOT login.rolreplication
                      AND NOT login.rolinherit
                  )
                  AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_roles r
                    WHERE r.rolname NOT IN ('paqueteria_realtime', session_user)
                      AND pg_has_role(session_user, r.oid, 'MEMBER')
                  )
                  AND to_regprocedure('security.claim_realtime_outbox(text,integer,interval)') IS NOT NULL
                  AND to_regprocedure('security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)') IS NOT NULL
                  AND to_regprocedure('security.requeue_stale_realtime_outbox(interval,integer,integer)') IS NOT NULL
                  AND to_regprocedure('security.claim_location_outbox(text,integer,interval)') IS NOT NULL
                  AND to_regprocedure('security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)') IS NOT NULL
                  AND to_regprocedure('security.requeue_stale_location_outbox(interval,integer,integer)') IS NOT NULL
                  AND has_function_privilege(
                    current_user,
                    'security.claim_realtime_outbox(text,integer,interval)',
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
                    'security.requeue_stale_realtime_outbox(interval,integer,integer)',
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
                    "Realtime dispatcher database role and its lease-token outbox functions are available.")
                : HealthCheckResult.Unhealthy(
                    "Realtime dispatcher role, its login shape, its lease-token outbox functions or their execute grants are unavailable.");
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy(
                "Realtime dispatcher database readiness failed.",
                exception);
        }
    }
}
