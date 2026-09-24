using Incidents.Domain;

namespace Incidents.Infrastructure;

/// <summary>
/// How the mandatory incident description is protected before it is persisted. There is no
/// permissive default: an unconfigured deployment protects nothing, so INC-001 fails closed.
/// </summary>
public enum IncidentPiiProtectorKind
{
    Disabled,
    Mock,
}

/// <summary>
/// The bounded operational surface of INC-001. Everything here is an MVP-1 parameter that may be
/// revised without changing the semantics AI-08 fixes; the semantics themselves are not settings.
/// The values are validated as a unit on start, so an invalid deployment never serves incidents.
/// </summary>
public sealed class IncidentsOptions
{
    public const string SectionName = "Incidents";

    public IncidentPiiProtectorKind PiiProtector { get; set; }

    /// <summary>The key version recorded alongside the ciphertext in <c>incidents.pii_key_version</c>.</summary>
    public string PiiKeyVersion { get; set; } = "inc001-v1";

    public int CommandTimeoutSeconds { get; set; } = 30;

    public int CriticalSlaHours { get; set; } = 2;
    public int HighSlaHours { get; set; } = 8;
    public int MediumSlaHours { get; set; } = 24;
    public int LowSlaHours { get; set; } = 72;

    /// <summary>How far back an attempt may be reported. The approved MVP-1 window is 72 hours.</summary>
    public int MaximumOccurrenceAgeHours { get; set; } = 72;

    public int MaximumOccurrenceSkewMinutes { get; set; } = 5;

    public int MaximumEvidenceCount { get; set; } = IncidentEvidencePolicy.MaximumEvidenceCount;

    /// <summary>
    /// The configured values as the single policy the domain validates and the service reads.
    /// </summary>
    public IncidentOperationalPolicy OperationalPolicy => new()
    {
        CriticalResolutionWindow = Hours(CriticalSlaHours),
        HighResolutionWindow = Hours(HighSlaHours),
        MediumResolutionWindow = Hours(MediumSlaHours),
        LowResolutionWindow = Hours(LowSlaHours),
        MaximumOccurrenceAge = Hours(MaximumOccurrenceAgeHours),
        MaximumOccurrenceSkew = Minutes(MaximumOccurrenceSkewMinutes),
        MaximumEvidenceCount = MaximumEvidenceCount,
    };

    /// <summary>
    /// Converts without overflowing: an out-of-range configured number becomes an out-of-range
    /// window that <see cref="IncidentOperationalPolicy.IsValid"/> rejects, never an exception
    /// thrown while binding.
    /// </summary>
    private static TimeSpan Hours(int value) =>
        value is > 0 and <= 24 * 365 ? TimeSpan.FromHours(value) : TimeSpan.Zero;

    private static TimeSpan Minutes(int value) =>
        value is >= 0 and <= 24 * 60 ? TimeSpan.FromMinutes(value) : TimeSpan.MinValue;
}
