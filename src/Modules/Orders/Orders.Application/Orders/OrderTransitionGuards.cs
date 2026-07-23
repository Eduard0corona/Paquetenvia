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

    public bool CustodyAcquired =>
        Proofs.PickupProofComplete ||
        Incidents.AnyCustodyAcquired ||
        Incidents.RequestedIncidentCustodyAcquired;
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

public sealed class OrderTransitionGuardRegistry
{
    private readonly IReadOnlyList<IOrderTransitionGuard> guards;

    public OrderTransitionGuardRegistry()
        : this(CreateDefaults())
    {
    }

    public OrderTransitionGuardRegistry(IEnumerable<IOrderTransitionGuard> guards)
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
        static bool RetryDelivery(OrderTransitionGuardContext c) =>
            c.Target == OrderStatus.Delivering &&
            c.Source is OrderStatus.FailedAttempt or OrderStatus.Rescheduled;

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
            Guard(200, "custody_acquired_true", Return, c => c.CustodyAcquired),
            Guard(210, "retry_custody_acquired_true", RetryDelivery, c => c.CustodyAcquired),
            Guard(220, "retry_valid_assignment", RetryDelivery,
                c => c.Assignment.ExactlyOneActive && c.Assignment.EligibleDriver),
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
