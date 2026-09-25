namespace Finance.Domain.Settlements;

/// <summary>
/// Which operational facts become settlement lines, and how each line names its source. A line is
/// traceable to exactly one source: an assignment for delivered or returned work, or the audit entry
/// that recorded a manual adjustment.
/// </summary>
public static class SettlementSourcePolicy
{
    public const string AssignmentSourcePrefix = "dispatch.assignments/";
    public const string AdjustmentSourcePrefix = "platform.audit_logs/";

    /// <summary>ORD-002 public event codes that record a payable outcome of an order.</summary>
    public const string DeliveredOutcome = "DELIVERED";
    public const string ReturnedOutcome = "RETURNED";

    public static IReadOnlyList<string> OutcomeEventCodes { get; } = [DeliveredOutcome, ReturnedOutcome];

    /// <summary>
    /// Assignment modalities a DRIVER is paid for directly. ALLY_CAPACITY work is owed to the ally, whose
    /// settlements stay reserved, so it is never settled to the driver who performed it.
    /// </summary>
    public static IReadOnlyList<string> DriverPayableAssignmentTypes { get; } = ["OWN", "EXTERNAL"];

    /// <summary>
    /// Order statuses that can only be reached through DELIVERED. The order state machine has no edge back
    /// from any of them, so a delivery that happened stays proven by the current status.
    /// </summary>
    public static IReadOnlySet<string> DeliveredOrderStatuses { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "DELIVERED", "CLOSED", "CLAIM_OPEN", "CLAIM_RESOLVED" };

    /// <summary>RETURNED is terminal, so a completed return stays proven by the current status.</summary>
    public static IReadOnlySet<string> ReturnedOrderStatuses { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "RETURNED" };

    /// <summary>Incident statuses that are still pending; RESOLVED and REJECTED are terminal.</summary>
    public static IReadOnlyList<string> PendingIncidentStatuses { get; } = ["OPEN", "INVESTIGATING"];

    public const string ClaimOpenOrderStatus = "CLAIM_OPEN";

    public static string AssignmentSource(Guid assignmentId) =>
        AssignmentSourcePrefix + assignmentId.ToString("D");

    public static string AdjustmentSource(Guid auditId) => AdjustmentSourcePrefix + auditId.ToString("D");

    /// <summary>
    /// The line an assignment earns when its order's latest payable outcome is <paramref name="outcomeEventCode"/>
    /// and the order is currently <paramref name="orderStatus"/>. Both must agree: the outcome event says when the
    /// work happened, the current status proves it is still the order's outcome. Anything else earns nothing.
    /// DELIVERY and RETURN share the assignment as their economic identity; the line type only classifies it.
    /// </summary>
    public static SettlementLineType? Classify(string? outcomeEventCode, string? orderStatus) =>
        outcomeEventCode switch
        {
            DeliveredOutcome when orderStatus is not null && DeliveredOrderStatuses.Contains(orderStatus) =>
                SettlementLineType.Delivery,
            ReturnedOutcome when orderStatus is not null && ReturnedOrderStatuses.Contains(orderStatus) =>
                SettlementLineType.Return,
            _ => null,
        };
}

/// <summary>The current state of the order behind one order-bearing settlement line.</summary>
public sealed record SettlementSourceState(
    SettlementLineType LineType,
    string OrderStatus,
    MoneyCents CodExpected,
    CodStatus? CodStatus,
    MoneyCents? CodAmount,
    bool HasPendingIncident);

/// <summary>Why a CALCULATED settlement cannot be approved yet.</summary>
public enum SettlementApprovalBlocker { None, CashPending, IncidentPending, ClaimPending }

/// <summary>
/// "El cierre bloquea efectivo/incidente pendiente": approval freezes the settlement, so it waits until
/// every included order is financially and operationally settled. The result does not depend on line
/// order, and when several blockers apply the first of cash, incident and claim is reported.
/// </summary>
public static class SettlementApprovalPolicy
{
    public static SettlementApprovalBlocker Evaluate(IReadOnlyCollection<SettlementSourceState> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Any(IsCashPending))
        {
            return SettlementApprovalBlocker.CashPending;
        }

        if (sources.Any(source => source.HasPendingIncident))
        {
            return SettlementApprovalBlocker.IncidentPending;
        }

        return sources.Any(source => source.OrderStatus == SettlementSourcePolicy.ClaimOpenOrderStatus)
            ? SettlementApprovalBlocker.ClaimPending
            : SettlementApprovalBlocker.None;
    }

    /// <summary>
    /// A delivery whose order expects cash is paid only once that cash is RECONCILED for the exact expected
    /// amount. A returned order never collected cash, so its COD expectation never blocks a RETURN line.
    /// </summary>
    public static bool IsCashPending(SettlementSourceState source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.LineType == SettlementLineType.Delivery &&
            CodLifecyclePolicy.IsExpected(source.CodExpected) &&
            !(source.CodStatus == Finance.Domain.CodStatus.Reconciled && source.CodAmount == source.CodExpected);
    }
}
