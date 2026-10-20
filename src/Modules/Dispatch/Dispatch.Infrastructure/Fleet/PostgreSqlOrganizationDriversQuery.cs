using System.Diagnostics.Metrics;
using Dispatch.Application.Assignments;
using Dispatch.Application.Fleet;
using Dispatch.Domain;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Fleet;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listOrganizationDrivers). A read only: one explicit tenant transaction
/// (set_config after BEGIN, SET LOCAL ROLE paqueteria_app, FORCE RLS), the capability of the assignment picker read
/// first (DISPATCHER, PLATFORM_ADMIN with MFA), then one page of the active organization's OWN drivers that are not
/// INACTIVE with their current stops and the capture time of their latest position. Nothing is written, no row is
/// locked and no cross-module flow is added; the indexes assignments_driver_idx and driver_positions_tenant_time_idx
/// already serve both lookups.
/// </summary>
public sealed class PostgreSqlOrganizationDriversQuery(
    TenantTransactionContext<DispatchDbContext> transactionContext,
    IDispatchAssignmentAuthorizer authorizer,
    IDispatchAuthorizationReader authorizationReader,
    ILogger<PostgreSqlOrganizationDriversQuery> logger) : IOrganizationDriversQuery
{
    private static readonly Meter Meter = new("Paqueteria.Dispatch");
    private static readonly Histogram<long> ListedCount =
        Meter.CreateHistogram<long>("dispatch.organization_drivers.count");

    public async Task<OrganizationDriverPage> ListAsync(
        ListOrganizationDriversQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ActorId == Guid.Empty || query.OrganizationId == Guid.Empty)
        {
            throw new AssignmentForbiddenException();
        }

        OrganizationDriverPage page;
        try
        {
            page = await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(query.ActorId, [query.OrganizationId]),
                async (dbContext, token) =>
                {
                    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                    var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

                    var authorization = await authorizationReader.ReadAsync(
                        connection, transaction, query.ActorId, query.OrganizationId, token);
                    if (!authorizer.IsAuthorized(new DispatchAssignmentAuthorizationContext(
                            authorization.ActiveRole,
                            authorization.UserActive,
                            authorization.MembershipActive,
                            query.MfaSatisfied)))
                    {
                        throw new AssignmentForbiddenException();
                    }

                    var rows = await ReadDriversAsync(connection, transaction, query, token);
                    var hasMore = rows.Count > OrganizationDriverPolicy.PageSize;
                    var listed = rows.Take(OrganizationDriverPolicy.PageSize)
                        .Select(row => OrganizationDriverPolicy.ToResult(
                            row.DriverId, row.VehicleType, row.Status, row.ActiveStopCount, row.LastPositionAt))
                        .ToArray();
                    return new OrganizationDriverPage(
                        listed,
                        hasMore ? OrganizationDriverCursorCodec.Encode(listed[^1].DriverId) : null);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not (AssignmentForbiddenException or AssignmentInfrastructureException))
        {
            // The store failure is internal evidence: the caller only learns that the list is unavailable.
            throw new AssignmentInfrastructureException("The organization driver list is unavailable.", exception);
        }

        ListedCount.Record(page.Items.Count);
        // OBS-003-TENANT-PSEUDONYM-2026-10-12: the organization and the actor reach this line only as the pseudonym and
        // the role kind the logger factory adds, never as their ids.
        logger.LogInformation(
            "Dispatch organization drivers listed; count {Count}",
            page.Items.Count);
        return page;
    }

    private static async Task<IReadOnlyList<DriverRow>> ReadDriversAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ListOrganizationDriversQuery query,
        CancellationToken cancellationToken)
    {
        // Every OWN profile of the active organization that is not retired (INACTIVE), as the assignment picker lists
        // them. A stop is an ACCEPTED or ACTIVE assignment of the driver, of an order the organization owns or operates,
        // whose order is in a DriverStopPolicy stop status: exactly what listMyStops shows the driver. Only the capture
        // time of the latest position is read (the dashboard's ordering); its point never leaves PostgreSQL. No column
        // of identity.users, organization_memberships or driver_documents is read.
        const string sql =
            """
            SELECT p.id,p.vehicle_type,p.status,
                   (SELECT count(*)::integer
                    FROM dispatch.assignments a
                    JOIN orders.orders o ON o.id=a.order_id
                    WHERE a.driver_id=p.id
                      AND a.status IN ('ACCEPTED','ACTIVE')
                      AND (a.owner_org_id=@organization_id OR a.operator_org_id=@organization_id)
                      AND o.status=ANY(@stop_statuses)),
                   (SELECT position.captured_at
                    FROM drivers.driver_positions position
                    WHERE position.org_id=p.org_id AND position.driver_id=p.id
                    ORDER BY position.captured_at DESC,position.received_at DESC,position.id DESC
                    LIMIT 1)
            FROM drivers.driver_profiles p
            WHERE p.org_id=@organization_id AND p.driver_type='OWN' AND p.status<>'INACTIVE'
              AND (@after IS NULL OR p.id>@after)
            ORDER BY p.id
            LIMIT @limit
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, query.OrganizationId));
        command.Parameters.Add(P(
            "stop_statuses", NpgsqlDbType.Array | NpgsqlDbType.Text, DriverStopPolicy.OrderStatuses.ToArray()));
        command.Parameters.Add(P("after", NpgsqlDbType.Uuid, query.AfterDriverId));
        command.Parameters.Add(P("limit", NpgsqlDbType.Integer, OrganizationDriverPolicy.PageSize + 1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DriverRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DriverRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private sealed record DriverRow(
        Guid DriverId,
        string VehicleType,
        string Status,
        int ActiveStopCount,
        DateTimeOffset? LastPositionAt);
}

public sealed class DisabledOrganizationDriversQuery : IOrganizationDriversQuery
{
    public Task<OrganizationDriverPage> ListAsync(
        ListOrganizationDriversQuery query,
        CancellationToken cancellationToken) =>
        throw new AssignmentForbiddenException();
}
