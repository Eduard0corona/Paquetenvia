using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paqueteria.IntegrationTests.Operations;

internal sealed record Ops001SimulationReport(
    [property: JsonPropertyName("orders_planned")] int OrdersPlanned,
    [property: JsonPropertyName("orders_created")] int OrdersCreated,
    [property: JsonPropertyName("orders_delivered")] int OrdersDelivered,
    [property: JsonPropertyName("assignments_created")] int AssignmentsCreated,
    [property: JsonPropertyName("pickup_proofs_expected")] int PickupProofsExpected,
    [property: JsonPropertyName("pickup_proofs_completed")] int PickupProofsCompleted,
    [property: JsonPropertyName("delivery_proofs_expected")] int DeliveryProofsExpected,
    [property: JsonPropertyName("delivery_proofs_completed")] int DeliveryProofsCompleted,
    [property: JsonPropertyName("domain_events_expected")] int DomainEventsExpected,
    [property: JsonPropertyName("domain_events_persisted")] int DomainEventsPersisted,
    [property: JsonPropertyName("realtime_events_expected")] int RealtimeEventsExpected,
    [property: JsonPropertyName("realtime_events_observed")] int RealtimeEventsObserved,
    [property: JsonPropertyName("realtime_observation_percent")] decimal RealtimeObservationPercent,
    [property: JsonPropertyName("audits_expected")] int AuditsExpected,
    [property: JsonPropertyName("audits_persisted")] int AuditsPersisted,
    [property: JsonPropertyName("outbox_processed")] int OutboxProcessed,
    [property: JsonPropertyName("outbox_dead_expected")] int OutboxDeadExpected,
    [property: JsonPropertyName("outbox_dead_actual")] int OutboxDeadActual,
    [property: JsonPropertyName("stale_recoveries")] int StaleRecoveries,
    [property: JsonPropertyName("stale_lease_rejections")] int StaleLeaseRejections,
    [property: JsonPropertyName("missing_aggregate_versions")] int MissingAggregateVersions,
    [property: JsonPropertyName("secondary_tenant_rows")] int SecondaryTenantRows,
    [property: JsonPropertyName("newer_message_processed_after_poison")] bool NewerMessageProcessedAfterPoison,
    [property: JsonPropertyName("duration")] string Duration)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal void EnsureAccepted()
    {
        Require(OrdersPlanned == Ops001ScenarioData.OrderCount, "orders_planned");
        Require(OrdersCreated == OrdersPlanned, "orders_created");
        Require(OrdersDelivered == OrdersPlanned, "orders_delivered");
        Require(AssignmentsCreated == OrdersPlanned, "assignments_created");
        Require(PickupProofsExpected == OrdersPlanned, "pickup_proofs_expected");
        Require(PickupProofsCompleted == PickupProofsExpected, "pickup_proofs_completed");
        Require(DeliveryProofsExpected == OrdersPlanned, "delivery_proofs_expected");
        Require(DeliveryProofsCompleted == DeliveryProofsExpected, "delivery_proofs_completed");
        Require(DomainEventsExpected == 180, "domain_events_expected");
        Require(DomainEventsPersisted == DomainEventsExpected, "domain_events_persisted");
        Require(RealtimeEventsExpected == 160, "realtime_events_expected");
        Require(RealtimeEventsObserved <= RealtimeEventsExpected, "realtime_events_observed");
        Require(RealtimeObservationPercent >= 98m, "realtime_observation_percent");
        Require(AuditsExpected == 340, "audits_expected");
        Require(AuditsPersisted == AuditsExpected, "audits_persisted");
        Require(OutboxProcessed == 360, "outbox_processed");
        Require(OutboxDeadExpected == 1, "outbox_dead_expected");
        Require(OutboxDeadActual == OutboxDeadExpected, "outbox_dead_actual");
        Require(StaleRecoveries >= 1, "stale_recoveries");
        Require(StaleLeaseRejections >= 1, "stale_lease_rejections");
        Require(MissingAggregateVersions == 0, "missing_aggregate_versions");
        Require(SecondaryTenantRows == 0, "secondary_tenant_rows");
        Require(NewerMessageProcessedAfterPoison, "newer_message_processed_after_poison");
        Require(TimeSpan.TryParse(Duration, out var duration) && duration > TimeSpan.Zero, "duration");
        AssertRedacted(ToJson());
    }

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    internal static decimal ObservationPercentage(
        int expected,
        IEnumerable<Guid> observedEventIds)
    {
        if (expected <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expected));
        }

        var observed = observedEventIds.Distinct().Count();
        return Math.Round(
            Math.Min(observed, expected) * 100m / expected,
            2,
            MidpointRounding.AwayFromZero);
    }

    internal static int CountMissingVersions(
        IEnumerable<(Guid OrderId, int Version)> persistedVersions,
        IReadOnlyDictionary<Guid, int> expectedMaximumVersions)
    {
        var actual = persistedVersions
            .GroupBy(value => value.OrderId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.Version).Distinct().Order().ToArray());
        var missing = 0;
        foreach (var expected in expectedMaximumVersions)
        {
            actual.TryGetValue(expected.Key, out var versions);
            versions ??= [];
            missing += Enumerable.Range(1, expected.Value).Except(versions).Count();
        }

        return missing;
    }

    internal static void AssertRedacted(string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertRedacted(document.RootElement);
    }

    private static void AssertRedacted(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("object_key", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("sha", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("connection", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Ops001AcceptanceException(
                        $"The report contains the sensitive field '{name}'.");
                }

                AssertRedacted(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertRedacted(item);
            }
        }
    }

    private static void Require(bool condition, string metric)
    {
        if (!condition)
        {
            throw new Ops001AcceptanceException(
                $"OPS-001 acceptance failed for metric '{metric}'.");
        }
    }
}

internal sealed class Ops001AcceptanceException(string message) : Exception(message);
