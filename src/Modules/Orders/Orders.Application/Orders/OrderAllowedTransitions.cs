using Orders.Domain;

namespace Orders.Application.Orders;

/// <summary>
/// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: one ORD-002 transition the caller could request now, with the metadata
/// members transitionOrder requires for it (<see cref="OrderTransitionInputPolicy.RequiredMetadataKeys"/>).
/// </summary>
public sealed record OrderAllowedTransition(OrderStatus Target, IReadOnlyList<string> RequiredMetadata);

/// <summary>
/// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: the advisory <c>allowed_transitions</c> of getOrder, so the UI never
/// duplicates the state machine. It reuses, in the same order, the checks transitionOrder applies before its guards:
/// <list type="number">
/// <item>ORD-002 is owner-only (ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03): transitionOrder locks the order with
/// <c>owner_org_id = selected organization</c>, so an operator, or any organization that only sees the order, gets
/// none.</item>
/// <item><see cref="OrderTransitionMatrix.EvaluateVersion"/> on the current version (an exhausted version admits
/// nothing).</item>
/// <item>The <see cref="OrderTransitionMatrix"/> edges and <see cref="OrderTransitionMatrix.Evaluate"/> (terminal
/// states, the claim window and finalization of CLOSED) at the server clock.</item>
/// <item><see cref="IOrderTransitionAuthorizer"/> with the role and driver-assignment snapshot transitionOrder reads
/// (<see cref="IOrderTransitionAuthorizationReader"/>) and the session MFA.</item>
/// </list>
/// The <see cref="OrderTransitionGuardRegistry"/> guards (quote and acceptance, assignment, proofs, custody,
/// incidents, COD and reconciliation) are not evaluated and never exposed: a listed transition can still be refused
/// with 409, and transitionOrder re-evaluates everything in its own transaction.
/// </summary>
public static class OrderAllowedTransitionsPolicy
{
    public static IReadOnlyList<OrderAllowedTransition> Compute(
        IOrderTransitionAuthorizer authorizer,
        Guid organizationId,
        OrderResult order,
        OrderTransitionAuthorizationSnapshot authorization,
        bool mfaSatisfied,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(authorization);
        if (organizationId == Guid.Empty ||
            order.OwnerOrganizationId != organizationId ||
            !OrderContractValues.TryParseOrderStatus(order.Status, out var source) ||
            !OrderTransitionMatrix.EvaluateVersion(order.Version, order.Version).Allowed ||
            !OrderTransitionMatrix.AllowedTransitions.TryGetValue(source, out var targets))
        {
            return [];
        }

        return targets
            .Order()
            .Where(target => OrderTransitionMatrix.Evaluate(
                source,
                target,
                evaluatedAt,
                order.ClaimWindowEndsAt,
                order.FinalizedAt).Allowed)
            .Where(target => authorizer.IsAuthorized(new OrderTransitionAuthorizationContext(
                authorization.ActiveRole,
                source,
                target,
                mfaSatisfied,
                authorization.HasMatchingDriverAssignment)))
            .Select(target => new OrderAllowedTransition(
                target,
                OrderTransitionInputPolicy.RequiredMetadataKeys(source, target)))
            .ToArray();
    }
}
