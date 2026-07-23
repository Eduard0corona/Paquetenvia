using System.Collections.Frozen;

namespace Orders.Domain;

public enum OrderTransitionRuleCode
{
    Allowed,
    NotAllowed,
    TerminalState,
    ClaimWindowExpired,
    Finalized,
    VersionMismatch,
    VersionOverflow,
}

public sealed record OrderTransitionEvaluation(bool Allowed, OrderTransitionRuleCode Code)
{
    public static OrderTransitionEvaluation Success { get; } =
        new(true, OrderTransitionRuleCode.Allowed);

    public static OrderTransitionEvaluation Rejected(OrderTransitionRuleCode code) =>
        new(false, code);
}

public static class OrderTransitionMatrix
{
    private static readonly FrozenDictionary<OrderStatus, FrozenSet<OrderStatus>> Edges =
        new Dictionary<OrderStatus, OrderStatus[]>
        {
            [OrderStatus.Draft] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
            [OrderStatus.Confirmed] = [OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
            [OrderStatus.ReadyForPickup] = [OrderStatus.Assigned, OrderStatus.Cancelled],
            [OrderStatus.Assigned] = [OrderStatus.AtPickup, OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
            [OrderStatus.AtPickup] = [OrderStatus.PickedUp, OrderStatus.FailedAttempt, OrderStatus.Cancelled],
            [OrderStatus.PickedUp] = [OrderStatus.InTransit, OrderStatus.Returning],
            [OrderStatus.InTransit] = [OrderStatus.Delivering, OrderStatus.FailedAttempt, OrderStatus.Returning],
            [OrderStatus.Delivering] = [OrderStatus.Delivered, OrderStatus.FailedAttempt],
            [OrderStatus.FailedAttempt] = [OrderStatus.Rescheduled, OrderStatus.Returning, OrderStatus.Delivering],
            [OrderStatus.Rescheduled] = [OrderStatus.ReadyForPickup, OrderStatus.Assigned, OrderStatus.Delivering],
            [OrderStatus.Returning] = [OrderStatus.Returned],
            [OrderStatus.Delivered] = [OrderStatus.Closed, OrderStatus.ClaimOpen],
            [OrderStatus.Closed] = [OrderStatus.ClaimOpen],
            [OrderStatus.ClaimOpen] = [OrderStatus.ClaimResolved],
        }.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet());

    public static IReadOnlyDictionary<OrderStatus, FrozenSet<OrderStatus>> AllowedTransitions => Edges;

    public static FrozenSet<OrderStatus> ImmediateTerminalStates { get; } =
        new[] { OrderStatus.Returned, OrderStatus.ClaimResolved, OrderStatus.Cancelled }.ToFrozenSet();

    public static OrderTransitionEvaluation Evaluate(
        OrderStatus source,
        OrderStatus target,
        DateTimeOffset occurredAt,
        DateTimeOffset? claimWindowEndsAt,
        DateTimeOffset? finalizedAt)
    {
        if (ImmediateTerminalStates.Contains(source))
        {
            return OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.TerminalState);
        }

        if (!Edges.TryGetValue(source, out var targets) || !targets.Contains(target))
        {
            return OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.NotAllowed);
        }

        if (source == OrderStatus.Closed)
        {
            if (finalizedAt is not null)
            {
                return OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.Finalized);
            }

            if (claimWindowEndsAt is null || occurredAt > claimWindowEndsAt.Value)
            {
                return OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.ClaimWindowExpired);
            }
        }

        return OrderTransitionEvaluation.Success;
    }

    public static OrderTransitionEvaluation EvaluateVersion(int actual, int expected)
    {
        if (actual != expected)
        {
            return OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.VersionMismatch);
        }

        return actual == int.MaxValue
            ? OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.VersionOverflow)
            : OrderTransitionEvaluation.Success;
    }
}

public static class OrderPublicEventCodePolicy
{
    public static string? Map(OrderStatus target) => target switch
    {
        OrderStatus.ReadyForPickup => "PICKUP_SCHEDULED",
        OrderStatus.PickedUp => "PICKED_UP",
        OrderStatus.InTransit => "IN_TRANSIT",
        OrderStatus.Delivering => "OUT_FOR_DELIVERY",
        OrderStatus.FailedAttempt => "DELIVERY_ATTEMPTED",
        OrderStatus.Rescheduled => "RESCHEDULED",
        OrderStatus.Delivered => "DELIVERED",
        OrderStatus.Returning => "RETURNING",
        OrderStatus.Returned => "RETURNED",
        OrderStatus.Cancelled => "CANCELLED",
        _ => null,
    };
}
