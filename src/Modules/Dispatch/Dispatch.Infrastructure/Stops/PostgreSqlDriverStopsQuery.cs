using System.Diagnostics.Metrics;
using Dispatch.Application.Stops;
using Dispatch.Domain;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Stops;

public sealed class PostgreSqlDriverStopsQuery(
    TenantTransactionContext<DispatchDbContext> transactionContext,
    ILogger<PostgreSqlDriverStopsQuery> logger) : IDriverStopsQuery
{
    private static readonly Meter Meter = new("Paqueteria.Dispatch");
    private static readonly Histogram<long> StopsCount =
        Meter.CreateHistogram<long>("dispatch.driver_stops.count");

    public async Task<IReadOnlyList<DriverStopResult>> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DriverStopsForbiddenException();
        }

        var result = await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(actorId, [organizationId]),
            async (dbContext, token) =>
            {
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!
                    .GetDbTransaction();
                var driverId = await ResolveDriverIdAsync(
                    connection,
                    transaction,
                    actorId,
                    organizationId,
                    token);
                if (driverId is null)
                {
                    throw new DriverStopsForbiddenException();
                }

                return await ReadStopsAsync(
                    connection,
                    transaction,
                    driverId.Value,
                    organizationId,
                    token);
            },
            cancellationToken);

        StopsCount.Record(result.Count);
        logger.LogInformation(
            "Dispatch driver stops result; tenant {TenantId}; actor {ActorId}; count {Count}",
            organizationId,
            actorId,
            result.Count);
        return result;
    }

    private static async Task<Guid?> ResolveDriverIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT p.id
            FROM drivers.driver_profiles p
            JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
            WHERE p.user_id=@actor_id AND p.org_id=@organization_id
              AND p.driver_type='OWN' AND p.status='ACTIVE'
              AND EXISTS (
                SELECT 1 FROM organizations.organization_memberships m
                WHERE m.user_id=p.user_id AND m.organization_id=p.org_id
                  AND m.role='DRIVER' AND m.status='ACTIVE'
              )
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("actor_id", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid id ? id : null;
    }

    private static async Task<IReadOnlyList<DriverStopResult>> ReadStopsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT o.public_id,o.status,origin.address_summary,destination.address_summary,
                   (
                     EXISTS (
                       SELECT 1 FROM custody.proofs p
                       WHERE p.order_id=o.id AND p.proof_type='PICKUP_PHOTO'
                     )
                     OR EXISTS (
                       SELECT 1 FROM incidents.incidents i
                       WHERE i.order_id=o.id AND i.custody_acquired=true
                     )
                     OR EXISTS (
                       SELECT 1 FROM orders.order_events e
                       WHERE e.order_id=o.id
                         AND e.event_type='ORDER_STATUS_CHANGED'
                         AND e.payload->>'new_status' IN ('PICKED_UP','IN_TRANSIT','DELIVERING')
                     )
                   ) AS custody_acquired
            FROM dispatch.assignments a
            JOIN orders.orders o ON o.id=a.order_id
            JOIN locations.locations origin ON origin.id=o.origin_location_id
            JOIN locations.locations destination ON destination.id=o.destination_location_id
            WHERE a.driver_id=@driver_id
              AND (a.owner_org_id=@organization_id OR a.operator_org_id=@organization_id)
              AND a.status IN ('ACCEPTED','ACTIVE')
              AND o.status IN (
                'ASSIGNED','AT_PICKUP','PICKED_UP','IN_TRANSIT','DELIVERING',
                'FAILED_ATTEMPT','RESCHEDULED','RETURNING'
              )
            ORDER BY a.created_at ASC,a.id ASC
            """;
        var stops = new List<DriverStopResult>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader.GetString(1);
            var projection = DriverStopPolicy.Project(status, reader.GetBoolean(4));
            if (!projection.Included)
            {
                continue;
            }

            stops.Add(new DriverStopResult(
                reader.GetString(0),
                projection.StopType.ToContractValue(),
                status,
                projection.UseOriginAddress ? reader.GetString(2) : reader.GetString(3)));
        }

        return stops;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object value) =>
        new(name, type) { Value = value };
}
