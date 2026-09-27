using System.Data.Common;
using Drivers.Application.Eligibility;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;

namespace Orders.Infrastructure.Orders;

public sealed class PostgreSqlOrderTransitionAuthorizationReader : IOrderTransitionAuthorizationReader
{
    public async Task<OrderTransitionAuthorizationSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT
              (
                SELECT m.role
                FROM organizations.organization_memberships m
                WHERE m.user_id=@actor AND m.organization_id=@org AND m.status='ACTIVE'
                ORDER BY CASE m.role
                  WHEN 'PLATFORM_ADMIN' THEN 0
                  WHEN 'DISPATCHER' THEN 1
                  WHEN 'DRIVER' THEN 2
                  ELSE 3 END, m.role
                LIMIT 1
              ),
              EXISTS (
                SELECT 1
                FROM dispatch.assignments a
                JOIN drivers.driver_profiles d ON d.id=a.driver_id
                WHERE a.order_id=@order
                  AND a.status IN ('ACCEPTED','ACTIVE')
                  AND d.user_id=@actor
                  AND d.org_id=@org
                  AND d.status='ACTIVE'
                  AND (a.owner_org_id=@org OR a.operator_org_id=@org)
              )
            """);
        command.Parameters.Add(TransitionReaderCommand.P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(null, false);
        }

        return new(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetBoolean(1));
    }
}

public sealed class PostgreSqlOrderTransitionReplayAuthorizationReader
    : IOrderTransitionReplayAuthorizationReader
{
    public async Task<OrderTransitionReplayAuthorizationSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        int aggregateVersion,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            WITH matching_events AS (
              SELECT aggregate_version,payload->>'previous_status' AS previous_status,
                     payload->>'new_status' AS new_status
              FROM orders.order_events
              WHERE order_id=@order
                AND aggregate_version=@version
                AND event_type='ORDER_STATUS_CHANGED'
                AND owner_org_id=@org
            ),
            event_evidence AS (
              SELECT count(*)::integer AS matching_event_count,
                     min(aggregate_version) AS aggregate_version,
                     min(previous_status) AS previous_status,
                     min(new_status) AS new_status
              FROM matching_events
            )
            SELECT e.matching_event_count,e.aggregate_version,e.previous_status,e.new_status,
              (
                SELECT m.role
                FROM organizations.organization_memberships m
                WHERE m.user_id=@actor AND m.organization_id=@org AND m.status='ACTIVE'
                ORDER BY CASE m.role
                  WHEN 'PLATFORM_ADMIN' THEN 0
                  WHEN 'DISPATCHER' THEN 1
                  WHEN 'DRIVER' THEN 2
                  ELSE 3 END, m.role
                LIMIT 1
              ),
              EXISTS (
                SELECT 1
                FROM dispatch.assignments a
                JOIN drivers.driver_profiles d ON d.id=a.driver_id
                WHERE a.order_id=@order
                  AND a.status IN ('ACCEPTED','ACTIVE')
                  AND d.user_id=@actor
                  AND d.org_id=@org
                  AND d.status='ACTIVE'
                  AND (a.owner_org_id=@org OR a.operator_org_id=@org)
              )
            FROM event_evidence e
            """);
        command.Parameters.Add(TransitionReaderCommand.P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(TransitionReaderCommand.P("version", NpgsqlDbType.Integer, aggregateVersion));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(0, null, null, null, null, false);
        }

        return new(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5));
    }
}

public sealed class PostgreSqlOrderQuoteAcceptanceGuardReader : IOrderQuoteAcceptanceGuardReader
{
    public async Task<QuoteAcceptanceGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT
              q.id=o.quote_id
                AND q.owner_org_id=o.owner_org_id
                AND q.status='USED'
                AND q.consumed_at IS NOT NULL
                AND q.city_id=o.city_id
                AND q.service_area_id IS NOT DISTINCT FROM o.service_area_id
                AND q.origin_location_id=o.origin_location_id
                AND q.destination_location_id=o.destination_location_id
                AND q.service_type=o.service_type
                AND q.pricing_tier=o.pricing_tier
                AND q.consolidated_route=o.consolidated_route
                AND q.subtotal_cents=o.subtotal_cents
                AND q.discount_cents=o.discount_cents
                AND q.tax_cents=o.tax_cents
                AND q.total_cents=o.total_cents
                AND q.minimum_total_cents_snapshot=o.minimum_total_cents_snapshot
                AND q.currency=o.currency
                AND q.pricing_policy_version=o.pricing_policy_version
                AND q.package_snapshot=o.package_snapshot,
              a.order_id=o.id
                AND a.quote_id=o.quote_id
                AND a.owner_org_id=o.owner_org_id
                AND octet_length(a.evidence_hash)=32
            FROM orders.orders o
            JOIN pricing.quotes q ON q.id=o.quote_id
            JOIN orders.order_acceptances a ON a.order_id=o.id
            WHERE o.id=@order AND o.owner_org_id=@org
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetBoolean(0), reader.GetBoolean(1))
            : new(false, false);
    }
}

/// <summary>
/// Reads the single current assignment and evaluates its driver with the DSP-002
/// <see cref="DriverEligibilityPolicy"/> over the same rows DSP-002 reads: profile, user,
/// membership, service area, latest documents and the order's packages.
/// </summary>
public sealed class PostgreSqlOrderAssignmentGuardReader(
    IOptions<OrderTransitionDriverEligibilityOptions> eligibilityOptions) : IOrderAssignmentGuardReader
{
    public async Task<AssignmentGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        Guid orderCityId,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var assignments = new List<(Guid DriverId, string AssignmentType, long CostCents, Guid? ServiceAreaId)>();
        await using (var command = TransitionReaderCommand.Create(
                         connection,
                         transaction,
                         """
                         SELECT a.driver_id,a.assignment_type,a.cost_cents,o.service_area_id
                         FROM dispatch.assignments a
                         JOIN drivers.driver_profiles d ON d.id=a.driver_id
                         JOIN orders.orders o ON o.id=a.order_id
                         WHERE a.order_id=@order
                           AND a.status IN ('ACCEPTED','ACTIVE')
                           AND (a.owner_org_id=@org OR a.operator_org_id=@org)
                           AND d.org_id=@org
                         """))
        {
            command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
            command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                assignments.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3)));
            }
        }

        if (assignments.Count != 1)
        {
            return new(false, false, false, false);
        }

        var assignment = assignments[0];
        var snapshot = await ReadDriverSnapshotAsync(
            connection,
            transaction,
            organizationId,
            assignment.DriverId,
            orderCityId,
            assignment.ServiceAreaId,
            cancellationToken);
        var capacity = OrderTransitionEligibility.Aggregate(
            await ReadPackagesAsync(connection, transaction, orderId, cancellationToken));
        var (eligibleDriver, capacityAvailable) = OrderTransitionEligibility.Evaluate(
            assignment.AssignmentType,
            organizationId,
            assignment.DriverId,
            orderCityId,
            assignment.ServiceAreaId,
            capacity,
            evaluatedAt,
            snapshot,
            eligibilityOptions.Value.ToPolicy());
        return new(true, eligibleDriver, capacityAvailable, assignment.CostCents >= 0);
    }

    private static async Task<DriverEligibilitySnapshot?> ReadDriverSnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid driverId,
        Guid cityId,
        Guid? serviceAreaId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT p.id,p.org_id,p.user_id,p.home_city_id,p.driver_type,p.vehicle_type,p.status,
                   u.status,
                   EXISTS (
                     SELECT 1
                     FROM organizations.organization_memberships m
                     WHERE m.user_id=p.user_id AND m.organization_id=p.org_id
                       AND m.role='DRIVER' AND m.status='ACTIVE'
                   ),
                   CASE WHEN @service_area IS NULL THEN NULL ELSE EXISTS (
                     SELECT 1
                     FROM drivers.driver_service_areas dsa
                     JOIN locations.service_areas sa ON sa.id=dsa.service_area_id
                     WHERE dsa.driver_id=p.id AND dsa.service_area_id=@service_area
                       AND dsa.org_id=p.org_id AND dsa.status='ACTIVE'
                       AND sa.owner_org_id=p.org_id AND sa.city_id=@city AND sa.status='ACTIVE'
                   ) END
            FROM drivers.driver_profiles p
            LEFT JOIN identity.users u ON u.id=p.user_id
            WHERE p.id=@driver AND p.org_id=@org;

            SELECT DISTINCT ON (document_type)
                   document_type,status,object_key,sha256,expires_at
            FROM drivers.driver_documents
            WHERE driver_id=@driver AND org_id=@org
            ORDER BY document_type,created_at DESC,id DESC
            """);
        command.Parameters.Add(TransitionReaderCommand.P("driver", NpgsqlDbType.Uuid, driverId));
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("service_area", NpgsqlDbType.Uuid, serviceAreaId));
        command.Parameters.Add(TransitionReaderCommand.P("city", NpgsqlDbType.Uuid, cityId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        DriverEligibilitySnapshot? profile = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            profile = new DriverEligibilitySnapshot(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetBoolean(9),
                new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal));
        }

        if (!await reader.NextResultAsync(cancellationToken))
        {
            throw new OrderTransitionInfrastructureException(
                "The driver document result is missing.");
        }

        var documents = new Dictionary<string, DriverDocumentSnapshot>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(0);
            documents[type] = new DriverDocumentSnapshot(
                type,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
        }

        return profile is null ? null : profile with { LatestDocuments = documents };
    }

    private static async Task<IReadOnlyList<(long WeightGrams, string DimensionsJson)>> ReadPackagesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT weight_grams,dimensions_mm::text
            FROM orders.package_items
            WHERE order_id=@order
            ORDER BY id
            """);
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        var packages = new List<(long WeightGrams, string DimensionsJson)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            packages.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return packages;
    }
}

/// <summary>
/// Proofs count only for the attempt in progress. The attempt starts at the latest status change
/// into <c>AT_PICKUP</c> (pickup) or <c>DELIVERING</c> (delivery); without that event there is no
/// attempt and nothing counts. A proof already named as incident evidence documents a failure and
/// never completes a pickup or a delivery.
/// </summary>
public sealed class PostgreSqlOrderProofGuardReader : IOrderProofGuardReader
{
    public async Task<ProofGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            WITH attempt AS (
              SELECT
                (SELECT e.occurred_at FROM orders.order_events e
                  WHERE e.order_id=@order AND (e.owner_org_id=@org OR e.operator_org_id=@org)
                    AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'='AT_PICKUP'
                  ORDER BY e.aggregate_version DESC LIMIT 1) AS pickup_started_at,
                (SELECT e.occurred_at FROM orders.order_events e
                  WHERE e.order_id=@order AND (e.owner_org_id=@org OR e.operator_org_id=@org)
                    AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'='DELIVERING'
                  ORDER BY e.aggregate_version DESC LIMIT 1) AS delivery_started_at
            )
            SELECT
              COALESCE(bool_or(p.proof_type='PICKUP_PHOTO'
                AND p.created_at>=attempt.pickup_started_at),false),
              COALESCE(bool_or(p.proof_type IN ('DELIVERY_PHOTO','DELIVERY_CODE')
                AND p.created_at>=attempt.delivery_started_at),false)
            FROM attempt
            LEFT JOIN custody.proofs p
              ON p.order_id=@order
             AND (p.owner_org_id=@org OR p.operator_org_id=@org)
             AND octet_length(p.sha256)>0
             AND NOT EXISTS (
               SELECT 1 FROM incidents.incident_evidence ev
               WHERE ev.proof_id=p.id AND ev.order_id=p.order_id)
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetBoolean(0), reader.GetBoolean(1))
            : new(false, false);
    }
}

/// <summary>
/// The single custody derivation: the order history holds a <c>PICKED_UP</c> status change.
/// INC-001 and the driver stops view evaluate the same predicate over the same rows.
/// </summary>
public sealed class PostgreSqlOrderCustodyGuardReader : IOrderCustodyGuardReader
{
    public async Task<CustodyGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT EXISTS (
              SELECT 1
              FROM orders.orders o
              JOIN orders.order_events e ON e.order_id=o.id AND e.owner_org_id=o.owner_org_id
              WHERE o.id=@order
                AND (o.owner_org_id=@org OR o.operator_org_id=@org)
                AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'='PICKED_UP'
            )
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        return new(await command.ExecuteScalarAsync(cancellationToken) is true);
    }
}

/// <summary>
/// An incident justifies one failed attempt: the requested incident must be pending, opened after
/// the latest status change into the order's current status, and not already named by an earlier
/// <c>FAILED_ATTEMPT</c>. Also reads the next action of the incident behind the latest
/// <c>FAILED_ATTEMPT</c>, which decides the only successors the order may take, unless INC-001
/// adopted that incident from a pre-INC-001 installation. Adopted incidents are recognised without
/// a new AI-06 column: INC-001 creates <c>incidents.incident_evidence</c> in the adoption itself and
/// a deferred constraint trigger refuses every later incident without evidence, while nothing adds
/// evidence to an existing incident. An incident without evidence is therefore exactly one that
/// existed before the adoption, whose next action the backfill derived from
/// <c>custody_acquired</c>.
/// </summary>
public sealed class PostgreSqlOrderIncidentGuardReader : IOrderIncidentGuardReader
{
    public async Task<IncidentGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        Guid? requestedIncidentId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            WITH current_attempt AS (
              SELECT (
                SELECT e.occurred_at
                FROM orders.orders o
                JOIN orders.order_events e ON e.order_id=o.id AND e.owner_org_id=o.owner_org_id
                WHERE o.id=@order AND (o.owner_org_id=@org OR o.operator_org_id=@org)
                  AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'=o.status
                ORDER BY e.aggregate_version DESC LIMIT 1) AS started_at
            ),
            failed_attempts AS (
              SELECT e.aggregate_version,e.payload->>'incident_id' AS incident_id
              FROM orders.order_events e
              WHERE e.order_id=@order AND (e.owner_org_id=@org OR e.operator_org_id=@org)
                AND e.event_type='ORDER_STATUS_CHANGED' AND e.payload->>'new_status'='FAILED_ATTEMPT'
            ),
            requested AS (
              SELECT i.id,i.custody_acquired
              FROM incidents.incidents i
              CROSS JOIN current_attempt a
              WHERE i.id=@incident
                AND i.order_id=@order
                AND (i.owner_org_id=@org OR i.operator_org_id=@org)
                AND i.status IN ('OPEN','INVESTIGATING')
                AND i.created_at>=a.started_at
                AND NOT EXISTS (SELECT 1 FROM failed_attempts f WHERE f.incident_id=i.id::text)
            )
            SELECT
              EXISTS (SELECT 1 FROM requested),
              EXISTS (SELECT 1 FROM requested WHERE custody_acquired),
              EXISTS (
                SELECT 1 FROM incidents.incidents i
                WHERE i.order_id=@order AND (i.owner_org_id=@org OR i.operator_org_id=@org)
                  AND i.custody_acquired),
              EXISTS (
                SELECT 1 FROM incidents.incidents i
                WHERE i.order_id=@order AND (i.owner_org_id=@org OR i.operator_org_id=@org)
                  AND i.status IN ('OPEN','INVESTIGATING')),
              latest_incident.next_action,
              COALESCE(latest_incident.adopted,false)
            FROM (SELECT 1) anchor
            LEFT JOIN LATERAL (
              SELECT i.next_action,
                     NOT EXISTS (
                       SELECT 1 FROM incidents.incident_evidence ev WHERE ev.incident_id=i.id
                     ) AS adopted
              FROM incidents.incidents i
              JOIN (
                SELECT f.incident_id FROM failed_attempts f
                ORDER BY f.aggregate_version DESC LIMIT 1
              ) latest ON latest.incident_id=i.id::text
              WHERE i.order_id=@order AND (i.owner_org_id=@org OR i.operator_org_id=@org)
            ) latest_incident ON true
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(TransitionReaderCommand.P("incident", NpgsqlDbType.Uuid, requestedIncidentId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(
                reader.GetBoolean(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetBoolean(5))
            : new(false, false, false, false);
    }
}

public sealed class PostgreSqlOrderCodGuardReader : IOrderCodGuardReader
{
    public async Task<CodGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT status,amount_cents
            FROM finance.cod_transactions
            WHERE order_id=@order AND (owner_org_id=@org OR operator_org_id=@org)
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(true, reader.GetString(0), reader.GetInt64(1))
            : new(false, null, null);
    }
}

internal static class TransitionReaderCommand
{
    internal static NpgsqlCommand Create(
        DbConnection connection,
        DbTransaction transaction,
        string sql) =>
        new(sql, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);

    internal static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };
}
