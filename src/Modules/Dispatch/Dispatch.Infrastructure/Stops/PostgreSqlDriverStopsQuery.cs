using System.Diagnostics.Metrics;
using Dispatch.Application.Stops;
using Dispatch.Domain;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Locations;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Stops;

public sealed class PostgreSqlDriverStopsQuery(
    TenantTransactionContext<DispatchDbContext> transactionContext,
    ILocationPiiReader locationReader,
    IClock clock,
    ILogger<PostgreSqlDriverStopsQuery> logger) : IDriverStopsQuery
{
    private static readonly Meter Meter = new("Paqueteria.Dispatch");
    private static readonly Histogram<long> StopsCount =
        Meter.CreateHistogram<long>("dispatch.driver_stops.count");
    private static readonly Counter<long> DetailsUnavailableCount =
        Meter.CreateCounter<long>("dispatch.driver_stops.details_unavailable");

    public async Task<DriverStopListResult> ListCurrentDriverStopsAsync(
        Guid actorId,
        Guid organizationId,
        bool includeDetails,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DriverStopsForbiddenException();
        }

        var now = clock.UtcNow;
        var (rows, completedToday) = await transactionContext.ExecuteAsync(
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

                var stops = await ReadStopsAsync(
                    connection,
                    transaction,
                    driverId.Value,
                    organizationId,
                    token);
                var completed = await CountCompletedTodayAsync(
                    connection,
                    transaction,
                    actorId,
                    driverId.Value,
                    organizationId,
                    now,
                    token);
                return (stops, completed);
            },
            cancellationToken);

        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the protected address and contact name are read in one batch only
        // after the tenant transaction closed, only for the requesting driver's own stops and only when asked for. A
        // stop whose details cannot be read is still listed, with details_available false and no partial details;
        // the response never fails because of them and nothing personal is logged (only a count).
        var texts = includeDetails && rows.Count > 0
            ? await ReadLocationTextsAsync(rows, cancellationToken)
            : null;
        var stops = new DriverStopResult[rows.Count];
        var unavailable = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            stops[index] = ToResult(rows[index], texts?[index], includeDetails, out var detailsMissing);
            if (detailsMissing)
            {
                unavailable++;
            }
        }

        StopsCount.Record(stops.Length);
        if (unavailable > 0)
        {
            DetailsUnavailableCount.Add(unavailable);
            logger.LogWarning(
                "Dispatch driver stop details unavailable; tenant {TenantId}; actor {ActorId}; stops {Count}",
                organizationId,
                actorId,
                unavailable);
        }

        logger.LogInformation(
            "Dispatch driver stops result; tenant {TenantId}; actor {ActorId}; count {Count}; details {Details}",
            organizationId,
            actorId,
            stops.Length,
            includeDetails);
        return new DriverStopListResult(stops, completedToday);
    }

    private async Task<IReadOnlyList<LocationPiiText?>?> ReadLocationTextsAsync(
        IReadOnlyList<StopRow> rows,
        CancellationToken cancellationToken)
    {
        try
        {
            var texts = await locationReader.ReadAsync(
                rows.Select(row => row.Location.Protected).ToArray(),
                cancellationToken);
            return texts.Count == rows.Count ? texts : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The reader never returns a partial value; any failure leaves every stop without details.
            return null;
        }
    }

    private static DriverStopResult ToResult(
        StopRow row,
        LocationPiiText? text,
        bool includeDetails,
        out bool detailsMissing)
    {
        string? shortName = null;
        string? fullName = null;
        DriverStopNavigationResult? navigation = null;
        if (text is not null &&
            IsCoordinate(row.Location.Latitude, 90) &&
            IsCoordinate(row.Location.Longitude, 180) &&
            DriverStopAddressText.TryNormalize(text.AddressText, text.References, out var address, out var references))
        {
            // The owner's answers "Completo en recolección" and "Completo, como recolección": the full name on a
            // PICKUP or RETURN stop, the short name on a DELIVERY stop; the other form stays null.
            var contact = DriverStopContactName.ForStop(row.StopKind, text.ContactName);
            shortName = contact.ShortName;
            fullName = contact.FullName;
            navigation = new DriverStopNavigationResult(
                row.Location.Latitude,
                row.Location.Longitude,
                address,
                references);
        }

        detailsMissing = includeDetails && navigation is null;
        return new DriverStopResult(
            row.OrderId,
            row.AggregateVersion,
            row.OrderPublicId,
            row.StopKind.ToContractValue(),
            row.Status,
            row.Location.AddressSummary,
            navigation is not null,
            shortName,
            fullName,
            DriverStopZoneName.Normalize(row.Location.ZoneName),
            row.ServiceWindow,
            row.Cod,
            navigation);
    }

    private static bool IsCoordinate(double value, double bound) =>
        double.IsFinite(value) && value >= -bound && value <= bound;

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

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 (owner answer "Solo el número"): counts the status changes to
    /// PICKED_UP, DELIVERED or RETURNED that this driver made today, in America/Mazatlan time, on orders of the
    /// selected organization the driver is or was assigned to (<see cref="DriverStopsCompletedToday"/>). Only the
    /// count leaves the database; the day bounds are computed by PostgreSQL from the request time, in its own
    /// time zone database.
    /// </summary>
    private static async Task<int> CountCompletedTodayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid driverId,
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT count(*)::integer
            FROM orders.order_events e
            WHERE e.actor_id=@actor_id
              AND (e.owner_org_id=@organization_id OR e.operator_org_id=@organization_id)
              AND e.event_type='ORDER_STATUS_CHANGED'
              AND e.payload->>'new_status' = ANY(@completing)
              AND e.occurred_at >= (date_trunc('day', @now AT TIME ZONE @zone) AT TIME ZONE @zone)
              AND e.occurred_at < ((date_trunc('day', @now AT TIME ZONE @zone) + interval '1 day') AT TIME ZONE @zone)
              AND EXISTS (
                SELECT 1 FROM dispatch.assignments a
                WHERE a.order_id=e.order_id AND a.driver_id=@driver_id
              )
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(P("actor_id", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(P("organization_id", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P(
            "completing",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            DriverStopsCompletedToday.CompletingStatuses.ToArray()));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime()));
        command.Parameters.Add(P("zone", NpgsqlDbType.Text, DriverStopsCompletedToday.TimeZone));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int count ? count : 0;
    }

    private static async Task<IReadOnlyList<StopRow>> ReadStopsAsync(
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
        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: both locations are read with their protected
        // address and contact name, point and operating zone, and the location the stop heads to is
        // chosen after projection too. The COD is "recorded" exactly when the DELIVERED guard
        // if_cod_expected_then_cod_status_recorded_or_reconciled would hold. Everything is read in
        // this tenant transaction under RLS; nothing is decrypted here.
        const string sql =
            """
            SELECT o.id,o.version,o.public_id,o.status,origin.address_summary,destination.address_summary,
                   EXISTS (
                     SELECT 1 FROM orders.order_events e
                     WHERE e.order_id=o.id AND e.owner_org_id=o.owner_org_id
                       AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'='PICKED_UP'
                   ) AS custody_acquired,
                   r.id,r.scheduled_for,r.created_at,
                   stop.pickup_sequence,stop.delivery_sequence,stop.return_sequence,stop.any_sequence,
                   origin.id,origin.owner_org_id,origin.pii_key_version,origin.address_ciphertext,
                   origin.contact_name_ciphertext,public.ST_Y(origin.point)::double precision,
                   public.ST_X(origin.point)::double precision,origin_zone.name,
                   destination.id,destination.owner_org_id,destination.pii_key_version,destination.address_ciphertext,
                   destination.contact_name_ciphertext,public.ST_Y(destination.point)::double precision,
                   public.ST_X(destination.point)::double precision,destination_zone.name,
                   o.service_window_from,o.service_window_to,o.cod_expected_cents,
                   EXISTS (
                     SELECT 1 FROM finance.cod_transactions c
                     WHERE c.order_id=o.id AND c.status IN ('RECORDED','RECONCILED')
                       AND c.amount_cents=o.cod_expected_cents
                   ) AS cod_recorded
            FROM dispatch.assignments a
            JOIN orders.orders o ON o.id=a.order_id
            JOIN locations.locations origin ON origin.id=o.origin_location_id
            JOIN locations.locations destination ON destination.id=o.destination_location_id
            LEFT JOIN locations.operating_zones origin_zone ON origin_zone.id=origin.operating_zone_id
            LEFT JOIN locations.operating_zones destination_zone ON destination_zone.id=destination.operating_zone_id
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
        var stops = new List<(StopRow Stop, RouteKey Route)>();
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

            var location = ReadLocation(
                reader,
                projection.UseOriginAddress ? OriginColumns : DestinationColumns,
                projection.UseOriginAddress ? reader.GetString(4) : reader.GetString(5));
            var window = reader.IsDBNull(30) || reader.IsDBNull(31)
                ? null
                : new DriverStopServiceWindowResult(
                    reader.GetFieldValue<DateTimeOffset>(30),
                    reader.GetFieldValue<DateTimeOffset>(31));
            var codExpectedCents = reader.GetInt64(32);
            // Cash is collected at the door: only a DELIVERY stop of an order that expects COD carries it.
            var cod = projection.StopType == DriverStopType.Delivery && codExpectedCents > 0
                ? new DriverStopCodResult(codExpectedCents, reader.GetBoolean(33))
                : null;

            stops.Add((
                new StopRow(
                    reader.GetGuid(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    projection.StopType,
                    status,
                    location,
                    window,
                    cod),
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

    /// <summary>First ordinal of the eight origin location columns.</summary>
    private const int OriginColumns = 14;

    /// <summary>First ordinal of the eight destination location columns.</summary>
    private const int DestinationColumns = 22;

    private static StopLocationRow ReadLocation(NpgsqlDataReader reader, int first, string addressSummary) => new(
        new StoredLocationPii(
            reader.GetGuid(first + 1),
            reader.GetGuid(first),
            reader.GetString(first + 2),
            reader.GetFieldValue<byte[]>(first + 3),
            reader.IsDBNull(first + 4) ? null : reader.GetFieldValue<byte[]>(first + 4)),
        addressSummary,
        reader.GetDouble(first + 5),
        reader.GetDouble(first + 6),
        reader.IsDBNull(first + 7) ? null : reader.GetString(first + 7));

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

    /// <summary>One projected stop as read in the transaction, before its protected values are read.</summary>
    private sealed record StopRow(
        Guid OrderId,
        long AggregateVersion,
        string OrderPublicId,
        DriverStopType StopKind,
        string Status,
        StopLocationRow Location,
        DriverStopServiceWindowResult? ServiceWindow,
        DriverStopCodResult? Cod)
    {
        public override string ToString() => $"StopRow {{ OrderId = {OrderId:D}, [details redacted] }}";
    }

    /// <summary>The location the stop heads to; the point and the zone name are not personal on their own.</summary>
    private sealed record StopLocationRow(
        StoredLocationPii Protected,
        string AddressSummary,
        double Latitude,
        double Longitude,
        string? ZoneName)
    {
        public override string ToString() => "StopLocationRow { [redacted] }";
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object value) =>
        new(name, type) { Value = value };
}
