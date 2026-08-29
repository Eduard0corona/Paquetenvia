using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Drivers.Application.Eligibility;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Routing.Application.Routes;

namespace Routing.Infrastructure.Routes;

public sealed partial class PostgreSqlRouteService
{
    private async Task<T?> BeginIdempotencyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] requestHash,
        int expectedStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        where T : class
    {
        await using (var advisory = CreateCommand(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));"))
        {
            advisory.Parameters.Add(P(
                "key",
                NpgsqlDbType.Text,
                $"{organizationId:D}:{scope}:{key}"));
            await advisory.ExecuteNonQueryAsync(cancellationToken);
        }

        IdempotencyRow? stored;
        await using (var query = CreateCommand(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
            """))
        {
            query.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            query.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
            query.Parameters.Add(P("key", NpgsqlDbType.Text, key));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            stored = await reader.ReadAsync(cancellationToken)
                ? new(
                    reader.GetFieldValue<byte[]>(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3))
                : null;
        }

        if (stored is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(stored.Hash, requestHash))
            {
                throw Conflict(RoutingConflictCode.IdempotencyConflict);
            }
            if (stored.Status != expectedStatus || stored.ResourceId is null ||
                string.IsNullOrWhiteSpace(stored.Body))
            {
                throw Conflict(RoutingConflictCode.InconsistentReplayEvidence);
            }
            try
            {
                return JsonSerializer.Deserialize<T>(stored.Body, JsonOptions)
                    ?? throw Conflict(RoutingConflictCode.InconsistentReplayEvidence);
            }
            catch (JsonException exception)
            {
                throw Conflict(RoutingConflictCode.InconsistentReplayEvidence, exception);
            }
        }

        await using var insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,
              response_body,resource_id,created_at,expires_at)
            VALUES (@organization,@scope,@key,@hash,NULL,NULL,NULL,@now,@expires)
            """);
        insert.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        insert.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        insert.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        insert.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        insert.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        insert.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            now.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken), "Idempotency reservation failed.");
        return null;
    }

    private async Task CompleteIdempotencyAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        string scope,
        string key,
        byte[] requestHash,
        int status,
        Guid resourceId,
        T response,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=@status,response_body=@body,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@organization AND scope=@scope AND idempotency_key=@key
              AND request_hash=@hash AND response_status IS NULL
              AND response_body IS NULL AND resource_id IS NULL
            """);
        command.Parameters.Add(P("status", NpgsqlDbType.Integer, status));
        command.Parameters.Add(P("body", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(response, JsonOptions)));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, resourceId));
        command.Parameters.Add(P(
            "expires",
            NpgsqlDbType.TimestampTz,
            now.AddMinutes(options.Value.IdempotencyLifetimeMinutes)));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, scope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, key));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Idempotency completion failed.");
    }

    private static async Task AcquireOrderLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key,0));",
            connection,
            transaction);
        command.Parameters.Add(P("key", NpgsqlDbType.Text, $"routing-order:{orderId:D}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<RouteRow?> ReadRouteForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,operator_org_id,driver_id,city_id,service_area_id,status,version,scheduled_for
            FROM routes.routes
            WHERE id=@route AND operator_org_id=@organization
            FOR UPDATE
            """);
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7))
            : null;
    }

    private async Task<OrderRow?> ReadOrderForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,city_id,service_area_id
            FROM orders.orders
            WHERE id=@order
              AND (owner_org_id=@organization OR operator_org_id=@organization)
            FOR UPDATE
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2))
            : null;
    }

    private async Task<AssignmentRow?> ReadAssignmentForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,driver_id,route_id,assignment_type,status
            FROM dispatch.assignments
            WHERE order_id=@order
              AND status IN ('ACCEPTED','ACTIVE')
              AND (owner_org_id=@organization OR operator_org_id=@organization)
            ORDER BY created_at DESC,id DESC
            LIMIT 1
            FOR UPDATE
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4))
            : null;
    }

    private async Task<DriverCapacityRequirement?> ReadCapacityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT weight_grams,dimensions_mm::text
            FROM orders.package_items
            WHERE order_id=@order
            ORDER BY id
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        var packages = new List<(long WeightGrams, int? Length, int? Width, int? Height)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryReadDimensions(
                    reader.GetString(1),
                    out var length,
                    out var width,
                    out var height))
            {
                return null;
            }
            packages.Add((reader.GetInt32(0), length, width, height));
        }
        return RoutingCapacityAggregator.TryAggregate(packages, out var capacity) ? capacity : null;
    }

    internal static bool TryReadDimensions(
        string json,
        out int? length,
        out int? width,
        out int? height)
    {
        length = null;
        width = null;
        height = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                TryReadDimension(document.RootElement, "length_mm", out length) &&
                TryReadDimension(document.RootElement, "width_mm", out width) &&
                TryReadDimension(document.RootElement, "height_mm", out height);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDimension(JsonElement element, string name, out int? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property)) return true;
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var parsed) || parsed <= 0) return false;
        value = parsed;
        return true;
    }

    private async Task<int> NextSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT COALESCE(max(sequence),0)::integer + 1 FROM routes.route_stops WHERE route_id=@route;");
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task InsertStopAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid routeId,
        Guid stopId,
        Guid orderId,
        int sequence,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO routes.route_stops(
              id,route_id,order_id,operator_org_id,sequence,stop_type,status)
            VALUES (@id,@route,@order,@organization,@sequence,'DELIVERY','PENDING')
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, stopId));
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("sequence", NpgsqlDbType.Integer, sequence));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Route stop insert failed.");
    }

    private async Task LinkAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid assignmentId,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE dispatch.assignments
            SET route_id=@route
            WHERE id=@assignment AND route_id IS NULL
              AND assignment_type='OWN' AND status IN ('ACCEPTED','ACTIVE')
            """);
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("assignment", NpgsqlDbType.Uuid, assignmentId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Conflict(RoutingConflictCode.ConcurrencyConflict);
        }
    }

    private async Task<int> IncrementVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        int expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE routes.routes
            SET version=version+1,updated_at=@now
            WHERE id=@route AND status='DRAFT' AND version=@expected
            RETURNING version
            """);
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("expected", NpgsqlDbType.Integer, expectedVersion));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int version
            ? version
            : throw Conflict(RoutingConflictCode.VersionConflict);
    }

    private async Task<bool> RouteStopExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM routes.route_stops WHERE route_id=@route AND order_id=@order);");
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<bool> ActiveRouteStopExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid currentRouteId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT EXISTS(
              SELECT 1
              FROM routes.route_stops s
              JOIN routes.routes r ON r.id=s.route_id
              WHERE s.order_id=@order AND s.route_id<>@route
                AND r.status IN ('DRAFT','PLANNED','ACTIVE'))
            """);
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, currentRouteId));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<StopRow?> ReadStopAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid routeId,
        Guid stopId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql =
            """
            SELECT id,order_id,sequence
            FROM routes.route_stops
            WHERE id=@stop AND route_id=@route AND operator_org_id=@organization
            """ + (forUpdate ? " FOR UPDATE" : string.Empty);
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(P("stop", NpgsqlDbType.Uuid, stopId));
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2))
            : null;
    }

    private async Task<IReadOnlyList<Guid>> ReadShiftedStopIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        int removedSequence,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT id FROM routes.route_stops WHERE route_id=@route AND sequence>@sequence ORDER BY sequence;");
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("sequence", NpgsqlDbType.Integer, removedSequence));
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task DeleteStopAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid stopId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "DELETE FROM routes.route_stops WHERE id=@stop;");
        command.Parameters.Add(P("stop", NpgsqlDbType.Uuid, stopId));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Route stop delete failed.");
    }

    private async Task UnlinkAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "UPDATE dispatch.assignments SET route_id=NULL WHERE order_id=@order AND route_id=@route;");
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Assignment route unlink failed.");
    }

    private async Task CompactSequencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        await using (var offset = CreateCommand(
            connection,
            transaction,
            """
            WITH route_offset AS (
              SELECT COALESCE(max(sequence),0)::integer + 1 AS value
              FROM routes.route_stops WHERE route_id=@route)
            UPDATE routes.route_stops
            SET sequence=sequence+route_offset.value
            FROM route_offset
            WHERE route_id=@route
            """))
        {
            offset.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
            await offset.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var compact = CreateCommand(
            connection,
            transaction,
            """
            WITH ordered AS (
              SELECT id,row_number() OVER (ORDER BY sequence,id)::integer AS value
              FROM routes.route_stops WHERE route_id=@route)
            UPDATE routes.route_stops s
            SET sequence=ordered.value
            FROM ordered
            WHERE s.id=ordered.id
            """);
        compact.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        await compact.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> ReadAllStopIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT id FROM routes.route_stops WHERE route_id=@route ORDER BY sequence,id" +
            (forUpdate ? " FOR UPDATE" : string.Empty);
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        var values = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetGuid(0));
        return values;
    }

    private async Task ReorderSequencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid routeId,
        IReadOnlyList<Guid> stopIds,
        CancellationToken cancellationToken)
    {
        await using (var offset = CreateCommand(
            connection,
            transaction,
            """
            WITH route_offset AS (
              SELECT COALESCE(max(sequence),0)::integer + 1 AS value
              FROM routes.route_stops WHERE route_id=@route)
            UPDATE routes.route_stops
            SET sequence=sequence+route_offset.value
            FROM route_offset
            WHERE route_id=@route
            """))
        {
            offset.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
            await offset.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var reorder = CreateCommand(
            connection,
            transaction,
            """
            WITH requested AS (
              SELECT id,ordinality::integer AS sequence
              FROM unnest(@stop_ids::uuid[]) WITH ORDINALITY AS value(id,ordinality))
            UPDATE routes.route_stops s
            SET sequence=requested.sequence
            FROM requested
            WHERE s.route_id=@route AND s.id=requested.id
            """);
        reorder.Parameters.Add(new NpgsqlParameter<Guid[]>(
            "stop_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = stopIds.ToArray(),
        });
        reorder.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        if (await reorder.ExecuteNonQueryAsync(cancellationToken) != stopIds.Count)
        {
            throw Conflict(RoutingConflictCode.InvalidStopSet);
        }
    }

    private async Task<RouteDetailResult> ReadDetailRequiredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        const string routeSql =
            """
            SELECT r.id,r.status,r.version,r.driver_id,r.city_id,r.service_area_id,
                   r.scheduled_for,
                   COALESCE((
                     SELECT sum(a.cost_cents)
                     FROM dispatch.assignments a
                     WHERE a.route_id=r.id AND a.status IN ('ACCEPTED','ACTIVE')
                   ),0)::bigint,
                   (SELECT count(*)::integer FROM routes.route_stops s WHERE s.route_id=r.id)
            FROM routes.routes r
            WHERE r.id=@route AND r.operator_org_id=@organization
            """;
        RouteResult route;
        await using (var command = CreateCommand(connection, transaction, routeSql))
        {
            command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
            command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new RoutingNotFoundException();
            route = ReadRoute(reader);
        }

        await using var stopsCommand = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,order_id,sequence,stop_type,status
            FROM routes.route_stops
            WHERE route_id=@route AND operator_org_id=@organization
            ORDER BY sequence ASC,id ASC
            """);
        stopsCommand.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        stopsCommand.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        var stops = new List<RouteStopResult>();
        await using (var reader = await stopsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                stops.Add(new(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        return new(
            route.Id,
            route.Status,
            route.Version,
            route.DriverId,
            route.CityId,
            route.ServiceAreaId,
            route.ScheduledFor,
            route.AssignmentCostCentsTotal,
            route.StopCount,
            stops);
    }

    private static RouteResult ReadRoute(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetGuid(3),
        reader.GetGuid(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6),
        reader.GetInt64(7),
        reader.GetInt32(8));

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid routeId,
        int routeVersion,
        IReadOnlyList<Guid> changedStopIds,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var tenantContext = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { organizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            schema_version = "route-changed-v1",
            route_id = routeId,
            route_version = routeVersion,
            changed_stop_ids = changedStopIds,
            occurred_at = occurredAt,
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,
              aggregate_version,payload,priority,status,attempts,available_at,
              locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at)
            VALUES (@id,@organization,@tenant,@topic,'Route',@route,@version,@payload,
                    50,'PENDING',0,@now,NULL,NULL,NULL,NULL,NULL,@now,NULL)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, Guid.NewGuid()));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(P("tenant", NpgsqlDbType.Jsonb, tenantContext));
        command.Parameters.Add(P("topic", NpgsqlDbType.Text, OutboxTopic));
        command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
        command.Parameters.Add(P("version", NpgsqlDbType.Integer, routeVersion));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "Route outbox insert failed.");
        await failureInjector.OnStageAsync(RoutingTransactionStage.OutboxInserted, cancellationToken);
    }

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        string action,
        Guid routeId,
        string? requestId,
        object payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var redacted = auditRedactor.Redact(JsonSerializer.SerializeToElement(payload, JsonOptions));
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                organizationId,
                actorId,
                action,
                "Route",
                routeId,
                requestId,
                redacted,
                occurredAt),
            cancellationToken);
        await failureInjector.OnStageAsync(RoutingTransactionStage.AuditInserted, cancellationToken);
    }
}
