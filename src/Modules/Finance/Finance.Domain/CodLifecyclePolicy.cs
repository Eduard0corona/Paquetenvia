namespace Finance.Domain;

/// <summary>
/// FIN-001 cash-on-delivery lifecycle rules. The order state machine owns the transition guards
/// (<c>if_cod_expected_then_cod_status_recorded_or_reconciled</c> before DELIVERED and
/// <c>if_cod_expected_then_cod_status_reconciled</c> before CLOSED); this policy owns the finance-side
/// preconditions that make those guards satisfiable and is the single place both are asserted from.
/// </summary>
public static class CodLifecyclePolicy
{
    /// <summary>
    /// Order statuses a collection may be recorded in: the driver already holds custody and the order
    /// has not yet been delivered, so recording always precedes the DELIVERED transition.
    /// </summary>
    public static IReadOnlySet<string> OrderStatusesAllowingRecord { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "PICKED_UP", "IN_TRANSIT", "DELIVERING", "FAILED_ATTEMPT", "RESCHEDULED",
        };

    /// <summary>
    /// Order statuses reconciliation may still happen in. CLOSED is excluded because reconciliation
    /// must precede financial close; CANCELLED and RETURNED carry no collectable cash.
    /// </summary>
    public static IReadOnlySet<string> OrderStatusesAllowingReconciliation { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "DELIVERING", "DELIVERED", "CLAIM_OPEN", "CLAIM_RESOLVED",
        };

    /// <summary>A collection is only recordable when the order actually expects cash.</summary>
    public static bool IsExpected(MoneyCents codExpected) => codExpected.AmountCents > 0;

    /// <summary>
    /// The recorded amount must equal the order's expectation exactly. Partial or excess collection is
    /// out of FIN-001 scope, and the order guards compare the same two values.
    /// </summary>
    public static bool AmountMatchesExpectation(MoneyCents codExpected, MoneyCents amount) =>
        IsExpected(codExpected) && amount == codExpected;

    public static bool CanRecord(string orderStatus, MoneyCents codExpected, MoneyCents amount, bool alreadyRecorded) =>
        !alreadyRecorded &&
        AmountMatchesExpectation(codExpected, amount) &&
        OrderStatusesAllowingRecord.Contains(orderStatus);

    public static bool CanReconcile(string orderStatus, CodStatus current) =>
        current == CodStatus.Recorded && OrderStatusesAllowingReconciliation.Contains(orderStatus);

    /// <summary>
    /// Mirrors the DELIVERING to DELIVERED guard: when cash is expected it must already be RECORDED
    /// (or RECONCILED) for the exact expected amount.
    /// </summary>
    public static bool SatisfiesDeliveryRequirement(MoneyCents codExpected, CodStatus? status, MoneyCents? amount) =>
        !IsExpected(codExpected) ||
        (status is CodStatus.Recorded or CodStatus.Reconciled && amount == codExpected);

    /// <summary>
    /// Mirrors the DELIVERED to CLOSED guard: when cash is expected it must be RECONCILED for the exact
    /// expected amount, and when no cash is expected no COD record may exist at all.
    /// </summary>
    public static bool SatisfiesCloseRequirement(MoneyCents codExpected, CodStatus? status, MoneyCents? amount) =>
        IsExpected(codExpected)
            ? status == CodStatus.Reconciled && amount == codExpected
            : status is null;
}
