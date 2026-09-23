namespace Incidents.Domain;

public enum IncidentStatus
{
    Open,
    Investigating,
    Resolved,
    Rejected,
}

public enum IncidentSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// The explicit next action AI-09 requires from a failed attempt. Both values are order
/// statuses ORD-002 already accepts as successors of <c>FAILED_ATTEMPT</c>.
/// </summary>
public enum IncidentNextAction
{
    Rescheduled,
    Returning,
}

public enum IncidentReasonCode
{
    RecipientAbsent,
    AddressNotFound,
    RecipientRefused,
    AccessRestricted,
    PaymentUnavailable,
    PackageDamaged,
    SecurityRisk,
}

public static class IncidentContract
{
    public const string Open = "OPEN";
    public const string Investigating = "INVESTIGATING";
    public const string Resolved = "RESOLVED";
    public const string Rejected = "REJECTED";

    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Critical = "CRITICAL";

    public const string Rescheduled = "RESCHEDULED";
    public const string Returning = "RETURNING";

    public const string RecipientAbsent = "RECIPIENT_ABSENT";
    public const string AddressNotFound = "ADDRESS_NOT_FOUND";
    public const string RecipientRefused = "RECIPIENT_REFUSED";
    public const string AccessRestricted = "ACCESS_RESTRICTED";
    public const string PaymentUnavailable = "PAYMENT_UNAVAILABLE";
    public const string PackageDamaged = "PACKAGE_DAMAGED";
    public const string SecurityRisk = "SECURITY_RISK";

    public static bool TryParseSeverity(string? value, out IncidentSeverity severity)
    {
        severity = value switch
        {
            Low => IncidentSeverity.Low,
            Medium => IncidentSeverity.Medium,
            High => IncidentSeverity.High,
            Critical => IncidentSeverity.Critical,
            _ => default,
        };
        return value is Low or Medium or High or Critical;
    }

    public static string ToContractValue(this IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Low => Low,
        IncidentSeverity.Medium => Medium,
        IncidentSeverity.High => High,
        IncidentSeverity.Critical => Critical,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown incident severity."),
    };

    public static bool TryParseNextAction(string? value, out IncidentNextAction nextAction)
    {
        nextAction = value switch
        {
            Rescheduled => IncidentNextAction.Rescheduled,
            Returning => IncidentNextAction.Returning,
            _ => default,
        };
        return value is Rescheduled or Returning;
    }

    public static string ToContractValue(this IncidentNextAction nextAction) => nextAction switch
    {
        IncidentNextAction.Rescheduled => Rescheduled,
        IncidentNextAction.Returning => Returning,
        _ => throw new ArgumentOutOfRangeException(nameof(nextAction), nextAction, "Unknown next action."),
    };

    public static bool TryParseReasonCode(string? value, out IncidentReasonCode reasonCode)
    {
        reasonCode = value switch
        {
            RecipientAbsent => IncidentReasonCode.RecipientAbsent,
            AddressNotFound => IncidentReasonCode.AddressNotFound,
            RecipientRefused => IncidentReasonCode.RecipientRefused,
            AccessRestricted => IncidentReasonCode.AccessRestricted,
            PaymentUnavailable => IncidentReasonCode.PaymentUnavailable,
            PackageDamaged => IncidentReasonCode.PackageDamaged,
            SecurityRisk => IncidentReasonCode.SecurityRisk,
            _ => default,
        };
        return value is RecipientAbsent or AddressNotFound or RecipientRefused or AccessRestricted
            or PaymentUnavailable or PackageDamaged or SecurityRisk;
    }

    public static string ToContractValue(this IncidentReasonCode reasonCode) => reasonCode switch
    {
        IncidentReasonCode.RecipientAbsent => RecipientAbsent,
        IncidentReasonCode.AddressNotFound => AddressNotFound,
        IncidentReasonCode.RecipientRefused => RecipientRefused,
        IncidentReasonCode.AccessRestricted => AccessRestricted,
        IncidentReasonCode.PaymentUnavailable => PaymentUnavailable,
        IncidentReasonCode.PackageDamaged => PackageDamaged,
        IncidentReasonCode.SecurityRisk => SecurityRisk,
        _ => throw new ArgumentOutOfRangeException(nameof(reasonCode), reasonCode, "Unknown reason code."),
    };
}

/// <summary>
/// INC-001 opens incidents only from the order states ORD-002 allows to reach
/// <c>FAILED_ATTEMPT</c>. The incident is the precondition of the transition, never its effect,
/// so the authoritative state machine stays the only writer of <c>orders.orders.status</c>.
/// </summary>
public static class IncidentOrderStatePolicy
{
    public const string AtPickup = "AT_PICKUP";
    public const string InTransit = "IN_TRANSIT";
    public const string Delivering = "DELIVERING";

    public static bool IsAllowedOpeningState(string? orderStatus) =>
        orderStatus is AtPickup or InTransit or Delivering;

    /// <summary>
    /// Custody is already acquired once the parcel has left the pickup point. An attempt that
    /// fails at pickup never acquired custody, which is what ORD-002 reads back to decide
    /// whether <c>RETURNING</c> and re-delivery are reachable.
    /// </summary>
    public static bool DerivesCustodyAcquired(string orderStatus) =>
        orderStatus is InTransit or Delivering;
}

/// <summary>
/// The SLA clock INC-001 requires. The deadline is derived from severity only, so replaying the
/// same opening request reproduces the same timestamp.
/// </summary>
public static class IncidentSlaPolicy
{
    public static TimeSpan ResolutionWindow(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Critical => TimeSpan.FromHours(2),
        IncidentSeverity.High => TimeSpan.FromHours(8),
        IncidentSeverity.Medium => TimeSpan.FromHours(24),
        IncidentSeverity.Low => TimeSpan.FromHours(72),
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown incident severity."),
    };

    public static DateTimeOffset DueAt(DateTimeOffset openedAt, IncidentSeverity severity) =>
        openedAt + ResolutionWindow(severity);
}

public static class IncidentEvidencePolicy
{
    public const int MinimumEvidenceCount = 1;
    public const int MaximumEvidenceCount = 10;

    public static bool IsAllowedCount(int count) =>
        count is >= MinimumEvidenceCount and <= MaximumEvidenceCount;
}
