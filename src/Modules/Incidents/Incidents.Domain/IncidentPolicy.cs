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
/// The two terminal outcomes a resolution may record. They are the terminal members of
/// <see cref="IncidentStatus"/>, never a second status vocabulary.
/// </summary>
public enum IncidentResolutionOutcome
{
    Resolved,
    Rejected,
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

    public static bool TryParseStatus(string? value, out IncidentStatus status)
    {
        status = value switch
        {
            Open => IncidentStatus.Open,
            Investigating => IncidentStatus.Investigating,
            Resolved => IncidentStatus.Resolved,
            Rejected => IncidentStatus.Rejected,
            _ => default,
        };
        return value is Open or Investigating or Resolved or Rejected;
    }

    public static string ToContractValue(this IncidentStatus status) => status switch
    {
        IncidentStatus.Open => Open,
        IncidentStatus.Investigating => Investigating,
        IncidentStatus.Resolved => Resolved,
        IncidentStatus.Rejected => Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown incident status."),
    };

    /// <summary>Only the terminal statuses parse as an outcome; OPEN and INVESTIGATING never do.</summary>
    public static bool TryParseResolutionOutcome(string? value, out IncidentResolutionOutcome outcome)
    {
        outcome = value switch
        {
            Resolved => IncidentResolutionOutcome.Resolved,
            Rejected => IncidentResolutionOutcome.Rejected,
            _ => default,
        };
        return value is Resolved or Rejected;
    }

    public static string ToContractValue(this IncidentResolutionOutcome outcome) => outcome switch
    {
        IncidentResolutionOutcome.Resolved => Resolved,
        IncidentResolutionOutcome.Rejected => Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown resolution outcome."),
    };

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
    /// ORD-002 and ADR-014 only return what the operator already holds: a parcel that was never
    /// picked up cannot be returned, so <c>RETURNING</c> is reachable exactly when custody was
    /// acquired. Custody is not derived from the order status here: it is the single derivation
    /// ORD-002 and the driver stops view share, a <c>PICKED_UP</c> status change in the order
    /// history, which the caller reads. <c>RESCHEDULED</c> stays available from every opening
    /// state. The incident is refused before persistence rather than recorded with a next action
    /// the state machine could never honour.
    /// </summary>
    public static bool IsAllowedNextAction(
        string? orderStatus,
        bool custodyAcquired,
        IncidentNextAction nextAction) =>
        IsAllowedOpeningState(orderStatus) &&
        (nextAction is not IncidentNextAction.Returning || custodyAcquired);
}

/// <summary>
/// The INC-001 resolution state machine. OPEN and INVESTIGATING are the pending states — the same
/// pair ORD-002 reads as an unresolved incident — and each may close into exactly one terminal
/// outcome. RESOLVED and REJECTED are final: a retry is answered by the idempotency record, never
/// by mutating a terminal incident again. Closing an incident never moves its order.
/// </summary>
public static class IncidentResolutionPolicy
{
    public static bool IsPending(IncidentStatus status) =>
        status is IncidentStatus.Open or IncidentStatus.Investigating;

    public static bool IsTerminal(IncidentStatus status) =>
        status is IncidentStatus.Resolved or IncidentStatus.Rejected;

    public static bool CanResolve(IncidentStatus current, IncidentResolutionOutcome outcome) =>
        IsPending(current) && Enum.IsDefined(outcome);

    public static IncidentStatus TerminalStatus(IncidentResolutionOutcome outcome) => outcome switch
    {
        IncidentResolutionOutcome.Resolved => IncidentStatus.Resolved,
        IncidentResolutionOutcome.Rejected => IncidentStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown resolution outcome."),
    };
}

/// <summary>
/// Closing an incident is a supervisory decision. Only an active DISPATCHER of the active
/// organization, or an active PLATFORM_ADMIN of it with a satisfied MFA challenge, may resolve or
/// reject. A driver never may, not even the one whose active assignment allowed them to open it.
/// </summary>
public static class IncidentResolutionAuthorizationPolicy
{
    public static bool MayResolve(
        bool isActiveDispatcher,
        bool isActivePlatformAdmin,
        bool mfaSatisfied) =>
        isActiveDispatcher || (isActivePlatformAdmin && mfaSatisfied);
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
