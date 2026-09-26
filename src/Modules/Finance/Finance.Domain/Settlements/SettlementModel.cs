namespace Finance.Domain.Settlements;

/// <summary>Lifecycle of a settlement header, as the SET-001 ledger guard admits it.</summary>
public enum SettlementStatus { Draft, Calculated, Approved, Paid, Void }

/// <summary>
/// Who a settlement pays. MVP-1 settles DRIVER only; ALLY and BUSINESS stay reserved AI-06 vocabulary
/// until their owning backlog items define their economic sources.
/// </summary>
public enum SettlementPayeeType { Driver }

/// <summary>
/// The settlement line types the SET-001 ledger admits. ROUTE_BASE, BONUS, WAITING and COD stay reserved
/// AI-06 vocabulary with no economic source, so they are not representable here.
/// </summary>
public enum SettlementLineType { Delivery, Return, Adjustment }

public static class SettlementContractValues
{
    public static IReadOnlyList<SettlementStatus> AllStatuses { get; } =
    [
        SettlementStatus.Draft, SettlementStatus.Calculated, SettlementStatus.Approved,
        SettlementStatus.Paid, SettlementStatus.Void,
    ];

    public static IReadOnlyList<SettlementLineType> AllLineTypes { get; } =
        [SettlementLineType.Delivery, SettlementLineType.Return, SettlementLineType.Adjustment];

    public static IReadOnlyList<SettlementPayeeType> AllPayeeTypes { get; } = [SettlementPayeeType.Driver];

    public static string ToContractValue(this SettlementStatus value) => value switch
    {
        SettlementStatus.Draft => "DRAFT",
        SettlementStatus.Calculated => "CALCULATED",
        SettlementStatus.Approved => "APPROVED",
        SettlementStatus.Paid => "PAID",
        SettlementStatus.Void => "VOID",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this SettlementPayeeType value) => value switch
    {
        SettlementPayeeType.Driver => "DRIVER",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this SettlementLineType value) => value switch
    {
        SettlementLineType.Delivery => "DELIVERY",
        SettlementLineType.Return => "RETURN",
        SettlementLineType.Adjustment => "ADJUSTMENT",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParseStatus(string? value, out SettlementStatus status)
    {
        switch (value)
        {
            case "DRAFT": status = SettlementStatus.Draft; return true;
            case "CALCULATED": status = SettlementStatus.Calculated; return true;
            case "APPROVED": status = SettlementStatus.Approved; return true;
            case "PAID": status = SettlementStatus.Paid; return true;
            case "VOID": status = SettlementStatus.Void; return true;
            default: status = default; return false;
        }
    }

    public static bool TryParsePayeeType(string? value, out SettlementPayeeType payeeType)
    {
        payeeType = SettlementPayeeType.Driver;
        return value == "DRIVER";
    }

    public static bool TryParseLineType(string? value, out SettlementLineType lineType)
    {
        switch (value)
        {
            case "DELIVERY": lineType = SettlementLineType.Delivery; return true;
            case "RETURN": lineType = SettlementLineType.Return; return true;
            case "ADJUSTMENT": lineType = SettlementLineType.Adjustment; return true;
            default: lineType = default; return false;
        }
    }
}

/// <summary>
/// The application side of the settlement lifecycle. The database guard admits the same edges and is the
/// last word; this policy decides them first so a refused transition is a stable conflict, never a
/// constraint violation.
/// </summary>
public static class SettlementLifecyclePolicy
{
    /// <summary>Adjustments move the total, which is only possible before approval freezes it.</summary>
    public static bool CanAdjust(SettlementStatus current) => current == SettlementStatus.Calculated;

    public static bool CanApprove(SettlementStatus current) => current == SettlementStatus.Calculated;

    public static bool CanMarkPaid(SettlementStatus current) => current == SettlementStatus.Approved;

    /// <summary>A settlement may be voided until it is PAID; PAID and VOID are terminal.</summary>
    public static bool CanVoid(SettlementStatus current) =>
        current is SettlementStatus.Draft or SettlementStatus.Calculated or SettlementStatus.Approved;

    public static bool IsTerminal(SettlementStatus current) =>
        current is SettlementStatus.Paid or SettlementStatus.Void;
}

/// <summary>Bounded plain-text rationale kept only as append-only audit evidence.</summary>
public static class SettlementReasonPolicy
{
    public const int MaximumLength = 500;

    /// <summary>
    /// Surrounding whitespace is rejected rather than trimmed, so the audit records exactly what the actor
    /// sent, and control characters are rejected because the rationale is a single statement.
    /// </summary>
    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaximumLength } &&
        !char.IsWhiteSpace(value[0]) &&
        !char.IsWhiteSpace(value[^1]) &&
        !value.Any(char.IsControl);
}

/// <summary>Exact reconciliation of a persisted ledger: the header total is the sum of its lines.</summary>
public static class SettlementLedger
{
    /// <summary>
    /// Sums in <see cref="Int128"/>, so no set of bigint lines can wrap around and appear to reconcile.
    /// </summary>
    public static bool Reconciles(long totalCents, IEnumerable<long> lineAmountsCents)
    {
        ArgumentNullException.ThrowIfNull(lineAmountsCents);
        Int128 sum = 0;
        foreach (var amount in lineAmountsCents)
        {
            sum += amount;
        }

        return sum == totalCents;
    }

    /// <summary>
    /// The exact bigint total of <paramref name="lineAmountsCents"/>, or <see langword="null"/> when it cannot
    /// be represented as a bigint.
    /// </summary>
    public static long? TryTotal(IEnumerable<long> lineAmountsCents)
    {
        ArgumentNullException.ThrowIfNull(lineAmountsCents);
        Int128 sum = 0;
        foreach (var amount in lineAmountsCents)
        {
            sum += amount;
        }

        return sum >= long.MinValue && sum <= long.MaxValue ? (long)sum : null;
    }

    /// <summary>Checked bigint arithmetic for an adjustment; <see langword="null"/> when it would overflow.</summary>
    public static long? TryApply(long totalCents, long adjustmentCents)
    {
        var sum = (Int128)totalCents + adjustmentCents;
        return sum >= long.MinValue && sum <= long.MaxValue ? (long)sum : null;
    }
}
