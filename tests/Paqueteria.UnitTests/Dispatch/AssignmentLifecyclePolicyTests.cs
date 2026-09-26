using Dispatch.Application.Assignments;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class AssignmentLifecyclePolicyTests
{
    [Theory]
    [InlineData("ASSIGNED", "READY_FOR_PICKUP", AssignmentClosure.Cancelled)]
    [InlineData("RESCHEDULED", "READY_FOR_PICKUP", AssignmentClosure.Cancelled)]
    [InlineData("DRAFT", "CANCELLED", AssignmentClosure.Cancelled)]
    [InlineData("ASSIGNED", "CANCELLED", AssignmentClosure.Cancelled)]
    [InlineData("AT_PICKUP", "CANCELLED", AssignmentClosure.Cancelled)]
    [InlineData("DELIVERING", "DELIVERED", AssignmentClosure.Completed)]
    [InlineData("RETURNING", "RETURNED", AssignmentClosure.Completed)]
    [InlineData("CONFIRMED", "READY_FOR_PICKUP", AssignmentClosure.None)]
    [InlineData("READY_FOR_PICKUP", "ASSIGNED", AssignmentClosure.None)]
    [InlineData("ASSIGNED", "AT_PICKUP", AssignmentClosure.None)]
    [InlineData("FAILED_ATTEMPT", "RESCHEDULED", AssignmentClosure.None)]
    [InlineData("RESCHEDULED", "ASSIGNED", AssignmentClosure.None)]
    [InlineData("DELIVERED", "CLOSED", AssignmentClosure.None)]
    public void Maps_committed_order_transitions_to_assignment_closure(
        string previous,
        string next,
        AssignmentClosure expected) =>
        Assert.Equal(expected, AssignmentLifecyclePolicy.Resolve(previous, next));

    [Fact]
    public void Parses_the_order_status_changed_v1_outbox_payload()
    {
        var eventId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        var payload = $$"""
            {"schema_version":"order-status-changed-v1","order_event_id":"{{Guid.NewGuid()}}",
             "order_id":"{{order}}","public_order_id":"ORD_x","previous_status":"ASSIGNED",
             "new_status":"READY_FOR_PICKUP","occurred_at":"2026-09-01T10:00:00+00:00",
             "public_event_code":null,"authorized_driver_id":"{{Guid.NewGuid()}}",
             "assignment_id":"{{assignment}}"}
            """;

        Assert.True(OrderStatusChangedFact.TryParse(eventId, owner, order, payload, out var fact));
        Assert.Equal(
            new OrderStatusChangedFact(
                eventId,
                owner,
                order,
                "ASSIGNED",
                "READY_FOR_PICKUP",
                new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
                assignment),
            fact);
    }

    [Theory]
    [InlineData("""{"schema_version":"order-status-changed-v2","order_id":"ORDER","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z"}""")]
    [InlineData("""{"schema_version":"order-status-changed-v1","order_id":"11111111-1111-1111-1111-111111111111","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z"}""")]
    [InlineData("""{"schema_version":"order-status-changed-v1","order_id":"ORDER","new_status":"B","occurred_at":"2026-09-01T10:00:00Z"}""")]
    [InlineData("""{"schema_version":"order-status-changed-v1","order_id":"ORDER","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":42}""")]
    [InlineData("not-json")]
    public void Rejects_foreign_or_malformed_payloads(string template)
    {
        var order = Guid.NewGuid();
        var payload = template.Replace("ORDER", order.ToString(), StringComparison.Ordinal);
        Assert.False(OrderStatusChangedFact.TryParse(Guid.NewGuid(), Guid.NewGuid(), order, payload, out var fact));
        Assert.Null(fact);
    }
}
