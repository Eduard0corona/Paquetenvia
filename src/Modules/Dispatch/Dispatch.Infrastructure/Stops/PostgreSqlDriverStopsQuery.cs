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
              AND p.driver_type IN ('OWN','EXTERNAL') AND p.status='ACTIVE'
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
        // Custody is the single derivation ORD-002 and INC-001 share: a PICKED_UP status change
        // in the order history. A pickup photo is not custody: an incident at pickup carries one.
        // Stops follow the driver's route: routed assignments first, by route and by the sequence
        // of the route stop of the type the driver is heading to — the PICKUP stop before custody,
        // the DELIVERY stop after it, the RETURN stop while returning. The stop type comes from
        // DriverStopPolicy, so the sequence is chosen after projection rather than re-derived in
        // SQL. Unrouted assignments keep their assignment order after the routed ones.
        const string sql =
            """
            SELECT o.id,o.version,o.public_id,o.status,origin.address_summary,destination.address_summary,
                   EXISTS (
                     SELECT 1 FROM orders.order_events e
                     WHERE e.order_id=o.id AND e.owner_org_id=o.owner_org_id
                       AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'='PICKED_UP'
                   ) AS custody_acquired,
                   r.id,r.scheduled_for,r.created_at,
                   stop.pickup_sequence,stop.delivery_sequence,stop.return_sequence,stop.any_sequence
            FROM dispatch.assignments a
            JOIN orders.orders o ON o.id=a.order_id
            JOIN locations.locations origin ON origin.id=o.origin_location_id
            JOIN locations.locations destination ON destination.id=o.destination_location_id
            LEFT JOIN routes.routes r
              ON r.id=a.route_id
             AND r.driver_id=a.driver_id
             AND r.status IN ('DRAFT','PLANNED','ACTIVE')
            LEFT JOIN LATERAL (
              SELECT min(s.sequence) FILTER (WHERE s.stop_type='PICKUP') AS pickup_sequence,
                     min(s.sequence) FILTER (WHERE s.stop_type='DELIVERY') AS delivery_sequence,
                     min(s.sequence) FILTER (WHERE s.stop_type='RETURN') AS return_sequence,
                     min(s.sequence) AS any_sequence
              FROM routes.route_stops s
              WHERE s.route_id=r.id AND s.order_id=o.id
            ) stop ON true
            WHERE a.driver_id=@driver_id
              AND (a.owner_org_id=@organization_id OR a.operator_org_id=@organization_id)
              AND a.status IN ('ACCEPTED','ACTIVE')
              AND o.status IN (
                'ASSIGNED','AT_PICKUP','PICKED_UP','IN_TRANSIT','DELIVERING',
                'FAILED_ATTEMPT','RESCHEDULED','RETURNING'
              )
            ORDER BY a.created_at ASC,a.id ASC
            """;
        var stops = new List<(DriverStopResult Stop, RouteKey Route)>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader.GetString(3);
            var projection = DriverStopPolicy.Project(status, reader.GetBoolean(6));
            if (!projection.Included)
            {
                continue;
            }

            var typedSequence = projection.StopType switch
            {
                DriverStopType.Pickup => NullableInt32(reader, 10),
                DriverStopType.Delivery => NullableInt32(reader, 11),
                _ => NullableInt32(reader, 12),
            };
            var route = reader.IsDBNull(7)
                ? RouteKey.Unrouted
                : new RouteKey(
                    reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    // A routed order without a stop of the type it is heading to still belongs
                    // to its route; it keeps the position of its earliest stop there.
                    typedSequence ?? NullableInt32(reader, 13));

            stops.Add((
                new DriverStopResult(
                    reader.GetGuid(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    projection.StopType.ToContractValue(),
                    status,
                    projection.UseOriginAddress ? reader.GetString(4) : reader.GetString(5)),
                route));
        }

        // LINQ ordering is stable, so ties keep the assignment order the query returned.
        return stops
            .OrderBy(item => item.Route.Sequence is null)
            .ThenBy(item => item.Route.ScheduledFor is null)
            .ThenBy(item => item.Route.ScheduledFor)
            .ThenBy(item => item.Route.CreatedAt)
            // PostgreSQL orders uuid bytewise, which the lowercase canonical text form preserves.
            .ThenBy(item => item.Route.RouteId?.ToString("D"), StringComparer.Ordinal)
            .ThenBy(item => item.Route.Sequence)
            .Select(item => item.Stop)
            .ToArray();
    }

    private static int? NullableInt32(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private sealed record RouteKey(
        Guid? RouteId,
        DateOnly? ScheduledFor,
        DateTimeOffset? CreatedAt,
        int? Sequence)
    {
        public static RouteKey Unrouted { get; } = new(null, null, null, null);
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object value) =>
        new(name, type) { Value = value };
}
