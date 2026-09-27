using Incidents.Domain;
using Paqueteria.Application.Idempotency;

namespace Incidents.Application.Incidents;

/// <summary>
/// OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27 ("Unificar pero configurable"): an
/// incident report is an offline driver operation, so its <c>occurred_at</c> is judged by the same
/// rule as the other offline operations (AI-05 <c>x-offline-operation-age</c>) and a report older
/// than the maximum age is refused with <c>OFFLINE_OPERATION_EXPIRED</c>. Unlike those operations,
/// openIncident keeps its own configurable maximum age and clock tolerance, taken from the validated
/// <see cref="IncidentOperationalPolicy"/>; the comparison itself is the shared
/// <see cref="OfflineOperationAgePolicy"/>.
/// </summary>
public sealed class IncidentOccurrenceAgePolicy
{
    private readonly OfflineOperationAgePolicy rule;

    public IncidentOccurrenceAgePolicy(IncidentOperationalPolicy operationalPolicy)
    {
        ArgumentNullException.ThrowIfNull(operationalPolicy);
        if (!operationalPolicy.IsValid)
        {
            throw new ArgumentException(
                "The incident operational policy is outside the bounded MVP-1 surface.",
                nameof(operationalPolicy));
        }

        rule = OfflineOperationAgePolicy.WithConfiguredLimits(
            operationalPolicy.MaximumOccurrenceAge,
            operationalPolicy.MaximumOccurrenceSkew);
    }

    /// <summary>The owner-approved MVP-1 defaults: 72 hours back, five minutes ahead.</summary>
    public static IncidentOccurrenceAgePolicy Mvp1 { get; } = new(IncidentOperationalPolicy.Mvp1);

    public TimeSpan MaximumAge => rule.AgeLimit;

    public TimeSpan ClockTolerance => rule.ClockTolerance;

    /// <summary>
    /// Exactly the maximum age old is accepted, one tick older is <see cref="OfflineOperationAge.Expired"/>;
    /// exactly the tolerance ahead is accepted, one tick later is
    /// <see cref="OfflineOperationAge.AheadOfServerClock"/>.
    /// </summary>
    public OfflineOperationAge Evaluate(DateTimeOffset occurredAt, DateTimeOffset serverNow) =>
        rule.Evaluate(occurredAt, serverNow);

    /// <summary>A usable occurrence: declared (never the default instant) and accepted by the rule.</summary>
    public bool IsAccepted(DateTimeOffset occurredAt, DateTimeOffset serverNow) =>
        occurredAt != default && Evaluate(occurredAt, serverNow) == OfflineOperationAge.Accepted;
}
