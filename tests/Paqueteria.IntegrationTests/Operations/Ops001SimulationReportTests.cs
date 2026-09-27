namespace Paqueteria.IntegrationTests.Operations;

public sealed class Ops001SimulationReportTests
{
    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Report_rejects_missing_orders_proofs_audits_and_versions()
    {
        AssertRejected(Accepted() with { OrdersDelivered = 19 });
        AssertRejected(Accepted() with { PickupProofsCompleted = 19 });
        AssertRejected(Accepted() with { DeliveryProofsCompleted = 19 });
        AssertRejected(Accepted() with { AuditsExactlyMatched = 339, AuditsMissing = 1 });
        AssertRejected(Accepted() with { MissingAggregateVersions = 1 });
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Exact_realtime_expectation_set_passes()
    {
        var expected = RealtimeExpectations(2);
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            expected.Select(Delivery));

        Assert.Equal(2, result.Expected);
        Assert.Equal(2, result.Matched);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Unexpected);
        Assert.Equal(0, result.Mismatched);
        Assert.Equal(100m, result.ObservationPercent);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Realtime_duplicates_do_not_inflate_functional_percentage()
    {
        var expected = RealtimeExpectations(2);
        var deliveries = expected
            .SelectMany(value => new[] { Delivery(value), Delivery(value) });
        var result = Ops001RealtimeCorrelation.Correlate(expected, deliveries);

        Assert.Equal(2, result.Matched);
        Assert.Equal(4, result.RawDeliveries);
        Assert.Equal(2, result.DuplicateDeliveries);
        Assert.Equal(100m, result.ObservationPercent);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Realtime_unexpected_id_does_not_compensate_missing_expected_id()
    {
        var expected = RealtimeExpectations(2);
        var unexpected = Delivery(expected[1]) with { EventId = Id(99) };
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            [Delivery(expected[0]), unexpected]);

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Unexpected);
        Assert.Equal(50m, result.ObservationPercent);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Realtime_expected_id_with_wrong_aggregate_fails_exact_match()
    {
        var expected = RealtimeExpectations(1);
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            [Delivery(expected[0]) with { AggregateId = Id(88) }]);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Mismatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Realtime_expected_id_with_wrong_version_fails_exact_match()
    {
        var expected = RealtimeExpectations(1);
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            [Delivery(expected[0]) with { AggregateVersion = 40 }]);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Mismatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Realtime_expected_id_with_wrong_event_type_fails_exact_match()
    {
        var expected = RealtimeExpectations(1);
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            [Delivery(expected[0]) with { EventType = "OrderTimelineEventAdded.v1" }]);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Mismatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Additional_unexpected_realtime_event_stays_outside_numerator()
    {
        var expected = RealtimeExpectations(1);
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            [
                Delivery(expected[0]),
                Delivery(expected[0]) with { EventId = Id(77) },
            ]);

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Unexpected);
        Assert.Equal(100m, result.ObservationPercent);
        AssertRejected(Accepted() with { RealtimeEventsUnexpected = 1 });
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Interrupted_realtime_redelivery_counts_once_functionally_and_twice_raw()
    {
        var expected = RealtimeExpectations(1);
        var result = Ops001RealtimeCorrelation.Correlate(
            expected,
            [Delivery(expected[0]), Delivery(expected[0])]);

        Assert.Equal(1, result.Matched);
        Assert.Equal(2, result.RawDeliveriesFor(expected[0].EventId));
        Assert.Equal(1, result.DuplicateDeliveries);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Missing_realtime_version_is_rejected_even_above_98_percent()
    {
        var order = Id(500);
        var persisted = Enumerable.Range(1, 100)
            .Where(version => version != 50)
            .Select(version => (order, version));

        Assert.Equal(
            1,
            Ops001SimulationReport.CountMissingVersions(
                persisted,
                new Dictionary<Guid, int> { [order] = 100 }));
        Assert.Equal(99m, Ops001SimulationReport.ObservationPercentage(100, 99));
        AssertRejected(Accepted() with
        {
            RealtimeEventsMatched = 159,
            RealtimeEventsMissing = 1,
            RealtimeObservationPercent = 99.38m,
        });
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Exact_audit_expectation_set_passes()
    {
        var expected = new[]
        {
            Audit(1, "ORDER_CREATED"),
            Audit(2, "TRACKING_TOKEN_ISSUED"),
        };
        var result = Ops001AuditCorrelation.Correlate(expected, expected);

        Assert.Equal(2, result.Expected);
        Assert.Equal(2, result.ExactlyMatched);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Duplicated);
        Assert.Equal(0, result.Mismatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Missing_and_duplicated_audits_are_rejected_independently()
    {
        var expected = new[]
        {
            Audit(1, "ORDER_CREATED"),
            Audit(2, "TRACKING_TOKEN_ISSUED"),
        };

        var missing = Ops001AuditCorrelation.Correlate(expected, [expected[0]]);
        Assert.Equal(1, missing.Missing);

        var duplicated = Ops001AuditCorrelation.Correlate(
            expected,
            [expected[0], expected[0], expected[1]]);
        Assert.Equal(1, duplicated.Duplicated);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Duplicated_audit_type_does_not_compensate_missing_other_type()
    {
        var expected = new[]
        {
            Audit(1, "ORDER_CREATED"),
            Audit(2, "TRACKING_TOKEN_ISSUED"),
        };
        var result = Ops001AuditCorrelation.Correlate(
            expected,
            [expected[0], expected[0]]);

        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Duplicated);
        Assert.Equal(0, result.ExactlyMatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Seven_transitions_and_one_duplicate_do_not_satisfy_eight()
    {
        var expected = Enumerable.Range(2, 8)
            .Select(version => Audit(
                version,
                "ORDER_STATUS_CHANGED",
                aggregateVersion: version))
            .ToArray();
        var observations = expected.Take(7).Append(expected[0]);
        var result = Ops001AuditCorrelation.Correlate(expected, observations);

        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Duplicated);
        Assert.Equal(6, result.ExactlyMatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Audit_from_other_tenant_or_entity_is_mismatched_and_missing()
    {
        var expected = Audit(1, "ASSIGNMENT_CREATED");
        var wrongTenant = expected with { OrganizationId = Id(91) };
        var wrongEntity = expected with { EntityId = Id(92) };

        var tenantResult = Ops001AuditCorrelation.Correlate([expected], [wrongTenant]);
        Assert.Equal(1, tenantResult.Missing);
        Assert.Equal(1, tenantResult.Mismatched);

        var entityResult = Ops001AuditCorrelation.Correlate([expected], [wrongEntity]);
        Assert.Equal(1, entityResult.Missing);
        Assert.Equal(1, entityResult.Mismatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Every_pod_session_and_proof_requires_its_own_exact_audits()
    {
        var expected = new[]
        {
            Audit(10, "custody.proof_upload_session.created"),
            Audit(10, "custody.proof_upload_session.ready"),
            Audit(11, "custody.proof_upload_session.created"),
            Audit(11, "custody.proof_upload_session.ready"),
            Audit(12, "custody.proof.finalized"),
            Audit(13, "custody.proof.finalized"),
        };
        var result = Ops001AuditCorrelation.Correlate(expected, expected);

        Assert.Equal(expected.Length, result.Expected);
        Assert.Equal(expected.Length, result.ExactlyMatched);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.Duplicated);
        Assert.Equal(0, result.Mismatched);
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Audit_report_counts_derive_from_exact_correlation()
    {
        var expected = new[]
        {
            Audit(1, "ORDER_CREATED"),
            Audit(2, "ORDER_STATUS_CHANGED", aggregateVersion: 2),
            Audit(3, "TRACKING_TOKEN_ISSUED"),
        };
        var result = Ops001AuditCorrelation.Correlate(expected, expected);

        Assert.Equal(expected.Length, result.Expected);
        Assert.Equal(expected.Length, result.ExactlyMatched);
        AssertRejected(Accepted() with { AuditsDuplicated = 1 });
        AssertRejected(Accepted() with { AuditsMismatched = 1 });
    }

    [Fact]
    [Trait("Category", "OpsDeliverySimulation")]
    public void Report_has_no_sensitive_fields_and_rejects_secondary_tenant_contamination()
    {
        var report = Accepted();
        report.EnsureAccepted();
        Ops001SimulationReport.AssertRedacted(report.ToJson());
        Assert.DoesNotContain("token", report.ToJson(), StringComparison.OrdinalIgnoreCase);
        AssertRejected(report with { SecondaryTenantRows = 1 });
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

    private static void AssertRejected(Ops001SimulationReport report) =>
        Assert.Throws<Ops001AcceptanceException>(report.EnsureAccepted);

    private static Ops001RealtimeExpectation[] RealtimeExpectations(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new Ops001RealtimeExpectation(
                Id(index),
                Id(100 + index),
                index + 1,
                "OrderStatusChanged.v1"))
            .ToArray();

    private static Ops001RealtimeDelivery Delivery(Ops001RealtimeExpectation expected) =>
        new(
            expected.EventId,
            expected.OrderId,
            expected.AggregateVersion,
            expected.EventType);

    private static Ops001AuditEvidence Audit(
        int operationSeed,
        string action,
        int? aggregateVersion = null) =>
        new(
            Id(operationSeed),
            Id(800),
            Id(801),
            Id(operationSeed),
            "Synthetic",
            action,
            aggregateVersion,
            DateTimeOffset.UnixEpoch.AddSeconds(operationSeed));

    private static Guid Id(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

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
        RealtimeEventsMatched: 160,
        RealtimeEventsMissing: 0,
        RealtimeEventsUnexpected: 0,
        RealtimeEventsMismatched: 0,
        RealtimeRawDeliveries: 161,
        RealtimeDuplicateDeliveries: 1,
        RealtimeObservationPercent: 100m,
        AuditsExpected: 340,
        AuditsExactlyMatched: 340,
        AuditsMissing: 0,
        AuditsDuplicated: 0,
        AuditsMismatched: 0,
        OutboxProcessed: 380,
        OutboxDeadExpected: 1,
        OutboxDeadActual: 1,
        StaleRecoveries: 1,
        StaleLeaseRejections: 1,
        MissingAggregateVersions: 0,
        SecondaryTenantRows: 0,
        NewerMessageProcessedAfterPoison: true,
        Duration: TimeSpan.FromSeconds(1).ToString("c"));
}
