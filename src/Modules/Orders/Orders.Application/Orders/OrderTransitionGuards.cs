using Orders.Domain;

namespace Orders.Application.Orders;

public sealed class OrderTransitionGuardContext
{
    public required OrderStatus Source { get; init; }
    public required OrderStatus Target { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset? ClaimWindowEndsAt { get; init; }
    public required DateTimeOffset? FinalizedAt { get; init; }
    public required long CodExpectedCents { get; init; }
    public required bool MonetaryIntegrityValid { get; init; }
    public required NormalizedTransitionMetadata Metadata { get; init; }
    public QuoteAcceptanceGuardSnapshot QuoteAcceptance { get; init; } = new(false, false);
    public AssignmentGuardSnapshot Assignment { get; init; } = new(false, false, false, false);
    public ProofGuardSnapshot Proofs { get; init; } = new(false, false);
    public IncidentGuardSnapshot Incidents { get; init; } = new(false, false, false, false);
    public CodGuardSnapshot Cod { get; init; } = new(false, null, null);
    public CustodyGuardSnapshot Custody { get; init; } = new(false);

    /// <summary>
    /// The transitionOrder active role of the actor (the membership snapshot the authorizer judged), read by
    /// <c>failed_attempt_dispatch_decides</c> (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11). Null for the system
    /// actor and for the flows that never leave <c>FAILED_ATTEMPT</c>; a null role never decides a pending failed
    /// attempt.
    /// </summary>
    public string? ActorRole { get; init; }

    /// <summary>
    /// Custody comes only from the <c>PICKED_UP</c> history. A pickup photo is evidence of an
    /// attempt, not custody: an incident at pickup must carry one while the parcel never left.
    /// </summary>
    public bool CustodyAcquired => Custody.PickedUpRecorded;
}

public sealed record OrderTransitionGuardResult(bool Satisfied, string Code)
{
    public static OrderTransitionGuardResult Success(string code) => new(true, code);
    public static OrderTransitionGuardResult Failure(string code) => new(false, code);
}

public interface IOrderTransitionGuard
{
    string Code { get; }
    int Order { get; }
    bool AppliesTo(OrderTransitionGuardContext context);
    OrderTransitionGuardResult Evaluate(OrderTransitionGuardContext context);
}

/// <summary>
/// The AI-04 transition guards, evaluated in order by the ORD-002 transition and by the Dispatch assignment and
/// external-offer flows before an order moves.
/// </summary>
/// <remarks>
/// ORD-002-API-GUARD-REGISTRY: the parameterless constructor is the only public one, so no host can compose a registry
/// without the AI-04 guards. A container activates the longest public constructor it can satisfy, and an
/// <c>IEnumerable&lt;IOrderTransitionGuard&gt;</c> parameter is always satisfiable (empty when no guard is
/// registered): while the guard-list constructor was public, the type registrations of the Orders and Dispatch modules
/// gave the API a registry with no guard at all.
/// </remarks>
public sealed class OrderTransitionGuardRegistry
{
    private readonly IReadOnlyList<IOrderTransitionGuard> guards;

    /// <summary>The AI-04 guards: the registry every host composes.</summary>
    public OrderTransitionGuardRegistry()
        : this(CreateDefaults())
    {
    }

    /// <summary>
    /// A registry over explicit guards, for the uniqueness and ordering tests only. It stays internal so that no
    /// container can ever select it (see the remarks on <see cref="OrderTransitionGuardRegistry"/>).
    /// </summary>
    internal OrderTransitionGuardRegistry(IEnumerable<IOrderTransitionGuard> guards)
    {
        this.guards = guards.OrderBy(guard => guard.Order).ThenBy(guard => guard.Code, StringComparer.Ordinal).ToArray();
        if (this.guards.Select(guard => guard.Code).Distinct(StringComparer.Ordinal).Count() != this.guards.Count)
        {
            throw new InvalidOperationException("Order transition guard codes must be unique.");
        }
    }

    public IReadOnlyList<IOrderTransitionGuard> Guards => guards;

    public OrderTransitionGuardResult Evaluate(OrderTransitionGuardContext context)
    {
        foreach (var guard in guards.Where(guard => guard.AppliesTo(context)))
        {
            var result = guard.Evaluate(context);
            if (!result.Satisfied)
            {
                return result;
            }
        }

        return OrderTransitionGuardResult.Success("SATISFIED");
    }

    private static IEnumerable<IOrderTransitionGuard> CreateDefaults()
    {
        static bool Confirm(OrderTransitionGuardContext c) =>
            c.Source == OrderStatus.Draft && c.Target == OrderStatus.Confirmed;
        static bool Assign(OrderTransitionGuardContext c) => c.Target == OrderStatus.Assigned;
        static bool Pickup(OrderTransitionGuardContext c) =>
            c.Source == OrderStatus.AtPickup && c.Target == OrderStatus.PickedUp;
        static bool Deliver(OrderTransitionGuardContext c) =>
            c.Source == OrderStatus.Delivering && c.Target == OrderStatus.Delivered;
        static bool Close(OrderTransitionGuardContext c) =>
            c.Source == OrderStatus.Delivered && c.Target == OrderStatus.Closed;
        static bool Claim(OrderTransitionGuardContext c) => c.Target == OrderStatus.ClaimOpen;
        static bool ResolveClaim(OrderTransitionGuardContext c) =>
            c.Source == OrderStatus.ClaimOpen && c.Target == OrderStatus.ClaimResolved;
        static bool Cancel(OrderTransitionGuardContext c) => c.Target == OrderStatus.Cancelled;
        static bool Fail(OrderTransitionGuardContext c) => c.Target == OrderStatus.FailedAttempt;
        static bool Return(OrderTransitionGuardContext c) => c.Target == OrderStatus.Returning;
        // D8-RESCHEDULED-NO-DIRECT-DELIVERY: the only retry into DELIVERING is the immediate
        // FAILED_ATTEMPT -> DELIVERING, which keeps the active assignment.
        static bool RetryDelivery(OrderTransitionGuardContext c) =>
            c.Target == OrderStatus.Delivering &&
            c.Source == OrderStatus.FailedAttempt;
        static bool LeaveFailedAttempt(OrderTransitionGuardContext c) =>
            c.Source == OrderStatus.FailedAttempt;

        return
        [
            Guard(10, "valid_active_quote", Confirm, c => c.QuoteAcceptance.ValidConsumedQuote),
            Guard(20, "payer_acceptance", Confirm, c => c.QuoteAcceptance.ValidAcceptance),
            Guard(30, "restricted_goods_check", Confirm, c => c.Metadata.RestrictedGoodsAcknowledged is true),
            Guard(40, "eligible_driver", Assign, c => c.Assignment.ExactlyOneActive && c.Assignment.EligibleDriver),
            Guard(50, "capacity_available", Assign, c => c.Assignment.CapacityAttested),
            Guard(60, "assignment_cost_present", Assign, c => c.Assignment.CostPresent),
            Guard(70, "pickup_proof_complete", Pickup, c => c.Proofs.PickupProofComplete),
            Guard(80, "delivery_proof_complete", Deliver, c => c.Proofs.DeliveryProofComplete),
            Guard(90, "if_cod_expected_then_cod_status_recorded_or_reconciled", Deliver,
                c => c.CodExpectedCents == 0 ||
                    (c.Cod.HasRecord &&
                     c.Cod.Status is "RECORDED" or "RECONCILED" &&
                     c.Cod.AmountCents == c.CodExpectedCents)),
            Guard(100, "no_unresolved_incident", Close, c => !c.Incidents.HasUnresolvedIncident),
            Guard(110, "if_cod_expected_then_cod_status_reconciled", Close,
                c => c.CodExpectedCents == 0 ||
                    (c.Cod.HasRecord && c.Cod.Status == "RECONCILED" &&
                     c.Cod.AmountCents == c.CodExpectedCents)),
            Guard(120, "financial_reconciliation_complete", Close,
                c => c.MonetaryIntegrityValid &&
                    (c.CodExpectedCents > 0
                        ? c.Cod.Status == "RECONCILED"
                        : !c.Cod.HasRecord)),
            Guard(130, "claim_window_ends_at_set", Close, c => c.ClaimWindowEndsAt is not null),
            Guard(140, "now_before_or_equal_claim_window_ends_at", Claim,
                c => c.ClaimWindowEndsAt is not null &&
                    c.OccurredAt <= c.ClaimWindowEndsAt.Value &&
                    c.FinalizedAt is null),
            Guard(150, "claim_reason_present", Claim, c => !string.IsNullOrWhiteSpace(c.Reason)),
            Guard(155, "claim_resolution_reason_present", ResolveClaim,
                c => !string.IsNullOrWhiteSpace(c.Reason)),
            Guard(160, "cancellation_reason_present", Cancel, c => !string.IsNullOrWhiteSpace(c.Reason)),
            Guard(170, "if_from_at_pickup_then_custody_not_acquired", Cancel,
                c => c.Source != OrderStatus.AtPickup || !c.CustodyAcquired),
            Guard(180, "attempt_stage_recorded", Fail, c => c.Metadata.IncidentId is not null && c.Incidents.RequestedIncidentValid),
            Guard(190, "custody_acquired_recorded", Fail, c => c.Incidents.RequestedIncidentValid),
            // INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: a driver report leaves the decision to dispatch, so
            // only a DISPATCHER or an MFA-satisfied PLATFORM_ADMIN leaves FAILED_ATTEMPT while it is pending. It runs
            // before the target's own guards, so a driver is always told who decides.
            Guard(195, "failed_attempt_dispatch_decides", LeaveFailedAttempt,
                c => FailedAttemptDecisionPolicy.DecidedByDispatch(c.Incidents, c.ActorRole)),
            Guard(200, "custody_acquired_true", Return, c => c.CustodyAcquired),
            Guard(210, "retry_custody_acquired_true", RetryDelivery, c => c.CustodyAcquired),
            Guard(220, "retry_valid_assignment", RetryDelivery,
                c => c.Assignment.ExactlyOneActive && c.Assignment.EligibleDriver),
            // An adopted incident's next action is a backfilled default, not a decision: it does
            // not bind the successor. A pending decision admits RESCHEDULED, DELIVERING and
            // RETURNING. Every other guard of the target (custody for RETURNING, a valid assignment
            // for redelivery) still applies.
            Guard(230, "failed_attempt_next_action_respected", LeaveFailedAttempt,
                c => FailedAttemptDecisionPolicy.RespectsNextAction(c.Incidents, c.Target)),
        ];
    }

    private static IOrderTransitionGuard Guard(
        int order,
        string code,
        Func<OrderTransitionGuardContext, bool> applies,
        Func<OrderTransitionGuardContext, bool> evaluate) =>
        new ConfiguredGuard(order, code, applies, evaluate);

    private sealed class ConfiguredGuard(
        int order,
        string code,
        Func<OrderTransitionGuardContext, bool> applies,
        Func<OrderTransitionGuardContext, bool> evaluate) : IOrderTransitionGuard
    {
        public string Code { get; } = code;
        public int Order { get; } = order;
        public bool AppliesTo(OrderTransitionGuardContext context) => applies(context);
        public OrderTransitionGuardResult Evaluate(OrderTransitionGuardContext context) =>
            evaluate(context)
                ? OrderTransitionGuardResult.Success(Code)
                : OrderTransitionGuardResult.Failure(Code);
    }
}
