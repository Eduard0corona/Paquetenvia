using Orders.Domain;

namespace Orders.Application.Orders;

/// <summary>
/// D8-OUTBOX-LANE-DISPATCH: the committed ORD-002 transitions for which Orders writes, in the same
/// transaction, a <c>dispatch.order-status-reaction-requested</c> outbox row so Dispatch can close the
/// assignment that was active at that moment (D8, eventual consistency; no new AI-13 §4 flow).
/// </summary>
/// <remarks>
/// Orders only decides whether to ask; Dispatch owns the closure status. A unit test keeps this set
/// equal to the transitions for which the Dispatch <c>AssignmentLifecyclePolicy</c> closes.
/// </remarks>
public static class DispatchReactionRequestPolicy
{
    public const string Topic = "dispatch.order-status-reaction-requested";
    public const string SchemaVersion = "order-status-reaction-v1";

    public static bool Requires(OrderStatus source, OrderStatus target) =>
        target is OrderStatus.Cancelled or OrderStatus.Delivered or OrderStatus.Returned ||
        (source, target) is
            (OrderStatus.Assigned, OrderStatus.ReadyForPickup) or
            (OrderStatus.FailedAttempt, OrderStatus.Rescheduled) or
            (OrderStatus.Rescheduled, OrderStatus.ReadyForPickup);
}
