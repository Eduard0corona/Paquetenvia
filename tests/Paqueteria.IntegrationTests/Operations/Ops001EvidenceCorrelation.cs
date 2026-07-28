namespace Paqueteria.IntegrationTests.Operations;

internal sealed record Ops001RealtimeExpectation(
    Guid EventId,
    Guid OrderId,
    long AggregateVersion,
    string EventType);

internal sealed record Ops001RealtimeDelivery(
    Guid EventId,
    Guid AggregateId,
    long AggregateVersion,
    string EventType);

internal sealed class Ops001RealtimeCorrelation
{
    private readonly IReadOnlyDictionary<Guid, int> rawDeliveriesByEventId;

    private Ops001RealtimeCorrelation(
        int expected,
        int matched,
        int missing,
        int unexpected,
        int mismatched,
        int rawDeliveries,
        int duplicateDeliveries,
        decimal observationPercent,
        IReadOnlyDictionary<Guid, int> rawDeliveriesByEventId)
    {
        Expected = expected;
        Matched = matched;
        Missing = missing;
        Unexpected = unexpected;
        Mismatched = mismatched;
        RawDeliveries = rawDeliveries;
        DuplicateDeliveries = duplicateDeliveries;
        ObservationPercent = observationPercent;
        this.rawDeliveriesByEventId = rawDeliveriesByEventId;
    }

    internal int Expected { get; }
    internal int Matched { get; }
    internal int Missing { get; }
    internal int Unexpected { get; }
    internal int Mismatched { get; }
    internal int RawDeliveries { get; }
    internal int DuplicateDeliveries { get; }
    internal decimal ObservationPercent { get; }

    internal int RawDeliveriesFor(Guid eventId) =>
        rawDeliveriesByEventId.GetValueOrDefault(eventId);

    internal static Ops001RealtimeCorrelation Correlate(
        IEnumerable<Ops001RealtimeExpectation> expectations,
        IEnumerable<Ops001RealtimeDelivery> deliveries)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(deliveries);

        var expected = expectations.ToArray();
        if (expected.Length == 0)
        {
            throw new ArgumentException(
                "At least one realtime expectation is required.",
                nameof(expectations));
        }

        var duplicateExpectation = expected
            .GroupBy(value => value.EventId)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateExpectation is not null)
        {
            throw new ArgumentException(
                "Realtime expectations must contain unique event identifiers.",
                nameof(expectations));
        }

        var expectedById = expected.ToDictionary(value => value.EventId);
        var deliveredById = deliveries
            .GroupBy(value => value.EventId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var matched = 0;
        var missing = 0;
        var mismatched = 0;
        foreach (var expectation in expected)
        {
            if (!deliveredById.TryGetValue(expectation.EventId, out var observed))
            {
                missing++;
                continue;
            }

            var exact = observed.Count(delivery =>
                delivery.AggregateId == expectation.OrderId &&
                delivery.AggregateVersion == expectation.AggregateVersion &&
                string.Equals(
                    delivery.EventType,
                    expectation.EventType,
                    StringComparison.Ordinal));
            mismatched += observed.Length - exact;
            if (exact == 0)
            {
                missing++;
            }
            else
            {
                matched++;
            }
        }

        var unexpected = deliveredById.Keys.Count(id => !expectedById.ContainsKey(id));
        var rawById = deliveredById.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Length);
        var raw = rawById.Values.Sum();
        var duplicateDeliveries = rawById.Values.Sum(count => Math.Max(0, count - 1));
        return new(
            expected.Length,
            matched,
            missing,
            unexpected,
            mismatched,
            raw,
            duplicateDeliveries,
            Math.Round(
                matched * 100m / expected.Length,
                2,
                MidpointRounding.AwayFromZero),
            rawById);
    }
}

internal sealed record Ops001AuditEvidence(
    Guid OperationId,
    Guid OrganizationId,
    Guid OrderId,
    Guid EntityId,
    string EntityType,
    string Action,
    int? AggregateVersion,
    DateTimeOffset? OccurredAt);

internal sealed record Ops001AuditCorrelation(
    int Expected,
    int ExactlyMatched,
    int Missing,
    int Duplicated,
    int Mismatched)
{
    internal static Ops001AuditCorrelation Correlate(
        IEnumerable<Ops001AuditEvidence> expectations,
        IEnumerable<Ops001AuditEvidence> observations)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(observations);

        var expected = expectations.ToArray();
        if (expected.Length == 0)
        {
            throw new ArgumentException(
                "At least one audit expectation is required.",
                nameof(expectations));
        }

        var expectedGroups = expected.GroupBy(EvidenceKey).ToArray();
        if (expectedGroups.Any(group => group.Count() != 1))
        {
            throw new ArgumentException(
                "Audit expectations must identify unique operations.",
                nameof(expectations));
        }

        var expectedKeys = expectedGroups
            .Select(group => group.Key)
            .ToHashSet();
        var observedGroups = observations
            .GroupBy(EvidenceKey)
            .ToDictionary(group => group.Key, group => group.Count());
        var exactlyMatched = 0;
        var missing = 0;
        var duplicated = 0;
        foreach (var key in expectedKeys)
        {
            var count = observedGroups.GetValueOrDefault(key);
            if (count == 0)
            {
                missing++;
            }
            else if (count == 1)
            {
                exactlyMatched++;
            }
            else
            {
                duplicated += count - 1;
            }
        }

        var mismatched = observedGroups
            .Where(pair => !expectedKeys.Contains(pair.Key))
            .Sum(pair => pair.Value);
        return new(
            expected.Length,
            exactlyMatched,
            missing,
            duplicated,
            mismatched);
    }

    private static AuditEvidenceKey EvidenceKey(Ops001AuditEvidence evidence) =>
        new(
            evidence.OperationId,
            evidence.OrganizationId,
            evidence.OrderId,
            evidence.EntityId,
            evidence.EntityType,
            evidence.Action,
            evidence.AggregateVersion,
            evidence.OccurredAt);

    private sealed record AuditEvidenceKey(
        Guid OperationId,
        Guid OrganizationId,
        Guid OrderId,
        Guid EntityId,
        string EntityType,
        string Action,
        int? AggregateVersion,
        DateTimeOffset? OccurredAt);
}
