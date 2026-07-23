using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
public sealed class OrdersTransitionPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Every_normative_edge_commits_exactly_one_version_event_outbox_audit_and_idempotency()
    {
        foreach (var (source, target) in ExpectedEdges())
        {
            await using var scenario = new SyntheticOrderScenario(fixture);
            await scenario.InitializeAsync(source.ToContractValue());
            var metadata = await PrepareGuardFixturesAsync(scenario, source, target);
            await using var scope = CreateScope();
            var key = $"ord002-edge-{(int)source:D2}-{(int)target:D2}-0001";

            var result = await scope.Service.TransitionAsync(
                Command(
                    scenario,
                    target,
                    1,
                    key,
                    metadata,
                    "hidden@example.test Avenida Universidad 1234 token=super-secret"),
                CancellationToken.None);

            Assert.Equal(target.ToContractValue(), result.Status);
            Assert.Equal(2, result.Version);
            if (target == OrderStatus.Delivered)
            {
                Assert.NotNull(result.ClaimWindowEndsAt);
            }

            await using var verify = fixture.AdminDataSource.CreateCommand(
                """
                SELECT o.status,o.version,
                  (SELECT count(*) FROM orders.order_events e
                    WHERE e.order_id=o.id AND e.aggregate_version=2 AND e.event_type='ORDER_STATUS_CHANGED'),
                  (SELECT public_event_code FROM orders.order_events e
                    WHERE e.order_id=o.id AND e.aggregate_version=2),
                  (SELECT count(*) FROM platform.outbox_events x
                    WHERE x.aggregate_id=o.id AND x.aggregate_version=2
                      AND x.topic='orders.status-changed' AND x.status='PENDING' AND x.attempts=0),
                  (SELECT count(*) FROM platform.audit_logs a
                    WHERE a.entity_id=o.id AND a.action='ORDER_STATUS_CHANGED'),
                  (SELECT count(*) FROM platform.idempotency_keys i
                    WHERE i.owner_org_id=o.owner_org_id AND i.scope='ORD-002:TRANSITION_ORDER'
                      AND i.idempotency_key=@key AND i.response_status=200),
                  (SELECT payload::text FROM orders.order_events e
                    WHERE e.order_id=o.id AND e.aggregate_version=2),
                  (SELECT payload::text FROM platform.outbox_events x
                    WHERE x.aggregate_id=o.id AND x.aggregate_version=2),
                  (SELECT payload_redacted::text FROM platform.audit_logs a
                    WHERE a.entity_id=o.id AND a.action='ORDER_STATUS_CHANGED')
                FROM orders.orders o WHERE o.id=@order;
                """);
            verify.Parameters.AddWithValue("order", scenario.OrderId);
            verify.Parameters.AddWithValue("key", key);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(target.ToContractValue(), reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(OrderPublicEventCodePolicy.Map(target), reader.IsDBNull(3) ? null : reader.GetString(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal(1L, reader.GetInt64(5));
            Assert.Equal(1L, reader.GetInt64(6));
            foreach (var ordinal in new[] { 7, 8, 9 })
            {
                var json = reader.GetString(ordinal);
                Assert.DoesNotContain("hidden@example.test", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Universidad 1234", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("super-secret", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("cipher", json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Prohibited_terminal_claim_window_and_retry_guards_fail_without_partial_rows()
    {
        var cases = new[]
        {
            (OrderStatus.FailedAttempt, OrderStatus.Delivered, (string?)null, (DateTimeOffset?)null),
            (OrderStatus.Rescheduled, OrderStatus.Delivering, (string?)null, (DateTimeOffset?)null),
            (OrderStatus.Cancelled, OrderStatus.Confirmed,
                """{"restricted_goods_acknowledged":true}""", (DateTimeOffset?)null),
            (OrderStatus.Closed, OrderStatus.ClaimOpen, (string?)null, DateTimeOffset.UtcNow.AddHours(-1)),
        };

        foreach (var (source, target, metadata, claimWindow) in cases)
        {
            await using var scenario = new SyntheticOrderScenario(fixture);
            await scenario.InitializeAsync(source.ToContractValue());
            if (claimWindow is not null)
            {
                await scenario.ExecuteAdminAsync(
                    "UPDATE orders.orders SET claim_window_ends_at=@window WHERE id=@order;",
                    SyntheticOrderScenario.P("window", claimWindow.Value),
                    SyntheticOrderScenario.P("order", scenario.OrderId));
            }

            await using var scope = CreateScope();
            await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(
                    Command(scenario, target, 1, Key(), metadata),
                    CancellationToken.None));
            await AssertNoTransitionArtifactsAsync(scenario, source);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Replay_hash_conflict_and_optimistic_concurrency_are_serialized()
    {
        await using (var replayScenario = new SyntheticOrderScenario(fixture))
        {
            await replayScenario.InitializeAsync();
            await using var first = CreateScope();
            await using var second = CreateScope();
            var command = Command(replayScenario, OrderStatus.Cancelled, 1, "ord002-replay-key-0001");
            var results = await Task.WhenAll(
                first.Service.TransitionAsync(command, CancellationToken.None),
                second.Service.TransitionAsync(command, CancellationToken.None));
            Assert.Equal(results[0], results[1]);
            await AssertArtifactCountsAsync(replayScenario, 1, 1, 1, 1);

            var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                first.Service.TransitionAsync(
                    command with { Reason = "changed exact reason" },
                    CancellationToken.None));
            Assert.Equal(OrderTransitionConflictCode.IdempotencyConflict, conflict.Code);
        }

        await using (var concurrencyScenario = new SyntheticOrderScenario(fixture))
        {
            await concurrencyScenario.InitializeAsync();
            await using var first = CreateScope();
            await using var second = CreateScope();
            var outcomes = await Task.WhenAll(
                TryTransitionAsync(first.Service, Command(
                    concurrencyScenario, OrderStatus.Cancelled, 1, "ord002-distinct-key-0001")),
                TryTransitionAsync(second.Service, Command(
                    concurrencyScenario, OrderStatus.Cancelled, 1, "ord002-distinct-key-0002")));
            Assert.Equal(1, outcomes.Count(value => value));
            Assert.Equal(1, outcomes.Count(value => !value));
            await AssertArtifactCountsAsync(concurrencyScenario, 1, 1, 1, 1);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Completed_replay_uses_original_event_and_current_authorization_without_new_effects()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await using var otherActor = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync();
        await otherActor.InitializeAsync(createOrder: false);
        await PrepareConfirmationAsync(scenario);
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default)
            VALUES (gen_random_uuid(),@user,@org,'DISPATCHER','ACTIVE',false);
            """,
            SyntheticOrderScenario.P("user", otherActor.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));

        await using var scope = CreateScope();
        var original = Command(
            scenario,
            OrderStatus.Confirmed,
            1,
            "ord002-authorized-replay-0001",
            """{"restricted_goods_acknowledged":true}""");
        var stored = await scope.Service.TransitionAsync(original, CancellationToken.None);
        var advanced = await scope.Service.TransitionAsync(
            Command(scenario, OrderStatus.ReadyForPickup, 2, "ord002-advance-0001"),
            CancellationToken.None);
        Assert.Equal("READY_FOR_PICKUP", advanced.Status);
        Assert.Equal(3, advanced.Version);

        await scenario.ExecuteAdminAsync(
            "UPDATE pricing.quotes SET status='ACTIVE',consumed_at=NULL WHERE id=@quote;",
            SyntheticOrderScenario.P("quote", scenario.QuoteId));

        var replayed = await scope.Service.TransitionAsync(
            original with { ActorId = otherActor.UserId },
            CancellationToken.None);
        Assert.Equal(stored, replayed);

        await scenario.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships
            SET role='VIEWER'
            WHERE user_id=@user AND organization_id=@org;
            """,
            SyntheticOrderScenario.P("user", otherActor.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(
                original with { ActorId = otherActor.UserId },
                CancellationToken.None));

        await scenario.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships
            SET role='PLATFORM_ADMIN'
            WHERE user_id=@user AND organization_id=@org;
            """,
            SyntheticOrderScenario.P("user", otherActor.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(
                original with { ActorId = otherActor.UserId, MfaSatisfied = false },
                CancellationToken.None));

        await AssertArtifactCountsAsync(scenario, 2, 2, 2, 2);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Completed_driver_replay_requires_current_exact_active_assignment()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await using var foreign = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(OrderStatus.Assigned.ToContractValue());
        await foreign.InitializeAsync();
        await using var scope = CreateScope();
        var command = Command(
            scenario,
            OrderStatus.AtPickup,
            1,
            "ord002-driver-replay-0001");
        var stored = await scope.Service.TransitionAsync(command, CancellationToken.None);
        await scenario.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships
            SET role='DRIVER'
            WHERE user_id=@user AND organization_id=@org;
            """,
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));

        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(command, CancellationToken.None));

        var otherDriver = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@other_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents)
            VALUES (gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',100);
            """,
            SyntheticOrderScenario.P("driver", otherDriver),
            SyntheticOrderScenario.P("other_user", foreign.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("city", scenario.CityId),
            SyntheticOrderScenario.P("order", scenario.OrderId));
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(command, CancellationToken.None));
        await DeleteSyntheticDriverAsync(scenario, otherDriver);

        var otherOrderDriver = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents)
            VALUES (gen_random_uuid(),@other_order,@other_org,@driver,'OWN','ACTIVE',100);
            """,
            SyntheticOrderScenario.P("driver", otherOrderDriver),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("city", scenario.CityId),
            SyntheticOrderScenario.P("other_order", foreign.OrderId),
            SyntheticOrderScenario.P("other_org", foreign.OrganizationId));
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(command, CancellationToken.None));
        await DeleteSyntheticDriverAsync(scenario, otherOrderDriver);

        var crossTenantDriver = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@foreign_org,@foreign_city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents)
            VALUES (gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',100);
            """,
            SyntheticOrderScenario.P("driver", crossTenantDriver),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("foreign_org", foreign.OrganizationId),
            SyntheticOrderScenario.P("foreign_city", foreign.CityId),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(command, CancellationToken.None));
        await DeleteSyntheticDriverAsync(scenario, crossTenantDriver);

        await InsertAssignmentAsync(scenario);
        Assert.Equal(
            stored,
            await scope.Service.TransitionAsync(command, CancellationToken.None));

        await scenario.ExecuteAdminAsync(
            "UPDATE dispatch.assignments SET status='CANCELLED' WHERE order_id=@order;",
            SyntheticOrderScenario.P("order", scenario.OrderId));
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            scope.Service.TransitionAsync(command, CancellationToken.None));
        await AssertArtifactCountsAsync(scenario, 1, 1, 1, 1);

        await using var prohibited = new SyntheticOrderScenario(fixture);
        await prohibited.InitializeAsync();
        await PrepareConfirmationAsync(prohibited);
        var prohibitedCommand = Command(
            prohibited,
            OrderStatus.Confirmed,
            1,
            "ord002-driver-prohibited-replay-0001",
            """{"restricted_goods_acknowledged":true}""");
        await using var prohibitedScope = CreateScope();
        await prohibitedScope.Service.TransitionAsync(prohibitedCommand, CancellationToken.None);
        await prohibited.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships
            SET role='DRIVER'
            WHERE user_id=@user AND organization_id=@org;
            """,
            SyntheticOrderScenario.P("user", prohibited.UserId),
            SyntheticOrderScenario.P("org", prohibited.OrganizationId));
        await InsertAssignmentAsync(prohibited);
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            prohibitedScope.Service.TransitionAsync(prohibitedCommand, CancellationToken.None));
        await AssertArtifactCountsAsync(prohibited, 1, 1, 1, 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Completed_replay_evidence_inconsistency_fails_closed_with_uniform_conflict()
    {
        var cases = new[]
        {
            (EventVersion: (int?)null, Previous: "DRAFT", New: "CANCELLED", ResourceMatches: true),
            (EventVersion: (int?)3, Previous: "DRAFT", New: "CANCELLED", ResourceMatches: true),
            (EventVersion: (int?)2, Previous: "DRAFT", New: "CONFIRMED", ResourceMatches: true),
            (EventVersion: (int?)2, Previous: "DRAFT", New: "CANCELLED", ResourceMatches: false),
        };

        foreach (var item in cases)
        {
            await using var scenario = new SyntheticOrderScenario(fixture);
            await scenario.InitializeAsync();
            var command = Command(
                scenario,
                OrderStatus.Cancelled,
                1,
                $"ord002-inconsistent-{Guid.NewGuid():N}");
            await InsertCompletedReplayFixtureAsync(
                scenario,
                command,
                item.EventVersion,
                item.Previous,
                item.New,
                item.ResourceMatches);
            await using var scope = CreateScope();

            var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(command, CancellationToken.None));
            Assert.Equal(OrderTransitionConflictCode.IdempotencyConflict, conflict.Code);
            await AssertArtifactCountsAsync(
                scenario,
                item.EventVersion is null ? 0 : 1,
                0,
                0,
                1);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Every_injected_stage_rolls_back_order_event_outbox_audit_and_idempotency()
    {
        foreach (var stage in Enum.GetValues<OrderTransitionStage>())
        {
            await using var scenario = new SyntheticOrderScenario(fixture);
            await scenario.InitializeAsync();
            await using var scope = CreateScope(new ThrowAtTransitionStage(stage));

            await Assert.ThrowsAsync<InjectedTransitionFailure>(() =>
                scope.Service.TransitionAsync(
                    Command(scenario, OrderStatus.Cancelled, 1, $"ord002-rollback-{(int)stage:D2}-0001"),
                    CancellationToken.None));
            await AssertNoTransitionArtifactsAsync(scenario, OrderStatus.Draft);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Confirmation_assignment_custody_incident_delivery_COD_close_and_claim_rules_use_real_rows()
    {
        await using (var confirmed = new SyntheticOrderScenario(fixture))
        {
            await confirmed.InitializeAsync();
            await PrepareConfirmationAsync(confirmed);
            await using var scope = CreateScope();
            var missing = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(
                    Command(confirmed, OrderStatus.Confirmed, 1, Key(), "{}"),
                    CancellationToken.None));
            Assert.Equal("restricted_goods_check", missing.GuardCode);
            var result = await scope.Service.TransitionAsync(
                Command(
                    confirmed,
                    OrderStatus.Confirmed,
                    1,
                    Key(),
                    """{"restricted_goods_acknowledged":true}"""),
                CancellationToken.None);
            Assert.Equal("CONFIRMED", result.Status);
        }

        await using (var custody = new SyntheticOrderScenario(fixture))
        {
            await custody.InitializeAsync(OrderStatus.AtPickup.ToContractValue());
            await InsertProofAsync(custody, "PICKUP_PHOTO");
            await using var scope = CreateScope();
            var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(
                    Command(custody, OrderStatus.Cancelled, 1, Key()),
                    CancellationToken.None));
            Assert.Equal("if_from_at_pickup_then_custody_not_acquired", conflict.GuardCode);
        }

        await using (var failed = new SyntheticOrderScenario(fixture))
        {
            await failed.InitializeAsync(OrderStatus.Delivering.ToContractValue());
            var incidentId = await InsertIncidentAsync(failed, custodyAcquired: true);
            await using var scope = CreateScope();
            var result = await scope.Service.TransitionAsync(
                Command(
                    failed,
                    OrderStatus.FailedAttempt,
                    1,
                    Key(),
                    $$"""{"incident_id":"{{incidentId:D}}"}"""),
                CancellationToken.None);
            Assert.Equal("FAILED_ATTEMPT", result.Status);
        }

        await using (var cod = new SyntheticOrderScenario(fixture))
        {
            await cod.InitializeAsync(OrderStatus.Delivering.ToContractValue());
            await cod.ExecuteAdminAsync(
                "UPDATE orders.orders SET cod_expected_cents=5000 WHERE id=@order;",
                SyntheticOrderScenario.P("order", cod.OrderId));
            await InsertProofAsync(cod, "DELIVERY_PHOTO");
            await using var scope = CreateScope();
            var missing = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(
                    Command(cod, OrderStatus.Delivered, 1, Key()),
                    CancellationToken.None));
            Assert.Equal("if_cod_expected_then_cod_status_recorded_or_reconciled", missing.GuardCode);

            await InsertCodAsync(cod, "RECORDED", 5_000);
            var delivered = await scope.Service.TransitionAsync(
                Command(cod, OrderStatus.Delivered, 1, Key()),
                CancellationToken.None);
            Assert.NotNull(delivered.ClaimWindowEndsAt);

            var closeConflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(
                    Command(cod, OrderStatus.Closed, 2, Key()),
                    CancellationToken.None));
            Assert.Equal("if_cod_expected_then_cod_status_reconciled", closeConflict.GuardCode);
            await cod.ExecuteAdminAsync(
                "UPDATE finance.cod_transactions SET status='RECONCILED',reconciled_at=clock_timestamp() WHERE order_id=@order;",
                SyntheticOrderScenario.P("order", cod.OrderId));
            var closed = await scope.Service.TransitionAsync(
                Command(cod, OrderStatus.Closed, 2, Key()),
                CancellationToken.None);
            Assert.Equal("CLOSED", closed.Status);
            Assert.Null(closed.FinalizedAt);
        }

        await using (var claim = new SyntheticOrderScenario(fixture))
        {
            await claim.InitializeAsync(OrderStatus.ClaimOpen.ToContractValue());
            await using var scope = CreateScope();
            var resolved = await scope.Service.TransitionAsync(
                Command(claim, OrderStatus.ClaimResolved, 1, Key(), reason: "synthetic resolution"),
                CancellationToken.None);
            Assert.NotNull(resolved.FinalizedAt);
            await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
                scope.Service.TransitionAsync(
                    Command(claim, OrderStatus.ClaimOpen, 2, Key(), reason: "again"),
                    CancellationToken.None));
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task RLS_hides_cross_tenant_order_and_foreign_guard_rows_and_cancellation_is_partial_row_free()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await using var foreign = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(OrderStatus.ReadyForPickup.ToContractValue());
        await foreign.InitializeAsync();
        var foreignDriver = Guid.NewGuid();
        await foreign.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents)
            VALUES (gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',100);
            """,
            SyntheticOrderScenario.P("driver", foreignDriver),
            SyntheticOrderScenario.P("user", foreign.UserId),
            SyntheticOrderScenario.P("org", foreign.OrganizationId),
            SyntheticOrderScenario.P("city", owner.CityId),
            SyntheticOrderScenario.P("order", owner.OrderId));

        await using var scope = CreateScope();
        var foreignGuard = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            scope.Service.TransitionAsync(
                Command(owner, OrderStatus.Assigned, 1, Key()),
                CancellationToken.None));
        Assert.Equal("eligible_driver", foreignGuard.GuardCode);

        var crossTenant = Command(owner, OrderStatus.Cancelled, 1, Key()) with
        {
            OrderId = foreign.OrderId,
        };
        var hidden = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            scope.Service.TransitionAsync(crossTenant, CancellationToken.None));
        Assert.Equal(OrderTransitionConflictCode.OrderUnavailable, hidden.Code);
        await AssertNoTransitionArtifactsAsync(foreign, OrderStatus.Draft);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Service.TransitionAsync(
                Command(owner, OrderStatus.Cancelled, 1, Key()),
                cancellationSource.Token));
        await AssertNoTransitionArtifactsAsync(owner, OrderStatus.ReadyForPickup);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task DRIVER_requires_exact_active_assignment_for_an_allowlisted_transition()
    {
        await using (var assigned = new SyntheticOrderScenario(fixture))
        {
            await assigned.InitializeAsync(OrderStatus.Assigned.ToContractValue());
            await assigned.ExecuteAdminAsync(
                """
                UPDATE organizations.organization_memberships
                SET role='DRIVER'
                WHERE user_id=@user AND organization_id=@org;
                """,
                SyntheticOrderScenario.P("user", assigned.UserId),
                SyntheticOrderScenario.P("org", assigned.OrganizationId));
            await using var scope = CreateScope();

            await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
                scope.Service.TransitionAsync(
                    Command(assigned, OrderStatus.AtPickup, 1, Key()),
                    CancellationToken.None));

            await InsertAssignmentAsync(assigned);
            var result = await scope.Service.TransitionAsync(
                Command(assigned, OrderStatus.AtPickup, 1, Key()),
                CancellationToken.None);
            Assert.Equal("AT_PICKUP", result.Status);
        }

        await using var foreignActor = new SyntheticOrderScenario(fixture);
        await using var mismatched = new SyntheticOrderScenario(fixture);
        await mismatched.InitializeAsync(OrderStatus.Assigned.ToContractValue());
        await foreignActor.InitializeAsync(createOrder: false);
        await mismatched.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships
            SET role='DRIVER'
            WHERE user_id=@user AND organization_id=@org;
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@foreign_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents)
            VALUES (gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',100);
            """,
            SyntheticOrderScenario.P("user", mismatched.UserId),
            SyntheticOrderScenario.P("foreign_user", foreignActor.UserId),
            SyntheticOrderScenario.P("org", mismatched.OrganizationId),
            SyntheticOrderScenario.P("driver", Guid.NewGuid()),
            SyntheticOrderScenario.P("city", mismatched.CityId),
            SyntheticOrderScenario.P("order", mismatched.OrderId));
        await using var mismatchedScope = CreateScope();
        await Assert.ThrowsAsync<OrderTransitionForbiddenException>(() =>
            mismatchedScope.Service.TransitionAsync(
                Command(mismatched, OrderStatus.AtPickup, 1, Key()),
                CancellationToken.None));
    }

    private TransitionScope CreateScope(IOrderTransitionFailureInjector? failureInjector = null)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(dbOptions, state);
        var service = new PostgreSqlOrderTransitionService(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new PostgreSqlOrderTransitionAuthorizationReader(),
            new PostgreSqlOrderTransitionReplayAuthorizationReader(),
            new PostgreSqlOrderQuoteAcceptanceGuardReader(),
            new PostgreSqlOrderAssignmentGuardReader(),
            new PostgreSqlOrderProofGuardReader(),
            new PostgreSqlOrderIncidentGuardReader(),
            new PostgreSqlOrderCodGuardReader(),
            new OrderTransitionAuthorizer(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            failureInjector ?? new NoOpOrderTransitionFailureInjector(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                IdempotencyLifetimeMinutes = 60,
                ClaimWindowHours = 72,
                TransitionMetadataMaximumBytes = 4_096,
            }),
            new SystemClock());
        return new TransitionScope(context, service);
    }

    private static TransitionOrderCommand Command(
        SyntheticOrderScenario scenario,
        OrderStatus target,
        int expectedVersion,
        string key,
        string? metadata = null,
        string reason = "synthetic reason") =>
        new(
            scenario.UserId,
            scenario.OrganizationId,
            key,
            scenario.OrderId,
            target.ToContractValue(),
            reason,
            expectedVersion,
            metadata,
            true,
            "synthetic-request-id");

    private async Task<string?> PrepareGuardFixturesAsync(
        SyntheticOrderScenario scenario,
        OrderStatus source,
        OrderStatus target)
    {
        if (source == OrderStatus.Draft && target == OrderStatus.Confirmed)
        {
            await PrepareConfirmationAsync(scenario);
            return """{"restricted_goods_acknowledged":true}""";
        }

        if (target == OrderStatus.Assigned ||
            (target == OrderStatus.Delivering &&
             source is OrderStatus.FailedAttempt or OrderStatus.Rescheduled))
        {
            await InsertAssignmentAsync(scenario);
        }

        if (target == OrderStatus.PickedUp ||
            target == OrderStatus.Returning ||
            (target == OrderStatus.Delivering &&
             source is OrderStatus.FailedAttempt or OrderStatus.Rescheduled))
        {
            await InsertProofAsync(scenario, "PICKUP_PHOTO");
        }

        if (target == OrderStatus.Delivered)
        {
            await InsertProofAsync(scenario, "DELIVERY_PHOTO");
        }

        if (target == OrderStatus.FailedAttempt)
        {
            var incidentId = await InsertIncidentAsync(scenario, custodyAcquired: false);
            return $$"""{"incident_id":"{{incidentId:D}}"}""";
        }

        if (target == OrderStatus.Closed || target == OrderStatus.ClaimOpen)
        {
            await scenario.ExecuteAdminAsync(
                "UPDATE orders.orders SET claim_window_ends_at=clock_timestamp()+interval '72 hours' WHERE id=@order;",
                SyntheticOrderScenario.P("order", scenario.OrderId));
        }

        return null;
    }

    private static async Task PrepareConfirmationAsync(SyntheticOrderScenario scenario)
    {
        await scenario.ExecuteAdminAsync(
            """
            UPDATE pricing.quotes SET status='USED',consumed_at=clock_timestamp() WHERE id=@quote;
            INSERT INTO orders.order_acceptances(
              id,order_id,quote_id,owner_org_id,actor_id,terms_version,privacy_version,
              accepted_at_client,recorded_at_server,acceptance_channel,evidence_schema_version,evidence_hash)
            VALUES (
              gen_random_uuid(),@order,@quote,@org,@user,'terms-synthetic-v1','privacy-synthetic-v1',
              clock_timestamp(),clock_timestamp(),'WEB','order-acceptance-v1',decode(repeat('01',32),'hex'));
            """,
            SyntheticOrderScenario.P("quote", scenario.QuoteId),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId));
    }

    private static async Task InsertAssignmentAsync(SyntheticOrderScenario scenario)
    {
        var driverId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents)
            VALUES (gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',100);
            """,
            SyntheticOrderScenario.P("driver", driverId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("city", scenario.CityId),
            SyntheticOrderScenario.P("order", scenario.OrderId));
    }

    private static Task DeleteSyntheticDriverAsync(
        SyntheticOrderScenario scenario,
        Guid driverId) =>
        scenario.ExecuteAdminAsync(
            """
            DELETE FROM dispatch.assignments WHERE driver_id=@driver;
            DELETE FROM drivers.driver_profiles WHERE id=@driver;
            """,
            SyntheticOrderScenario.P("driver", driverId));

    private static async Task InsertCompletedReplayFixtureAsync(
        SyntheticOrderScenario scenario,
        TransitionOrderCommand command,
        int? eventVersion,
        string previousStatus,
        string newStatus,
        bool resourceMatches)
    {
        var response = new OrderResult(
            scenario.OrderId,
            scenario.PublicOrderId,
            scenario.OrganizationId,
            null,
            OrderStatus.Cancelled.ToContractValue(),
            new MoneyResult("MXN", scenario.SubtotalCents - scenario.DiscountCents),
            2,
            scenario.OriginLocationId,
            scenario.DestinationLocationId,
            "SAME_DAY",
            scenario.QuoteId,
            scenario.CityId,
            null,
            "OCCASIONAL",
            new MoneyResult("MXN", scenario.TotalCents),
            null,
            null);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        };
        var responseBody = System.Text.Json.JsonSerializer.Serialize(response, jsonOptions);
        var requestHash = OrderTransitionCanonicalizer.ComputeSha256(
            command,
            OrderStatus.Cancelled,
            NormalizedTransitionMetadata.Empty);

        if (eventVersion is not null)
        {
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO orders.order_events(
                  id,order_id,owner_org_id,aggregate_version,event_type,payload,actor_id,occurred_at)
                VALUES (
                  gen_random_uuid(),@order,@org,@version,'ORDER_STATUS_CHANGED',
                  jsonb_build_object('previous_status',@previous,'new_status',@new),
                  @actor,clock_timestamp());
                """,
                SyntheticOrderScenario.P("order", scenario.OrderId),
                SyntheticOrderScenario.P("org", scenario.OrganizationId),
                SyntheticOrderScenario.P("version", eventVersion.Value),
                SyntheticOrderScenario.P("previous", previousStatus),
                SyntheticOrderScenario.P("new", newStatus),
                SyntheticOrderScenario.P("actor", scenario.UserId));
        }

        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,
              response_body,resource_id,created_at,expires_at)
            VALUES (
              @org,'ORD-002:TRANSITION_ORDER',@key,@hash,200,@body::jsonb,
              @resource,clock_timestamp(),clock_timestamp()+interval '1 hour');
            """,
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("key", command.IdempotencyKey),
            SyntheticOrderScenario.P("hash", requestHash),
            SyntheticOrderScenario.P("body", responseBody),
            SyntheticOrderScenario.P(
                "resource",
                resourceMatches ? scenario.OrderId : Guid.NewGuid()));
    }

    private static async Task InsertProofAsync(SyntheticOrderScenario scenario, string proofType)
    {
        var uploadId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            VALUES (
              @upload,@order,@org,@user,@quarantine,'image/jpeg',1024,'READY',clock_timestamp()+interval '1 day');
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,captured_at,created_by)
            VALUES (
              gen_random_uuid(),@order,@org,@upload,@proof_type,@object_key,
              decode(repeat('02',32),'hex'),'image/jpeg',100,clock_timestamp(),@user);
            """,
            SyntheticOrderScenario.P("upload", uploadId),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("quarantine", $"quarantine/{uploadId:N}"),
            SyntheticOrderScenario.P("proof_type", proofType),
            SyntheticOrderScenario.P("object_key", $"proofs/{uploadId:N}"));
    }

    private static async Task<Guid> InsertIncidentAsync(
        SyntheticOrderScenario scenario,
        bool custodyAcquired)
    {
        var incidentId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,created_by)
            VALUES (@incident,@order,@org,'SYNTHETIC','LOW','OPEN',@custody,@user);
            """,
            SyntheticOrderScenario.P("incident", incidentId),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("custody", custodyAcquired),
            SyntheticOrderScenario.P("user", scenario.UserId));
        return incidentId;
    }

    private static async Task InsertCodAsync(
        SyntheticOrderScenario scenario,
        string status,
        long amount)
    {
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO finance.cod_transactions(
              id,order_id,owner_org_id,amount_cents,status,recorded_at)
            VALUES (gen_random_uuid(),@order,@org,@amount,@status,clock_timestamp());
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("amount", amount),
            SyntheticOrderScenario.P("status", status));
    }

    private async Task AssertNoTransitionArtifactsAsync(
        SyntheticOrderScenario scenario,
        OrderStatus expectedStatus)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT status,version,
              (SELECT count(*) FROM orders.order_events WHERE order_id=@order),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order),
              (SELECT count(*) FROM platform.audit_logs
                WHERE entity_id=@order AND action='ORDER_STATUS_CHANGED'),
              (SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='ORD-002:TRANSITION_ORDER')
            FROM orders.orders WHERE id=@order;
            """);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        verify.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedStatus.ToContractValue(), reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        for (var ordinal = 2; ordinal <= 5; ordinal++)
        {
            Assert.Equal(0L, reader.GetInt64(ordinal));
        }
    }

    private async Task AssertArtifactCountsAsync(
        SyntheticOrderScenario scenario,
        long events,
        long outbox,
        long audit,
        long idempotency)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM orders.order_events WHERE order_id=@order),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order),
              (SELECT count(*) FROM platform.audit_logs
                WHERE entity_id=@order AND action='ORDER_STATUS_CHANGED'),
              (SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='ORD-002:TRANSITION_ORDER' AND response_status=200);
            """);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        verify.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(events, reader.GetInt64(0));
        Assert.Equal(outbox, reader.GetInt64(1));
        Assert.Equal(audit, reader.GetInt64(2));
        Assert.Equal(idempotency, reader.GetInt64(3));
    }

    private static async Task<bool> TryTransitionAsync(
        IOrderTransitionService service,
        TransitionOrderCommand command)
    {
        try
        {
            await service.TransitionAsync(command, CancellationToken.None);
            return true;
        }
        catch (OrderTransitionConflictException)
        {
            return false;
        }
    }

    private static IEnumerable<(OrderStatus Source, OrderStatus Target)> ExpectedEdges() =>
        OrderTransitionMatrix.AllowedTransitions
            .OrderBy(pair => pair.Key)
            .SelectMany(pair => pair.Value.Order().Select(target => (pair.Key, target)));

    private static string Key() => $"ord002-pg-{Guid.NewGuid():N}";

    private sealed class TransitionScope(
        OrdersDbContext context,
        IOrderTransitionService service) : IAsyncDisposable
    {
        internal IOrderTransitionService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class ThrowAtTransitionStage(OrderTransitionStage target)
        : IOrderTransitionFailureInjector
    {
        public Task OnStageAsync(
            OrderTransitionStage stage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == target)
            {
                throw new InjectedTransitionFailure(stage);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InjectedTransitionFailure(OrderTransitionStage stage)
        : Exception($"Injected transition failure at {stage}.");
}
