using Npgsql;
using NpgsqlTypes;
using Realtime.Application.Dispatching;

namespace Realtime.Infrastructure.Dispatching;

internal sealed class PostgreSqlRealtimeOutboxEvidenceReader(
    RealtimeWorkerConnectionFactory connections) : IRealtimeOutboxEvidenceReader
{
    public Task<OrderEventEvidence?> ReadOrderEventAsync(
        Guid ownerOrganizationId,
        Guid orderEventId,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string sql =
                    """
                    SELECT e.id,e.order_id,e.owner_org_id,e.aggregate_version,e.event_type,
                           e.payload->>'previous_status',e.payload->>'new_status',
                           e.public_event_code,e.occurred_at,o.public_id
                    FROM orders.order_events e
                    JOIN orders.orders o
                      ON o.id=e.order_id AND o.owner_org_id=e.owner_org_id
                    WHERE e.id=@event_id AND e.owner_org_id=@owner
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("event_id", NpgsqlDbType.Uuid, orderEventId));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) ||
                    reader.IsDBNull(5) ||
                    reader.IsDBNull(6))
                {
                    return null;
                }

                return new OrderEventEvidence(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetString(9));
            },
            cancellationToken);

    public async Task<AssignmentEvidence?> ReadAssignmentAsync(
        Guid ownerOrganizationId,
        Guid assignmentId,
        CancellationToken cancellationToken) =>
        await WithOperatorDriverAudienceAsync(
            await ReadOwnerAssignmentAsync(ownerOrganizationId, assignmentId, cancellationToken),
            ActiveAssignmentStatuses,
            cancellationToken);

    private Task<AssignmentEvidence?> ReadOwnerAssignmentAsync(
        Guid ownerOrganizationId,
        Guid assignmentId,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string sql =
                    """
                    SELECT a.id,a.order_id,a.driver_id,a.owner_org_id,a.operator_org_id,a.status,
                           e.aggregate_version,e.id,e.occurred_at,
                           (
                             a.assignment_type IN ('OWN','EXTERNAL')
                             AND a.status IN ('ACCEPTED','ACTIVE')
                             AND p.id=a.driver_id
                             AND p.org_id=a.owner_org_id
                             AND p.driver_type=a.assignment_type
                             AND p.status='ACTIVE'
                             AND u.status='ACTIVE'
                             AND EXISTS (
                               SELECT 1
                               FROM organizations.organization_memberships m
                               WHERE m.user_id=p.user_id
                                 AND m.organization_id=p.org_id
                                 AND m.role='DRIVER'
                                 AND m.status='ACTIVE'
                             )
                           ) IS TRUE AS driver_authorized
                    FROM dispatch.assignments a
                    JOIN orders.orders o
                      ON o.id=a.order_id AND o.owner_org_id=a.owner_org_id
                    JOIN orders.order_events e
                      ON e.order_id=a.order_id
                     AND e.owner_org_id=a.owner_org_id
                     AND e.event_type='ORDER_STATUS_CHANGED'
                     AND e.payload->>'assignment_id'=@assignment_text
                    LEFT JOIN drivers.driver_profiles p ON p.id=a.driver_id
                    LEFT JOIN identity.users u ON u.id=p.user_id
                    WHERE a.id=@assignment_id AND a.owner_org_id=@owner
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("assignment_id", NpgsqlDbType.Uuid, assignmentId));
                command.Parameters.Add(P("assignment_text", NpgsqlDbType.Text, assignmentId.ToString("D")));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                return new AssignmentEvidence(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetGuid(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetBoolean(9));
            },
            cancellationToken);

    public async Task<AssignmentEvidence?> ReadClosedAssignmentAsync(
        Guid ownerOrganizationId,
        Guid assignmentId,
        long orderVersion,
        CancellationToken cancellationToken) =>
        await WithOperatorDriverAudienceAsync(
            await ReadOwnerClosedAssignmentAsync(ownerOrganizationId, assignmentId, orderVersion, cancellationToken),
            ClosedAssignmentStatuses,
            cancellationToken);

    private Task<AssignmentEvidence?> ReadOwnerClosedAssignmentAsync(
        Guid ownerOrganizationId,
        Guid assignmentId,
        long orderVersion,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                // AI12-ASSIGNMENT-TERMINAL-STATES / D8: a closed assignment is published against the
                // committed order transition that closed it; the affected driver is an audience only
                // while that driver's profile, user and DRIVER membership are active.
                const string sql =
                    """
                    SELECT a.id,a.order_id,a.driver_id,a.owner_org_id,a.operator_org_id,a.status,
                           e.aggregate_version,e.id,e.occurred_at,
                           (
                             a.assignment_type IN ('OWN','EXTERNAL')
                             AND p.id=a.driver_id
                             AND p.org_id=a.owner_org_id
                             AND p.driver_type=a.assignment_type
                             AND p.status='ACTIVE'
                             AND u.status='ACTIVE'
                             AND EXISTS (
                               SELECT 1
                               FROM organizations.organization_memberships m
                               WHERE m.user_id=p.user_id
                                 AND m.organization_id=p.org_id
                                 AND m.role='DRIVER'
                                 AND m.status='ACTIVE'
                             )
                           ) IS TRUE AS driver_authorized
                    FROM dispatch.assignments a
                    JOIN orders.order_events e
                      ON e.order_id=a.order_id
                     AND e.owner_org_id=a.owner_org_id
                     AND e.event_type='ORDER_STATUS_CHANGED'
                     AND e.aggregate_version=@order_version
                    LEFT JOIN drivers.driver_profiles p ON p.id=a.driver_id
                    LEFT JOIN identity.users u ON u.id=p.user_id
                    WHERE a.id=@assignment_id AND a.owner_org_id=@owner
                      AND a.status IN ('COMPLETED','CANCELLED')
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("assignment_id", NpgsqlDbType.Uuid, assignmentId));
                command.Parameters.Add(P("order_version", NpgsqlDbType.Integer, checked((int)orderVersion)));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                return new AssignmentEvidence(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetGuid(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetBoolean(9));
            },
            cancellationToken);

    public async Task<bool> IsDriverAudienceAuthorizedAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid assignmentId,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        if (await IsOwnerDriverAudienceAuthorizedAsync(
                ownerOrganizationId,
                orderId,
                assignmentId,
                driverId,
                cancellationToken))
        {
            return true;
        }

        // DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: the operator's own driver of this exact active
        // assignment. The operator is read from the owner's persisted assignment, never from the payload, and
        // must still be the order's stored operator (ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03).
        var operatorOrganizationId = await ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string sql =
                    """
                    SELECT a.operator_org_id
                    FROM dispatch.assignments a
                    JOIN orders.orders o
                      ON o.id=a.order_id
                     AND o.owner_org_id=a.owner_org_id
                     AND o.operator_org_id=a.operator_org_id
                    WHERE a.id=@assignment_id
                      AND a.order_id=@order_id
                      AND a.driver_id=@driver_id
                      AND a.owner_org_id=@owner
                      AND a.status IN ('ACCEPTED','ACTIVE')
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("assignment_id", NpgsqlDbType.Uuid, assignmentId));
                command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
                command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                return await command.ExecuteScalarAsync(token) as Guid?;
            },
            cancellationToken);
        return operatorOrganizationId is { } operatorId &&
            operatorId != ownerOrganizationId &&
            await IsOperatorDriverAuthorizedAsync(
                ownerOrganizationId,
                operatorId,
                orderId,
                assignmentId,
                driverId,
                ActiveAssignmentStatuses,
                cancellationToken);
    }

    private Task<bool> IsOwnerDriverAudienceAuthorizedAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        Guid assignmentId,
        Guid driverId,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string sql =
                    """
                    SELECT EXISTS (
                      SELECT 1
                      FROM dispatch.assignments a
                      JOIN drivers.driver_profiles p
                        ON p.id=a.driver_id
                       AND p.org_id=a.owner_org_id
                       AND p.driver_type=a.assignment_type
                       AND p.status='ACTIVE'
                      JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
                      JOIN organizations.organization_memberships m
                        ON m.user_id=p.user_id
                       AND m.organization_id=p.org_id
                       AND m.role='DRIVER'
                       AND m.status='ACTIVE'
                      WHERE a.id=@assignment_id
                        AND a.order_id=@order_id
                        AND a.driver_id=@driver_id
                        AND a.owner_org_id=@owner
                        AND a.status IN ('ACCEPTED','ACTIVE')
                    )
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("assignment_id", NpgsqlDbType.Uuid, assignmentId));
                command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
                command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                return await command.ExecuteScalarAsync(token) is true;
            },
            cancellationToken);

    public Task<DriverPositionEvidence?> ReadDriverPositionAsync(
        Guid ownerOrganizationId,
        Guid driverPositionId,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string sql =
                    """
                    SELECT p.id,p.org_id,p.driver_id,
                           public.ST_Y(p.point),public.ST_X(p.point),
                           p.accuracy_m,p.captured_at,p.publish_realtime
                    FROM drivers.driver_positions p
                    WHERE p.id=@position_id AND p.org_id=@owner
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("position_id", NpgsqlDbType.Uuid, driverPositionId));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                return new DriverPositionEvidence(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    Convert.ToDouble(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToDouble(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetBoolean(7));
            },
            cancellationToken);

    public Task<ExternalOfferEvidence?> ReadExternalOfferAsync(
        Guid ownerOrganizationId,
        Guid offerId,
        IReadOnlyList<Guid> requestedAudienceDriverIds,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string offerSql =
                    """
                    SELECT id,owner_org_id,status,commission_cents,expires_at,version,accepted_by_driver_id
                    FROM dispatch.external_offers
                    WHERE id=@offer AND owner_org_id=@owner
                    """;
                ExternalOfferEvidence? offer;
                await using (var command = new NpgsqlCommand(offerSql, connection, transaction))
                {
                    command.Parameters.Add(P("offer", NpgsqlDbType.Uuid, offerId));
                    command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                    await using var reader = await command.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) return null;
                    offer = new ExternalOfferEvidence(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetString(2),
                        reader.GetInt64(3),
                        reader.GetFieldValue<DateTimeOffset>(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetGuid(6),
                        []);
                }

                if (requestedAudienceDriverIds.Count == 0) return offer;
                const string audienceSql =
                    """
                    SELECT p.id
                    FROM drivers.driver_profiles p
                    JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
                    JOIN organizations.organization_memberships m
                      ON m.user_id=p.user_id AND m.organization_id=p.org_id
                     AND m.role='DRIVER' AND m.status='ACTIVE'
                    WHERE p.org_id=@owner AND p.driver_type='EXTERNAL' AND p.status='ACTIVE'
                      AND p.id=ANY(@drivers)
                    ORDER BY p.id
                    """;
                var authorized = new List<Guid>();
                await using (var command = new NpgsqlCommand(audienceSql, connection, transaction))
                {
                    command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                    command.Parameters.Add(new NpgsqlParameter<Guid[]>(
                        "drivers", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                    {
                        TypedValue = requestedAudienceDriverIds.ToArray(),
                    });
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) authorized.Add(reader.GetGuid(0));
                }
                return new ExternalOfferEvidence(
                    offer.OfferId,
                    offer.OwnerOrganizationId,
                    offer.Status,
                    offer.CommissionCents,
                    offer.ExpiresAt,
                    offer.Version,
                    offer.AcceptedByDriverId,
                    authorized);
            },
            cancellationToken);

    public Task<RouteEvidence?> ReadRouteAsync(
        Guid ownerOrganizationId,
        Guid routeId,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            ownerOrganizationId,
            async (connection, transaction, token) =>
            {
                const string routeSql =
                    """
                    SELECT id,operator_org_id,driver_id,version,updated_at
                    FROM routes.routes
                    WHERE id=@route AND operator_org_id=@owner
                    """;
                Guid driverId;
                int version;
                DateTimeOffset updatedAt;
                await using (var command = new NpgsqlCommand(routeSql, connection, transaction))
                {
                    command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
                    command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                    await using var reader = await command.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) return null;
                    driverId = reader.GetGuid(2);
                    version = reader.GetInt32(3);
                    updatedAt = reader.GetFieldValue<DateTimeOffset>(4);
                }

                const string stopsSql =
                    """
                    SELECT id
                    FROM routes.route_stops
                    WHERE route_id=@route AND operator_org_id=@owner
                    ORDER BY sequence,id
                    """;
                var stopIds = new List<Guid>();
                await using (var command = new NpgsqlCommand(stopsSql, connection, transaction))
                {
                    command.Parameters.Add(P("route", NpgsqlDbType.Uuid, routeId));
                    command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) stopIds.Add(reader.GetGuid(0));
                }

                return new RouteEvidence(
                    routeId,
                    ownerOrganizationId,
                    driverId,
                    version,
                    updatedAt,
                    stopIds);
            },
            cancellationToken);

    private static readonly string[] ActiveAssignmentStatuses = ["ACCEPTED", "ACTIVE"];
    private static readonly string[] ClosedAssignmentStatuses = ["COMPLETED", "CANCELLED"];

    /// <summary>
    /// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 ("Sí, el dueño lo ve"): an assignment of the owner's order
    /// made by its operator organization (distinct from the owner) carries the operator's driver, whose profile,
    /// user and membership the owner's tenant context cannot read. When the owner-scope read did not authorize
    /// the driver, the driver audience is decided in the operator's own tenant context, for the operator taken from
    /// the persisted assignment, and only for that exact assignment, order and driver. Nothing else changes: the
    /// operations audience stays the owner and no other driver is ever added.
    /// </summary>
    private async Task<AssignmentEvidence?> WithOperatorDriverAudienceAsync(
        AssignmentEvidence? evidence,
        IReadOnlyList<string> assignmentStatuses,
        CancellationToken cancellationToken)
    {
        if (evidence is null ||
            evidence.DriverAudienceAuthorized ||
            evidence.OperatorOrganizationId is not { } operatorId ||
            operatorId == evidence.OwnerOrganizationId)
        {
            return evidence;
        }

        return await IsOperatorDriverAuthorizedAsync(
                evidence.OwnerOrganizationId,
                operatorId,
                evidence.OrderId,
                evidence.AssignmentId,
                evidence.DriverId,
                assignmentStatuses,
                cancellationToken)
            ? evidence with { DriverAudienceAuthorized = true }
            : evidence;
    }

    private Task<bool> IsOperatorDriverAuthorizedAsync(
        Guid ownerOrganizationId,
        Guid operatorOrganizationId,
        Guid orderId,
        Guid assignmentId,
        Guid driverId,
        IReadOnlyList<string> assignmentStatuses,
        CancellationToken cancellationToken) =>
        ExecuteTenantReadAsync(
            operatorOrganizationId,
            async (connection, transaction, token) =>
            {
                const string sql =
                    """
                    SELECT EXISTS (
                      SELECT 1
                      FROM dispatch.assignments a
                      JOIN drivers.driver_profiles p
                        ON p.id=a.driver_id
                       AND p.org_id=a.operator_org_id
                       AND p.driver_type=a.assignment_type
                       AND p.status='ACTIVE'
                      JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
                      JOIN organizations.organization_memberships m
                        ON m.user_id=p.user_id
                       AND m.organization_id=p.org_id
                       AND m.role='DRIVER'
                       AND m.status='ACTIVE'
                      WHERE a.id=@assignment_id
                        AND a.order_id=@order_id
                        AND a.driver_id=@driver_id
                        AND a.owner_org_id=@owner
                        AND a.operator_org_id=@operator
                        AND a.operator_org_id<>a.owner_org_id
                        AND a.assignment_type IN ('OWN','EXTERNAL')
                        AND a.status=ANY(@statuses)
                    )
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(P("assignment_id", NpgsqlDbType.Uuid, assignmentId));
                command.Parameters.Add(P("order_id", NpgsqlDbType.Uuid, orderId));
                command.Parameters.Add(P("driver_id", NpgsqlDbType.Uuid, driverId));
                command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, ownerOrganizationId));
                command.Parameters.Add(P("operator", NpgsqlDbType.Uuid, operatorOrganizationId));
                command.Parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    TypedValue = assignmentStatuses.ToArray(),
                });
                return await command.ExecuteScalarAsync(token) is true;
            },
            cancellationToken);

    private async Task<T> ExecuteTenantReadAsync<T>(
        Guid ownerOrganizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        if (ownerOrganizationId == Guid.Empty)
        {
            throw new OutboxMessageException(RealtimeOutboxErrorCodes.InvalidAudienceEvidence);
        }

        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PostgreSqlRealtimeOutboxStore.SetWorkerRoleAsync(
            connection,
            transaction,
            cancellationToken);
        await using (var context = new NpgsqlCommand(
            """
            SELECT set_config('app.current_user_id','',true),
                   set_config('app.current_org_ids',@organization_ids::uuid[]::text,true);
            """,
            connection,
            transaction))
        {
            context.Parameters.Add(new NpgsqlParameter<Guid[]>(
                "organization_ids",
                NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = [ownerOrganizationId],
            });
            await context.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await read(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object value) =>
        new(name, type) { Value = value };
}
