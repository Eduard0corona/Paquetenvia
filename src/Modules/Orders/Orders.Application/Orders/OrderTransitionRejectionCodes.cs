using System.Collections.Frozen;
using Orders.Domain;

namespace Orders.Application.Orders;

/// <summary>
/// ORD-002-GUARD-CODES-2026-10-05: the closed AI-05 <c>TransitionConflictProblem.code</c> values a transitionOrder
/// 409 may carry, and the one place that maps every ORD-002 rejection to its code. The AI-04 matrix rule codes
/// (<see cref="OrderTransitionRuleCode"/>) and the <see cref="OrderTransitionGuardRegistry"/> guard codes are mapped
/// here and nowhere else; the rules themselves stay in the matrix and the registry.
/// <para>
/// A code is only ever produced after the order was locked as an order of the selected organization (owner-only,
/// ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03) and the caller holds the transitionOrder capability on it
/// (<see cref="IOrderTransitionAuthorizer.HoldsTransitionCapability"/>). Request-shape, idempotency, concurrency,
/// unknown, foreign and operator-visible orders keep the uniform 409 without a code.
/// </para>
/// </summary>
public static class OrderTransitionRejectionCodes
{
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string TransitionNotAllowed = "TRANSITION_NOT_ALLOWED";
    public const string OrderTerminal = "ORDER_TERMINAL";
    public const string OrderFinalized = "ORDER_FINALIZED";
    public const string ClaimWindowClosed = "CLAIM_WINDOW_CLOSED";
    public const string QuoteNotValid = "QUOTE_NOT_VALID";
    public const string PayerAcceptanceRequired = "PAYER_ACCEPTANCE_REQUIRED";
    public const string RestrictedGoodsAckRequired = "RESTRICTED_GOODS_ACK_REQUIRED";
    public const string ValidAssignmentRequired = "VALID_ASSIGNMENT_REQUIRED";
    public const string DriverCapacityExceeded = "DRIVER_CAPACITY_EXCEEDED";
    public const string AssignmentCostRequired = "ASSIGNMENT_COST_REQUIRED";
    public const string PickupProofRequired = "PICKUP_PROOF_REQUIRED";
    public const string DeliveryProofRequired = "DELIVERY_PROOF_REQUIRED";
    public const string CodNotRecorded = "COD_NOT_RECORDED";
    public const string UnresolvedIncident = "UNRESOLVED_INCIDENT";
    public const string CodNotReconciled = "COD_NOT_RECONCILED";
    public const string FinancialReconciliationIncomplete = "FINANCIAL_RECONCILIATION_INCOMPLETE";
    public const string ClaimWindowNotSet = "CLAIM_WINDOW_NOT_SET";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string CustodyAlreadyAcquired = "CUSTODY_ALREADY_ACQUIRED";
    public const string IncidentRequired = "INCIDENT_REQUIRED";
    public const string CustodyNotAcquired = "CUSTODY_NOT_ACQUIRED";
    public const string NextActionMismatch = "NEXT_ACTION_MISMATCH";

    /// <summary>Every rule code in AI-05 enum order, after the existing OFFLINE_OPERATION_EXPIRED.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        VersionConflict,
        TransitionNotAllowed,
        OrderTerminal,
        OrderFinalized,
        ClaimWindowClosed,
        QuoteNotValid,
        PayerAcceptanceRequired,
        RestrictedGoodsAckRequired,
        ValidAssignmentRequired,
        DriverCapacityExceeded,
        AssignmentCostRequired,
        PickupProofRequired,
        DeliveryProofRequired,
        CodNotRecorded,
        UnresolvedIncident,
        CodNotReconciled,
        FinancialReconciliationIncomplete,
        ClaimWindowNotSet,
        ReasonRequired,
        CustodyAlreadyAcquired,
        IncidentRequired,
        CustodyNotAcquired,
        NextActionMismatch,
    ];

    private static readonly FrozenSet<string> Defined = All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>AI-04 guard code (the <see cref="OrderTransitionGuardRegistry"/> defaults) to its AI-05 code.</summary>
    private static readonly FrozenDictionary<string, string> GuardCodes = new Dictionary<string, string>
    {
        ["valid_active_quote"] = QuoteNotValid,
        ["payer_acceptance"] = PayerAcceptanceRequired,
        ["restricted_goods_check"] = RestrictedGoodsAckRequired,
        ["eligible_driver"] = ValidAssignmentRequired,
        ["capacity_available"] = DriverCapacityExceeded,
        ["assignment_cost_present"] = AssignmentCostRequired,
        ["pickup_proof_complete"] = PickupProofRequired,
        ["delivery_proof_complete"] = DeliveryProofRequired,
        ["if_cod_expected_then_cod_status_recorded_or_reconciled"] = CodNotRecorded,
        ["no_unresolved_incident"] = UnresolvedIncident,
        ["if_cod_expected_then_cod_status_reconciled"] = CodNotReconciled,
        ["financial_reconciliation_complete"] = FinancialReconciliationIncomplete,
        ["claim_window_ends_at_set"] = ClaimWindowNotSet,
        ["now_before_or_equal_claim_window_ends_at"] = ClaimWindowClosed,
        ["claim_reason_present"] = ReasonRequired,
        ["claim_resolution_reason_present"] = ReasonRequired,
        ["cancellation_reason_present"] = ReasonRequired,
        ["if_from_at_pickup_then_custody_not_acquired"] = CustodyAlreadyAcquired,
        ["attempt_stage_recorded"] = IncidentRequired,
        ["custody_acquired_recorded"] = IncidentRequired,
        ["custody_acquired_true"] = CustodyNotAcquired,
        ["retry_custody_acquired_true"] = CustodyNotAcquired,
        ["retry_valid_assignment"] = ValidAssignmentRequired,
        ["failed_attempt_next_action_respected"] = NextActionMismatch,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The AI-04 guard codes this map covers, for the completeness tests.</summary>
    public static IEnumerable<string> MappedGuardCodes => GuardCodes.Keys;

    /// <summary>True only for a value of the closed AI-05 enum.</summary>
    public static bool IsDefined(string? code) => code is not null && Defined.Contains(code);

    /// <summary>The code of a failed guard; null (the uniform 409) for a guard this map does not know.</summary>
    public static string? ForGuard(string guardCode) =>
        GuardCodes.TryGetValue(guardCode, out var code) ? code : null;

    /// <summary>The code of a version or AI-04 matrix rejection; null for <see cref="OrderTransitionRuleCode.Allowed"/>.</summary>
    public static string? ForRule(OrderTransitionRuleCode rule) => rule switch
    {
        OrderTransitionRuleCode.VersionMismatch => VersionConflict,
        // An exhausted version admits no transition at all; reloading cannot help.
        OrderTransitionRuleCode.VersionOverflow => TransitionNotAllowed,
        OrderTransitionRuleCode.NotAllowed => TransitionNotAllowed,
        OrderTransitionRuleCode.TerminalState => OrderTerminal,
        OrderTransitionRuleCode.Finalized => OrderFinalized,
        OrderTransitionRuleCode.ClaimWindowExpired => ClaimWindowClosed,
        _ => null,
    };
}
