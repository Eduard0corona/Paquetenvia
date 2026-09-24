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

    /// <summary>
    /// ORD-002 and ADR-014 only return what the operator already holds: a parcel that never left
    /// the pickup point cannot be returned, so <c>RETURNING</c> is reachable exactly when the
    /// attempt failed in a state that had already acquired custody. <c>RESCHEDULED</c> stays
    /// available from every opening state. The incident is refused before persistence rather
    /// than recorded with a next action the state machine could never honour.
    /// </summary>
    public static bool IsAllowedNextAction(string? orderStatus, IncidentNextAction nextAction) =>
        IsAllowedOpeningState(orderStatus) &&
        (nextAction is not IncidentNextAction.Returning || DerivesCustodyAcquired(orderStatus!));
}

/// <summary>
/// The SLA clock INC-001 requires, measured from the attempt and derived from severity only, so
/// replaying the same opening request reproduces the same timestamp. The windows themselves are
/// operational MVP-1 parameters and live in <see cref="IncidentOperationalPolicy"/>.
/// </summary>
public static class IncidentSlaPolicy
{
    public static TimeSpan ResolutionWindow(IncidentSeverity severity) =>
        IncidentOperationalPolicy.Mvp1.ResolutionWindow(severity);

    public static DateTimeOffset DueAt(DateTimeOffset openedAt, IncidentSeverity severity) =>
        IncidentOperationalPolicy.Mvp1.DueAt(openedAt, severity);
}

public static class IncidentEvidencePolicy
{
    /// <summary>
    /// At least one proof is semantic: AI-08 makes evidence mandatory, so this floor is not an
    /// operational parameter and no configuration may lower it.
    /// </summary>
    public const int MinimumEvidenceCount = 1;

    /// <summary>
    /// The ceiling AI-05 publishes for MVP-1. An operational policy may tighten it, never widen
    /// it beyond the published contract.
    /// </summary>
    public const int MaximumEvidenceCount = 10;

    public static bool IsAllowedCount(int count) =>
        IncidentOperationalPolicy.Mvp1.IsAllowedEvidenceCount(count);
}

/// <summary>
/// The operational MVP-1 parameters INC-001 may revise without changing its semantics: the SLA
/// windows, the retrospective window a report may claim, the tolerated clock skew and the evidence
/// ceiling. They are centralized here, bounded and validated as a unit, so a deployment cannot
/// quietly widen the contract; an invalid combination is rejected rather than applied. The
/// semantic requirements — reason, at least one proof, an explicit next action, a custody
/// precondition for <c>RETURNING</c> and an SLA timestamp — are not configurable.
/// </summary>
public sealed record IncidentOperationalPolicy
{
    /// <summary>The owner-approved MVP-1 defaults: 2h/8h/24h/72h, 72h retrospective, 5m skew, 10 proofs.</summary>
    public static readonly IncidentOperationalPolicy Mvp1 = new();

    private static readonly TimeSpan LongestConfigurableWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan LongestConfigurableSkew = TimeSpan.FromHours(1);

    public TimeSpan CriticalResolutionWindow { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan HighResolutionWindow { get; init; } = TimeSpan.FromHours(8);
    public TimeSpan MediumResolutionWindow { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan LowResolutionWindow { get; init; } = TimeSpan.FromHours(72);

    /// <summary>How far back an attempt may be reported. Older reports are malformed, not backdated.</summary>
    public TimeSpan MaximumOccurrenceAge { get; init; } = TimeSpan.FromHours(72);

    /// <summary>The device-to-server clock skew tolerated on <c>occurred_at</c>.</summary>
    public TimeSpan MaximumOccurrenceSkew { get; init; } = TimeSpan.FromMinutes(5);

    public int MaximumEvidenceCount { get; init; } = IncidentEvidencePolicy.MaximumEvidenceCount;

    /// <summary>
    /// A severer incident may never be given a laxer deadline, every window has to be a positive
    /// and bounded duration, and the evidence ceiling stays between the semantic floor and the
    /// bound AI-05 publishes. Anything else is a configuration error and fails closed.
    /// </summary>
    public bool IsValid =>
        IsWindow(CriticalResolutionWindow) &&
        IsWindow(HighResolutionWindow) &&
        IsWindow(MediumResolutionWindow) &&
        IsWindow(LowResolutionWindow) &&
        CriticalResolutionWindow <= HighResolutionWindow &&
        HighResolutionWindow <= MediumResolutionWindow &&
        MediumResolutionWindow <= LowResolutionWindow &&
        IsWindow(MaximumOccurrenceAge) &&
        MaximumOccurrenceSkew >= TimeSpan.Zero &&
        MaximumOccurrenceSkew <= LongestConfigurableSkew &&
        MaximumEvidenceCount >= IncidentEvidencePolicy.MinimumEvidenceCount &&
        MaximumEvidenceCount <= IncidentEvidencePolicy.MaximumEvidenceCount;

    public TimeSpan ResolutionWindow(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Critical => CriticalResolutionWindow,
        IncidentSeverity.High => HighResolutionWindow,
        IncidentSeverity.Medium => MediumResolutionWindow,
        IncidentSeverity.Low => LowResolutionWindow,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown incident severity."),
    };

    public DateTimeOffset DueAt(DateTimeOffset openedAt, IncidentSeverity severity) =>
        openedAt + ResolutionWindow(severity);

    public bool IsAllowedEvidenceCount(int count) =>
        count >= IncidentEvidencePolicy.MinimumEvidenceCount && count <= MaximumEvidenceCount;

    public bool IsValidOccurrence(DateTimeOffset occurredAt, DateTimeOffset now) =>
        occurredAt != default &&
        occurredAt <= now + MaximumOccurrenceSkew &&
        occurredAt >= now - MaximumOccurrenceAge;

    private static bool IsWindow(TimeSpan value) =>
        value > TimeSpan.Zero && value <= LongestConfigurableWindow;
}
