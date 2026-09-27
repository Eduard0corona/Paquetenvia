using Npgsql;
using NpgsqlTypes;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

/// <summary>
/// Seeds the append-only <c>ORDER_STATUS_CHANGED</c> history a real order would carry before it
/// reached its current status. ORD-002, INC-001 and the driver stops view derive custody from the
/// <c>PICKED_UP</c> event and bound proofs and incidents to the current attempt by the event that
/// entered the current status, so a synthetic order parked directly in a status without history
/// is — correctly — treated as never picked up and without a current attempt.
/// </summary>
internal static class OrderStatusHistory
{
    /// <summary>The canonical happy path up to <c>DELIVERING</c>.</summary>
    internal static readonly string[] ToDelivering =
        ["CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING"];

    /// <summary>
    /// The shortest canonical path that ends in <paramref name="status"/>. A failed attempt is
    /// reached from <c>DELIVERING</c> so the parcel is in custody.
    /// </summary>
    internal static string[] CanonicalPathTo(string status) => status switch
    {
        "DRAFT" => [],
        "CONFIRMED" => ["CONFIRMED"],
        "READY_FOR_PICKUP" => ["CONFIRMED", "READY_FOR_PICKUP"],
        "ASSIGNED" => ["CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED"],
        "AT_PICKUP" => ["CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP"],
        "PICKED_UP" => ["CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP"],
        "IN_TRANSIT" => ["CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT"],
        "DELIVERING" => ToDelivering,
        "FAILED_ATTEMPT" => [.. ToDelivering, "FAILED_ATTEMPT"],
        "RESCHEDULED" => [.. ToDelivering, "FAILED_ATTEMPT", "RESCHEDULED"],
        "RETURNING" => [.. ToDelivering, "FAILED_ATTEMPT", "RETURNING"],
        "RETURNED" => [.. ToDelivering, "FAILED_ATTEMPT", "RETURNING", "RETURNED"],
        "DELIVERED" => [.. ToDelivering, "DELIVERED"],
        "CLOSED" => [.. ToDelivering, "DELIVERED", "CLOSED"],
        "CLAIM_OPEN" => [.. ToDelivering, "DELIVERED", "CLAIM_OPEN"],
        "CLAIM_RESOLVED" => [.. ToDelivering, "DELIVERED", "CLAIM_OPEN", "CLAIM_RESOLVED"],
        "CANCELLED" => ["CANCELLED"],
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown order status."),
    };

    /// <summary>
    /// Writes one event per status, versions 1..n, ending in the order's current status, and
    /// aligns <c>orders.version</c> with the last event. Returns the order version to expect.
    /// A <c>FAILED_ATTEMPT</c> entry names <paramref name="failedAttemptIncidentId"/> as ORD-002
    /// would. Events are one second apart and end one second before the database clock.
    /// </summary>
    internal static async Task<int> SeedAsync(
        SyntheticOrderScenario scenario,
        IReadOnlyList<string> statuses,
        Guid? failedAttemptIncidentId = null,
        Guid? orderId = null)
    {
        var order = orderId ?? scenario.OrderId;
        if (statuses.Count == 0)
        {
            return 1;
        }

        var previous = "DRAFT";
        for (var index = 0; index < statuses.Count; index++)
        {
            var status = statuses[index];
            var incident = status == "FAILED_ATTEMPT" ? failedAttemptIncidentId : null;
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO orders.order_events(
                  id,order_id,owner_org_id,aggregate_version,event_type,payload,actor_id,occurred_at)
                VALUES (
                  gen_random_uuid(),@order,@org,@version,'ORDER_STATUS_CHANGED',
                  jsonb_strip_nulls(jsonb_build_object(
                    'previous_status',@previous::text,'new_status',@new::text,'incident_id',@incident::text)),
                  @actor,clock_timestamp()-make_interval(secs => @age));
                """,
                SyntheticOrderScenario.P("order", order),
                SyntheticOrderScenario.P("org", scenario.OrganizationId),
                SyntheticOrderScenario.P("version", index + 1),
                SyntheticOrderScenario.P("previous", previous),
                SyntheticOrderScenario.P("new", status),
                new NpgsqlParameter("incident", NpgsqlDbType.Text)
                {
                    Value = (object?)incident?.ToString("D") ?? DBNull.Value,
                },
                SyntheticOrderScenario.P("actor", scenario.UserId),
                SyntheticOrderScenario.P("age", (double)(statuses.Count - index)));
            previous = status;
        }

        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status=@status,version=@version WHERE id=@order;",
            SyntheticOrderScenario.P("status", statuses[^1]),
            SyntheticOrderScenario.P("version", statuses.Count),
            SyntheticOrderScenario.P("order", order));
        return statuses.Count;
    }

    /// <summary>Seeds the canonical path to the scenario's status and returns the version.</summary>
    internal static Task<int> SeedCanonicalAsync(
        SyntheticOrderScenario scenario,
        string status,
        Guid? failedAttemptIncidentId = null) =>
        SeedAsync(scenario, CanonicalPathTo(status), failedAttemptIncidentId);
}
