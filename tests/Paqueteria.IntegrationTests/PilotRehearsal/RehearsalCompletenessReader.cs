using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// Z6 entry half, AI-24 <c>pilot_readiness.data_completeness</c>: AI-02 <c>orders_with_complete_events_and_pod</c> over
/// the rehearsal's orders that reached DELIVERED. An order is complete when
/// <list type="bullet">
/// <item>its <c>order_events</c> versions are exactly 1 to the order's version, with no gap or repeat;</item>
/// <item>each of its status changes (<c>ORDER_STATUS_CHANGED</c> events) has exactly one
/// <c>ORDER_STATUS_CHANGED</c> audit row on the order;</item>
/// <item>it has a finalized <c>PICKUP_PHOTO</c> and a finalized <c>DELIVERY_PHOTO</c> proof.</item>
/// </list>
/// Read through the test's administration connection after the run; counts only. Each missing count is the number of
/// delivered orders that miss that kind, so an order may count in more than one.
/// </summary>
internal static class RehearsalCompletenessReader
{
    internal static async Task<RehearsalCompleteness> ReadAsync(string adminConnectionString, IReadOnlyCollection<Guid> orderIds)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH delivered AS (
              SELECT o.id, o.version
              FROM orders.orders o
              WHERE o.id = ANY(@orders)
                AND EXISTS (
                  SELECT 1 FROM orders.order_events e
                  WHERE e.order_id = o.id AND e.event_type = 'ORDER_STATUS_CHANGED'
                    AND e.payload->>'new_status' = 'DELIVERED')
            ),
            checked AS (
              SELECT
                d.id,
                (SELECT count(*) = d.version AND count(DISTINCT e.aggregate_version) = d.version
                        AND min(e.aggregate_version) = 1 AND max(e.aggregate_version) = d.version
                 FROM orders.order_events e WHERE e.order_id = d.id) AS contiguous,
                (SELECT count(*) FROM orders.order_events e
                 WHERE e.order_id = d.id AND e.event_type = 'ORDER_STATUS_CHANGED')
                  = (SELECT count(*) FROM platform.audit_logs a
                     WHERE a.entity_id = d.id AND a.entity_type = 'Order' AND a.action = 'ORDER_STATUS_CHANGED')
                  AS audited,
                EXISTS (SELECT 1 FROM custody.proofs p WHERE p.order_id = d.id AND p.proof_type = 'PICKUP_PHOTO')
                  AS pickup_photo,
                EXISTS (SELECT 1 FROM custody.proofs p WHERE p.order_id = d.id AND p.proof_type = 'DELIVERY_PHOTO')
                  AS delivery_photo
              FROM delivered d
            )
            SELECT
              count(*)::integer,
              count(*) FILTER (WHERE contiguous AND audited AND pickup_photo AND delivery_photo)::integer,
              count(*) FILTER (WHERE NOT contiguous)::integer,
              count(*) FILTER (WHERE NOT audited)::integer,
              count(*) FILTER (WHERE NOT pickup_photo)::integer,
              count(*) FILTER (WHERE NOT delivery_photo)::integer
            FROM checked
            """,
            connection)
        {
            CommandTimeout = 60,
        };
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("orders", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = orderIds.ToArray(),
        });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RehearsalCompleteness(
            reader.GetInt32(0),
            reader.GetInt32(1),
            new RehearsalMissing(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));
    }
}
