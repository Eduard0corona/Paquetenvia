using Orders.Domain;

namespace Orders.Application.Orders;

/// <summary>
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: who decides how an order leaves <c>FAILED_ATTEMPT</c>, and to
/// what. The incident that justified the latest <c>FAILED_ATTEMPT</c> decides it:
/// <list type="bullet">
/// <item>a recorded next action binds the successor (AI-04 <c>failed_attempt_next_action_respected</c>):
/// <c>RETURNING</c> allows only <c>RETURNING</c>; <c>RESCHEDULED</c> allows <c>RESCHEDULED</c> or the immediate
/// <c>DELIVERING</c> retry;</item>
/// <item>no next action — a driver report, "Despacho decide" — leaves the decision pending: a DISPATCHER, or a
/// PLATFORM_ADMIN with a satisfied MFA challenge (the transitionOrder role rules, which the authorizer applies
/// before any guard), chooses <c>RESCHEDULED</c>, <c>DELIVERING</c> or <c>RETURNING</c>, and a DRIVER never leaves
/// <c>FAILED_ATTEMPT</c> (AI-04 <c>failed_attempt_dispatch_decides</c>);</item>
/// <item>an incident INC-001 adopted from a pre-INC-001 installation binds nothing (its next action was
/// backfilled, never chosen).</item>
/// </list>
/// Every other guard of the target still applies: custody for <c>RETURNING</c>, custody and a valid assignment for
/// the retry. getOrder's advisory <c>allowed_transitions</c> apply this same policy
/// (<see cref="OrderAllowedTransitionsPolicy"/>), so the order detail never offers what it would refuse here.
/// </summary>
public static class FailedAttemptDecisionPolicy
{
    private const string Rescheduled = "RESCHEDULED";
    private const string Returning = "RETURNING";

    /// <summary>The justifying incident records no next action: dispatch has still to decide.</summary>
    public static bool IsDecisionPending(IncidentGuardSnapshot incidents)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        return incidents.LatestFailedAttemptIncidentFound &&
            !incidents.LatestFailedAttemptIncidentAdopted &&
            incidents.LatestFailedAttemptNextAction is null;
    }

    /// <summary>
    /// The roles that decide a pending failed attempt. MFA is not repeated here: a PLATFORM_ADMIN without it never
    /// reaches the guards (<see cref="IOrderTransitionAuthorizer"/>).
    /// </summary>
    public static bool MayDecide(string? activeRole) => activeRole is "DISPATCHER" or "PLATFORM_ADMIN";

    /// <summary>AI-04 <c>failed_attempt_dispatch_decides</c>: a pending decision is taken only by dispatch.</summary>
    public static bool DecidedByDispatch(IncidentGuardSnapshot incidents, string? activeRole) =>
        !IsDecisionPending(incidents) || MayDecide(activeRole);

    /// <summary>AI-04 <c>failed_attempt_next_action_respected</c>: the successor the justifying incident allows.</summary>
    public static bool RespectsNextAction(IncidentGuardSnapshot incidents, OrderStatus target)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        if (incidents.LatestFailedAttemptIncidentAdopted)
        {
            return true;
        }

        if (IsDecisionPending(incidents))
        {
            return target is OrderStatus.Rescheduled or OrderStatus.Delivering or OrderStatus.Returning;
        }

        return target switch
        {
            OrderStatus.Returning => incidents.LatestFailedAttemptNextAction == Returning,
            OrderStatus.Rescheduled or OrderStatus.Delivering => incidents.LatestFailedAttemptNextAction == Rescheduled,
            _ => false,
        };
    }

    /// <summary>
    /// The exits of <c>FAILED_ATTEMPT</c> getOrder may list for this actor: the decision above and the custody the
    /// target needs (<c>custody_acquired_true</c>, <c>retry_custody_acquired_true</c>). The retry's assignment
    /// (<c>retry_valid_assignment</c>) is judged only by transitionOrder.
    /// </summary>
    public static bool AllowsExit(FailedAttemptExitSnapshot snapshot, string? activeRole, OrderStatus target)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return DecidedByDispatch(snapshot.Incidents, activeRole) &&
            RespectsNextAction(snapshot.Incidents, target) &&
            (target is not (OrderStatus.Returning or OrderStatus.Delivering) || snapshot.Custody.PickedUpRecorded);
    }
}

/// <summary>
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: what getOrder reads, for an order in <c>FAILED_ATTEMPT</c> only,
/// to list its exits: the incident snapshot transitionOrder's guards read (<see cref="IOrderIncidentGuardReader"/>)
/// and the single custody derivation (<see cref="IOrderCustodyGuardReader"/>).
/// </summary>
public sealed record FailedAttemptExitSnapshot(IncidentGuardSnapshot Incidents, CustodyGuardSnapshot Custody);
