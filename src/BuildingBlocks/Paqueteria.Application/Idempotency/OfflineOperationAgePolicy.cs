namespace Paqueteria.Application.Idempotency;

/// <summary>Outcome of checking a client-captured timestamp against the server clock.</summary>
public enum OfflineOperationAge
{
    /// <summary>The request carries no client timestamp; it is an online operation and is not age-checked.</summary>
    NotDeclared,

    /// <summary>Within the maximum age in the past and not beyond the clock tolerance in the future.</summary>
    Accepted,

    /// <summary>Older than the maximum age: the offline queue must discard it (OPS-003-OFFLINE-72H).</summary>
    Expired,

    /// <summary>Later than the server clock plus the tolerance: the client clock is not trustworthy.</summary>
    AheadOfServerClock,
}

/// <summary>
/// OPS-003-SERVER-72H-REJECTION (AI-05 <c>x-offline-operation-age</c>). A driver PWA operation may be
/// replayed for up to 72 hours; the server refuses anything older, so an idempotency key purged after
/// its 72-hour floor can never let a stale replay execute twice. For the shared policy the maximum age
/// is fixed; only the tolerance for a client clock running ahead is configurable, between zero and
/// five minutes. An operation whose own contract owns a configurable maximum age and tolerance
/// (openIncident, OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27) evaluates through
/// <see cref="WithConfiguredLimits"/>, so the comparison itself is never duplicated.
/// </summary>
public sealed class OfflineOperationAgePolicy
{
    public const string ExpiredCode = "OFFLINE_OPERATION_EXPIRED";

    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(72);
    public static readonly TimeSpan DefaultClockTolerance = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumClockTolerance = TimeSpan.FromMinutes(5);

    public OfflineOperationAgePolicy(TimeSpan clockTolerance)
    {
        if (clockTolerance < TimeSpan.Zero || clockTolerance > MaximumClockTolerance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clockTolerance),
                $"The offline clock tolerance must be between zero and {MaximumClockTolerance}.");
        }

        AgeLimit = MaximumAge;
        ClockTolerance = clockTolerance;
    }

    private OfflineOperationAgePolicy(TimeSpan ageLimit, TimeSpan clockTolerance)
    {
        AgeLimit = ageLimit;
        ClockTolerance = clockTolerance;
    }

    public static OfflineOperationAgePolicy Default { get; } = new(DefaultClockTolerance);

    /// <summary>
    /// The same rule with a maximum age and a clock tolerance chosen by an operation whose contract
    /// makes them configurable. That operation validates its own published bounds; here only a
    /// positive age and a non-negative tolerance are required, so no configuration can disable the check.
    /// </summary>
    public static OfflineOperationAgePolicy WithConfiguredLimits(TimeSpan maximumAge, TimeSpan clockTolerance)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "The maximum age must be positive.");
        }

        if (clockTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockTolerance), "The clock tolerance cannot be negative.");
        }

        return new OfflineOperationAgePolicy(maximumAge, clockTolerance);
    }

    /// <summary>The oldest accepted timestamp age: <see cref="MaximumAge"/> unless configured by the operation.</summary>
    public TimeSpan AgeLimit { get; }

    public TimeSpan ClockTolerance { get; }

    /// <summary>
    /// Both bounds are inclusive: exactly the age limit old is still accepted, and so is exactly the
    /// tolerance ahead. The comparison is on absolute instants, so the client offset is irrelevant.
    /// </summary>
    public OfflineOperationAge Evaluate(DateTimeOffset? clientOccurredAt, DateTimeOffset serverNow)
    {
        if (clientOccurredAt is not { } occurredAt)
        {
            return OfflineOperationAge.NotDeclared;
        }

        if (occurredAt > serverNow + ClockTolerance)
        {
            return OfflineOperationAge.AheadOfServerClock;
        }

        return serverNow - occurredAt > AgeLimit
            ? OfflineOperationAge.Expired
            : OfflineOperationAge.Accepted;
    }
}
