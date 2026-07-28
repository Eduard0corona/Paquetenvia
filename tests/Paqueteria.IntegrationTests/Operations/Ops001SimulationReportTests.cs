namespace Paqueteria.IntegrationTests.Operations;

public sealed class Ops001SimulationReportTests
{
    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Report_rejects_missing_orders_proofs_audits_and_versions()
    {
        Assert.Throws<Ops001AcceptanceException>(() =>
            Reject(Accepted() with { OrdersDelivered = 19 }));
        Assert.Throws<Ops001AcceptanceException>(() =>
            Reject(Accepted() with { PickupProofsCompleted = 19 }));
        Assert.Throws<Ops001AcceptanceException>(() =>
            Reject(Accepted() with { DeliveryProofsCompleted = 19 }));
        Assert.Throws<Ops001AcceptanceException>(() =>
            Reject(Accepted() with { AuditsPersisted = 339 }));
        Assert.Throws<Ops001AcceptanceException>(() =>
            Reject(Accepted() with { MissingAggregateVersions = 1 }));
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Observation_percentage_deduplicates_event_ids()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");

        Assert.Equal(50m, Ops001SimulationReport.ObservationPercentage(
            4,
            [first, first, second, second]));
        Assert.Equal(100m, Ops001SimulationReport.ObservationPercentage(
            2,
            [first, first, second, second]));
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Missing_version_is_detected_even_when_observation_exceeds_98_percent()
    {
        var order = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var other = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var versions = Enumerable.Range(1, 100)
            .Where(version => version != 50)
            .Select(version => (order, version))
            .Append((other, 1));

        Assert.Equal(
            1,
            Ops001SimulationReport.CountMissingVersions(
                versions,
                new Dictionary<Guid, int> { [order] = 100, [other] = 1 }));
        Assert.Equal(
            99m,
            Ops001SimulationReport.ObservationPercentage(
                100,
                Enumerable.Range(1, 99).Select(index =>
                    Guid.Parse($"00000000-0000-0000-0000-{index:D12}"))));
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Report_has_no_sensitive_fields_and_rejects_secondary_tenant_contamination()
    {
        var report = Accepted();
        report.EnsureAccepted();
        Ops001SimulationReport.AssertRedacted(report.ToJson());
        Assert.DoesNotContain("token", report.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<Ops001AcceptanceException>(() =>
            Reject(report with { SecondaryTenantRows = 1 }));
        Assert.Throws<Ops001AcceptanceException>(() =>
            Ops001SimulationReport.AssertRedacted("""{"signed_url":"redacted"}"""));
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Consecutive_scenario_data_sets_do_not_collide()
    {
        var first = Ops001ScenarioData.Create(1);
        var second = Ops001ScenarioData.Create(2);
        var firstIds = ScenarioIds(first).ToHashSet();
        var secondIds = ScenarioIds(second).ToHashSet();

        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.Equal(first.DispatcherUserId, second.DispatcherUserId);
        Assert.Equal(
            first.IdempotencyKey("create-order", 1),
            first.IdempotencyKey("create-order", 1));
        Assert.NotEqual(
            first.IdempotencyKey("create-order", 1),
            first.IdempotencyKey("create-order", 2));
    }

    private static Ops001SimulationReport? Reject(Ops001SimulationReport report)
    {
        report.EnsureAccepted();
        return null;
    }

    private static IEnumerable<Guid> ScenarioIds(Ops001ScenarioData data) =>
        new[]
        {
            data.OrganizationId,
            data.DecoyOrganizationId,
            data.CityId,
            data.ServiceAreaId,
            data.OperatingZoneId,
            data.TariffRuleId,
        }.Concat(data.DriverUserIds).Concat(data.DriverIds);

    private static Ops001SimulationReport Accepted() => new(
        OrdersPlanned: 20,
        OrdersCreated: 20,
        OrdersDelivered: 20,
        AssignmentsCreated: 20,
        PickupProofsExpected: 20,
        PickupProofsCompleted: 20,
        DeliveryProofsExpected: 20,
        DeliveryProofsCompleted: 20,
        DomainEventsExpected: 180,
        DomainEventsPersisted: 180,
        RealtimeEventsExpected: 160,
        RealtimeEventsObserved: 160,
        RealtimeObservationPercent: 100m,
        AuditsExpected: 340,
        AuditsPersisted: 340,
        OutboxProcessed: 360,
        OutboxDeadExpected: 1,
        OutboxDeadActual: 1,
        StaleRecoveries: 1,
        StaleLeaseRejections: 1,
        MissingAggregateVersions: 0,
        SecondaryTenantRows: 0,
        NewerMessageProcessedAfterPoison: true,
        Duration: TimeSpan.FromSeconds(1).ToString("c"));
}
