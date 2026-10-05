using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Domain.Tenancy;
using Reporting.Application.Operations;

namespace Reporting.Infrastructure.Operations;

/// <summary>
/// UI-PHASE2-QUEUE-COUNTS-2026-10-05 (getOperationsQueueCounts): one explicit tenant transaction as the runtime
/// role, which cannot bypass RLS; the transaction-local context is set right after BEGIN and the operations role is
/// re-checked inside it; then a single aggregate over the orders RLS lets the selected organization read as owner or
/// operator. Read only: no row lock, no write.
/// </summary>
internal sealed class PostgreSqlOperationsQueueCountsReader(
    NpgsqlDataSource dataSource,
    IOptions<OperationsDashboardOptions> options,
    IOperationsDashboardTelemetry telemetry,
    TimeProvider timeProvider) : IOperationsQueueCountsReader
{
    // The same role check as the operations dashboard: the actor's ACTIVE membership of the ACTIVE selected
    // organization, as DISPATCHER or PLATFORM_ADMIN (MFA is applied by OperationsRolePolicy below).
    internal const string AuthorizationSql = """
        SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
        SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
        SET LOCAL ROLE paqueteria_app;
        SELECT m.role
        FROM identity.users AS u
        JOIN organizations.organization_memberships AS m
          ON m.user_id = u.id
        JOIN organizations.organizations AS organization
          ON organization.id = m.organization_id
        WHERE u.id = @user_id
          AND u.status = 'ACTIVE'
          AND m.organization_id = @organization_id
          AND m.status = 'ACTIVE'
          AND organization.status = 'ACTIVE'
          AND m.role IN ('PLATFORM_ADMIN', 'DISPATCHER')
        ORDER BY CASE m.role WHEN 'PLATFORM_ADMIN' THEN 0 ELSE 1 END
        LIMIT 1;
        """;

    // One aggregate. "unassigned" and "price_review" are exactly the dashboard's unassigned_alert and cost_warning
    // predicates (PostgreSqlOperationsDashboardReader); the assignment lookup uses one_active_assignment_per_order.
    internal const string CountsSql = """
        SELECT
          order_row.status,
          count(*)::bigint AS total,
          count(*) FILTER (
            WHERE order_row.status IN ('READY_FOR_PICKUP', 'RESCHEDULED')
              AND NOT EXISTS (
                SELECT 1
                FROM dispatch.assignments AS candidate
                WHERE candidate.order_id = order_row.id
                  AND candidate.status IN ('ACCEPTED', 'ACTIVE')
              )
          )::bigint AS unassigned,
          count(*) FILTER (
            WHERE COALESCE(order_row.total_cents < order_row.minimum_total_cents_snapshot, false)
              OR COALESCE(order_row.financial_override ?& ARRAY['actor_id', 'reason', 'valid_until'], false)
          )::bigint AS price_review
        FROM orders.orders AS order_row
        GROUP BY order_row.status;
        """;

    public async Task<OperationsQueueCounts> ReadAsync(
        OperationsQueueCountsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ActorId == Guid.Empty || request.OrganizationId == Guid.Empty)
        {
            throw new ArgumentException("Non-empty actor and organization ids are required.", nameof(request));
        }

        using var measurement = telemetry.MeasureLookup();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var role = await AuthorizeAsync(connection, transaction, request, cancellationToken);
            if (role is null || !OperationsRolePolicy.IsAllowed(role.Value, request.MfaSatisfied))
            {
                telemetry.AuthorizationRejected();
                await transaction.RollbackAsync(cancellationToken);
                throw new OperationsDashboardForbiddenException();
            }

            var rows = await ReadRowsAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var counts = OperationsQueueCounts.From(timeProvider.GetUtcNow(), rows);
            telemetry.LookupCompleted();
            return counts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationsDashboardForbiddenException)
        {
            throw;
        }
        catch (OperationsDashboardContractException exception)
        {
            telemetry.ContractInvalid("contract");
            throw new OperationsDashboardUnavailableException(
                "The operations queue counts are inconsistent.",
                exception);
        }
        catch (Exception exception)
        {
            telemetry.LookupFailed("database");
            throw new OperationsDashboardUnavailableException(
                "The operations queue counts are unavailable.",
                exception);
        }
    }

    private async Task<OrganizationRole?> AuthorizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationsQueueCountsRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AuthorizationSql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid>("user_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.ActorId,
        });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>(
            "organization_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = [request.OrganizationId],
        });
        command.Parameters.Add(new NpgsqlParameter<Guid>("organization_id", NpgsqlDbType.Uuid)
        {
            TypedValue = request.OrganizationId,
        });
        OrganizationRole? role = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                role = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) switch
                {
                    "PLATFORM_ADMIN" => OrganizationRole.PlatformAdmin,
                    "DISPATCHER" => OrganizationRole.Dispatcher,
                    _ => role,
                };
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
        return role;
    }

    private async Task<IReadOnlyList<OperationsQueueStatusRow>> ReadRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CountsSql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };
        var rows = new List<OperationsQueueStatusRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OperationsQueueStatusRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }
}

internal sealed class DisabledOperationsQueueCountsReader : IOperationsQueueCountsReader
{
    public Task<OperationsQueueCounts> ReadAsync(
        OperationsQueueCountsRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<OperationsQueueCounts>(
            new OperationsDashboardUnavailableException("The operations queue counts are disabled."));
}
