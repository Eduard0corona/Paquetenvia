using System.Data.Common;
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

public sealed class PostgreSqlOrderAssignmentGuardReader : IOrderAssignmentGuardReader
{
    public async Task<AssignmentGuardSnapshot> ReadAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid organizationId,
        Guid orderId,
        Guid orderCityId,
        CancellationToken cancellationToken)
    {
        await using var command = TransitionReaderCommand.Create(
            connection,
            transaction,
            """
            SELECT
              count(*)=1,
              COALESCE(bool_and(d.status='ACTIVE' AND d.home_city_id=@city),false),
              COALESCE(bool_and(a.status IN ('ACCEPTED','ACTIVE')),false),
              COALESCE(bool_and(a.cost_cents>=0),false)
            FROM dispatch.assignments a
            JOIN drivers.driver_profiles d ON d.id=a.driver_id
            WHERE a.order_id=@order
              AND a.status IN ('ACCEPTED','ACTIVE')
              AND (a.owner_org_id=@org OR a.operator_org_id=@org)
              AND d.org_id=@org
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(TransitionReaderCommand.P("city", NpgsqlDbType.Uuid, orderCityId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3))
            : new(false, false, false, false);
    }
}

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
            SELECT
              COALESCE(bool_or(proof_type='PICKUP_PHOTO' AND octet_length(sha256)>0),false),
              COALESCE(bool_or(proof_type IN ('DELIVERY_PHOTO','DELIVERY_CODE') AND octet_length(sha256)>0),false)
            FROM custody.proofs
            WHERE order_id=@order AND (owner_org_id=@org OR operator_org_id=@org)
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetBoolean(0), reader.GetBoolean(1))
            : new(false, false);
    }
}

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
            SELECT
              COALESCE(bool_or(id=@incident AND status IN ('OPEN','INVESTIGATING')),false),
              COALESCE(bool_or(id=@incident AND status IN ('OPEN','INVESTIGATING') AND custody_acquired),false),
              COALESCE(bool_or(custody_acquired),false),
              COALESCE(bool_or(status IN ('OPEN','INVESTIGATING')),false)
            FROM incidents.incidents
            WHERE order_id=@order AND (owner_org_id=@org OR operator_org_id=@org)
            """);
        command.Parameters.Add(TransitionReaderCommand.P("org", NpgsqlDbType.Uuid, organizationId));
        command.Parameters.Add(TransitionReaderCommand.P("order", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(TransitionReaderCommand.P("incident", NpgsqlDbType.Uuid, requestedIncidentId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3))
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
