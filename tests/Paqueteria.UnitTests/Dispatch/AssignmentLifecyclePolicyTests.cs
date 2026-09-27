using Dispatch.Application.Assignments;
using Orders.Application.Orders;
using Orders.Domain;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class AssignmentLifecyclePolicyTests
{
    /// <summary>
    /// Every AI-04 <c>order_state_machine.transitions</c> edge (29 since D8-RESCHEDULED-NO-DIRECT-DELIVERY) with its D8 closure. A contract test
    /// (<c>AssignmentLifecycleAi04CoverageTests</c>) proves this table equals AI-04 exactly.
    /// </summary>
    public static TheoryData<string, string, AssignmentClosure> Ai04Transitions => new()
    {
        { "DRAFT", "CONFIRMED", AssignmentClosure.None },
        { "DRAFT", "CANCELLED", AssignmentClosure.Cancelled },
        { "CONFIRMED", "READY_FOR_PICKUP", AssignmentClosure.None },
        { "CONFIRMED", "CANCELLED", AssignmentClosure.Cancelled },
        { "READY_FOR_PICKUP", "ASSIGNED", AssignmentClosure.None },
        { "READY_FOR_PICKUP", "CANCELLED", AssignmentClosure.Cancelled },
        { "ASSIGNED", "AT_PICKUP", AssignmentClosure.None },
        { "ASSIGNED", "READY_FOR_PICKUP", AssignmentClosure.Cancelled },
        { "ASSIGNED", "CANCELLED", AssignmentClosure.Cancelled },
        { "AT_PICKUP", "PICKED_UP", AssignmentClosure.None },
        { "AT_PICKUP", "FAILED_ATTEMPT", AssignmentClosure.None },
        { "AT_PICKUP", "CANCELLED", AssignmentClosure.Cancelled },
        { "PICKED_UP", "IN_TRANSIT", AssignmentClosure.None },
        { "PICKED_UP", "RETURNING", AssignmentClosure.None },
        { "IN_TRANSIT", "DELIVERING", AssignmentClosure.None },
        { "IN_TRANSIT", "FAILED_ATTEMPT", AssignmentClosure.None },
        { "IN_TRANSIT", "RETURNING", AssignmentClosure.None },
        { "DELIVERING", "DELIVERED", AssignmentClosure.Completed },
        { "DELIVERING", "FAILED_ATTEMPT", AssignmentClosure.None },
        // D8-REASSIGNMENT-NEW-ASSIGNMENT: a reschedule closes the previous assignment.
        { "FAILED_ATTEMPT", "RESCHEDULED", AssignmentClosure.Cancelled },
        { "FAILED_ATTEMPT", "RETURNING", AssignmentClosure.None },
        // A retry is not a reschedule: the active assignment is kept.
        { "FAILED_ATTEMPT", "DELIVERING", AssignmentClosure.None },
        { "RESCHEDULED", "READY_FOR_PICKUP", AssignmentClosure.Cancelled },
        // Reassignment after a reschedule always creates a new assignment; nothing to close.
        { "RESCHEDULED", "ASSIGNED", AssignmentClosure.None },
        { "RETURNING", "RETURNED", AssignmentClosure.Completed },
        { "DELIVERED", "CLOSED", AssignmentClosure.None },
        { "DELIVERED", "CLAIM_OPEN", AssignmentClosure.None },
        { "CLOSED", "CLAIM_OPEN", AssignmentClosure.None },
        { "CLAIM_OPEN", "CLAIM_RESOLVED", AssignmentClosure.None },
    };

    [Theory]
    [MemberData(nameof(Ai04Transitions))]
    public void Maps_every_ai04_transition_to_its_assignment_closure(
        string previous,
        string next,
        AssignmentClosure expected) =>
        Assert.Equal(expected, AssignmentLifecyclePolicy.Resolve(previous, next));

    [Fact]
    public void Policy_map_has_exactly_the_ai04_transitions_listed_here()
    {
        var listed = Ai04Transitions.Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        Assert.Equal(29, listed.Count);
        Assert.True(listed.SetEquals(AssignmentLifecyclePolicy.Transitions));
    }

    [Theory]
    [InlineData("DELIVERED", "RESCHEDULED")]
    [InlineData("RESCHEDULED", "DELIVERING")]
    [InlineData("UNKNOWN", "CANCELLED")]
    [InlineData("ASSIGNED", "UNKNOWN")]
    public void Unknown_transitions_never_close(string previous, string next) =>
        Assert.Equal(AssignmentClosure.None, AssignmentLifecyclePolicy.Resolve(previous, next));

    [Theory]
    [MemberData(nameof(Ai04Transitions))]
    public void Orders_requests_a_dispatch_reaction_exactly_when_dispatch_closes(
        string previous,
        string next,
        AssignmentClosure expected)
    {
        Assert.True(OrderContractValues.TryParseOrderStatus(previous, out var source));
        Assert.True(OrderContractValues.TryParseOrderStatus(next, out var target));
        Assert.Equal(expected != AssignmentClosure.None, DispatchReactionRequestPolicy.Requires(source, target));
    }

    [Fact]
    public void Topic_and_schema_are_shared_by_producer_and_consumer()
    {
        Assert.Equal(DispatchReactionRequestPolicy.Topic, OrderStatusChangedFact.Topic);
        Assert.Equal(DispatchReactionRequestPolicy.SchemaVersion, OrderStatusChangedFact.SchemaVersion);
        Assert.Equal("dispatch.order-status-reaction-requested", OrderStatusChangedFact.Topic);
        Assert.Equal("order-status-reaction-v1", OrderStatusChangedFact.SchemaVersion);
    }

    [Fact]
    public void Parses_the_order_status_reaction_v1_outbox_payload()
    {
        var eventId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var orderEvent = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        var payload = $$"""
            {"schema_version":"order-status-reaction-v1","order_event_id":"{{orderEvent}}",
             "order_id":"{{order}}","previous_status":"ASSIGNED","new_status":"READY_FOR_PICKUP",
             "occurred_at":"2026-09-01T10:00:00+00:00","assignment_id":"{{assignment}}"}
            """;

        Assert.True(OrderStatusChangedFact.TryParse(eventId, owner, order, 7, payload, out var fact));
        Assert.Equal(
            new OrderStatusChangedFact(
                eventId,
                owner,
                order,
                7,
                orderEvent,
                "ASSIGNED",
                "READY_FOR_PICKUP",
                new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
                assignment),
            fact);
    }

    [Theory]
    [InlineData("""{"schema_version":"order-status-changed-v1","order_event_id":"EVENT","order_id":"ORDER","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":"ASSIGNMENT"}""")]
    [InlineData("""{"schema_version":"order-status-reaction-v1","order_event_id":"EVENT","order_id":"11111111-1111-1111-1111-111111111111","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":"ASSIGNMENT"}""")]
    [InlineData("""{"schema_version":"order-status-reaction-v1","order_event_id":"EVENT","order_id":"ORDER","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":"ASSIGNMENT"}""")]
    [InlineData("""{"schema_version":"order-status-reaction-v1","order_event_id":"EVENT","order_id":"ORDER","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":42}""")]
    [InlineData("""{"schema_version":"order-status-reaction-v1","order_event_id":"EVENT","order_id":"ORDER","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":null}""")]
    [InlineData("""{"schema_version":"order-status-reaction-v1","order_event_id":"EVENT","order_id":"ORDER","previous_status":"A","new_status":"B","occurred_at":"2026-09-01T10:00:00Z","assignment_id":"ASSIGNMENT","public_order_id":"ORD_x"}""")]
    [InlineData("not-json")]
    public void Rejects_foreign_or_malformed_payloads(string template)
    {
        var order = Guid.NewGuid();
        var payload = template
            .Replace("ORDER", order.ToString(), StringComparison.Ordinal)
            .Replace("EVENT", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("ASSIGNMENT", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        Assert.False(OrderStatusChangedFact.TryParse(Guid.NewGuid(), Guid.NewGuid(), order, 3, payload, out var fact));
        Assert.Null(fact);
    }

    [Fact]
    public void Rejects_a_row_without_a_positive_order_version()
    {
        var order = Guid.NewGuid();
        var payload = $$"""
            {"schema_version":"order-status-reaction-v1","order_event_id":"{{Guid.NewGuid()}}",
             "order_id":"{{order}}","previous_status":"A","new_status":"B",
             "occurred_at":"2026-09-01T10:00:00Z","assignment_id":"{{Guid.NewGuid()}}"}
            """;
        Assert.False(OrderStatusChangedFact.TryParse(Guid.NewGuid(), Guid.NewGuid(), order, null, payload, out _));
        Assert.False(OrderStatusChangedFact.TryParse(Guid.NewGuid(), Guid.NewGuid(), order, 0, payload, out _));
    }
}
