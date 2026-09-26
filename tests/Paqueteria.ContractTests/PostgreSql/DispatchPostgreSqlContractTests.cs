using System.Data.Common;
using System.Text.Json;
using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.ExternalOffers;
using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Persistence.Migrations;
using Dispatch.Infrastructure.Stops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class DispatchPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 7, 23, 21, 0, 0, TimeSpan.Zero);

    [PostgreSqlContractFact]
    public async Task External_offer_has_one_winner_and_forty_nine_uniform_conflicts_under_real_concurrency()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var actors = await scenario.CreateExternalDriversAsync(50);
        var creator = CreateExternalOfferService(fixture.AppDataSource);
        var createCommand = new CreateExternalOfferCommand(
                scenario.DispatcherUserId,
                scenario.OrganizationId,
                $"ext-create-{Guid.NewGuid():N}",
                scenario.OrderId,
                12_345,
                OccurredAt.AddHours(2),
                new ExternalOfferConstraints(["MOTORCYCLE"], [], false),
                false,
                "ext-001-concurrency-create");
        var offer = await creator.CreateAsync(createCommand, default);
        Assert.Equal(offer, await creator.CreateAsync(createCommand, default));
        var changedCreate = await Assert.ThrowsAsync<ExternalOfferConflictException>(() =>
            creator.CreateAsync(createCommand with { CommissionCents = 12_346 }, default));
        Assert.Equal(ExternalOfferConflictCode.IdempotencyConflict, changedCreate.Code);

        var page = await CreateExternalOfferService(fixture.AppDataSource)
            .ListEligibleAsync(actors[0], scenario.OrganizationId, null, default);
        Assert.Contains(page.Items, item => item.Id == offer.Id && item.Commission.AmountCents == 12_345);

        using var gate = new ManualResetEventSlim(false);
        var attempts = actors.Select((actorId, index) => Task.Run(async () =>
        {
            gate.Wait();
            var acceptCommand = new AcceptExternalOfferCommand(
                actorId,
                scenario.OrganizationId,
                $"ext-accept-{index:D2}-{Guid.NewGuid():N}",
                offer.Id,
                $"ext-001-concurrency-{index:D2}");
            try
            {
                var result = await CreateExternalOfferService(fixture.AppDataSource)
                    .AcceptAsync(acceptCommand, default);
                return (Result: result, Conflict: (ExternalOfferConflictCode?)null, Command: acceptCommand);
            }
            catch (ExternalOfferConflictException exception)
            {
                return (Result: (AssignmentResult?)null, Conflict: (ExternalOfferConflictCode?)exception.Code, Command: acceptCommand);
            }
        })).ToArray();

        gate.Set();
        var outcomes = await Task.WhenAll(attempts);
        var winner = Assert.Single(outcomes, value => value.Result is not null).Result!;
        Assert.Equal(49, outcomes.Count(value => value.Conflict == ExternalOfferConflictCode.OfferUnavailable));
        Assert.Equal("ACCEPTED", winner.Status);
        Assert.Equal("MXN", winner.Cost.Currency);
        Assert.Equal(12_345, winner.Cost.AmountCents);
        var winningCommand = Assert.Single(outcomes, value => value.Result is not null).Command;
        Assert.Equal(
            winner,
            await CreateExternalOfferService(fixture.AppDataSource).AcceptAsync(winningCommand, default));

        await using var query = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM dispatch.external_offers WHERE id=@offer AND status='ACCEPTED'),
              (SELECT accepted_by_driver_id FROM dispatch.external_offers WHERE id=@offer),
              (SELECT count(*) FROM dispatch.assignments WHERE order_id=@order AND assignment_type='EXTERNAL'),
              (SELECT cost_cents FROM dispatch.assignments WHERE order_id=@order),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org
                 AND action IN ('EXTERNAL_OFFER_CREATED','EXTERNAL_OFFER_ACCEPTED','ASSIGNMENT_CREATED')),
              (SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@org
                 AND ((aggregate_id=@offer AND topic='dispatch.external-offer-changed')
                   OR (aggregate_id=@order AND topic IN ('orders.status-changed','orders.timeline-event-added','dispatch.assignment-changed')))),
              (SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org
                 AND scope='EXT-001:ACCEPT_EXTERNAL_OFFER' AND response_status=200);
            """);
        query.Parameters.AddWithValue("offer", offer.Id);
        query.Parameters.AddWithValue("order", scenario.OrderId);
        query.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(winner.DriverId, reader.GetGuid(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(12_345L, reader.GetInt64(3));
        Assert.Equal(3L, reader.GetInt64(4));
        Assert.Equal(5L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
    }

    [PostgreSqlContractFact]
    public async Task Eligible_read_is_tenant_scoped_rechecks_driver_and_excludes_expired_offers()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var actor = Assert.Single(await scenario.CreateExternalDriversAsync(1));
        var offer = await CreateExternalOfferService(fixture.AppDataSource).CreateAsync(
            new CreateExternalOfferCommand(
                scenario.DispatcherUserId,
                scenario.OrganizationId,
                $"ext-read-{Guid.NewGuid():N}",
                scenario.OrderId,
                5000,
                OccurredAt.AddMinutes(30),
                new ExternalOfferConstraints(["MOTORCYCLE"], [], false),
                false,
                "ext-001-read"),
            default);

        var eligible = await CreateExternalOfferService(fixture.AppDataSource)
            .ListEligibleAsync(actor, scenario.OrganizationId, null, default);
        Assert.Equal(offer.Id, Assert.Single(eligible.Items).Id);

        await scenario.ExecuteAdminAsync(
            "UPDATE drivers.driver_profiles SET status='SUSPENDED' WHERE user_id=@actor;",
            P("actor", actor));
        Assert.Empty((await CreateExternalOfferService(fixture.AppDataSource)
            .ListEligibleAsync(actor, scenario.OrganizationId, null, default)).Items);
        var ineligible = await Assert.ThrowsAsync<ExternalOfferConflictException>(() =>
            CreateExternalOfferService(fixture.AppDataSource).AcceptAsync(
                new AcceptExternalOfferCommand(
                    actor,
                    scenario.OrganizationId,
                    $"ext-ineligible-{Guid.NewGuid():N}",
                    offer.Id,
                    "ext-001-ineligible"),
                default));
        Assert.Equal(ExternalOfferConflictCode.DriverIneligible, ineligible.Code);
        await scenario.ExecuteAdminAsync(
            "UPDATE drivers.driver_profiles SET status='ACTIVE' WHERE user_id=@actor;",
            P("actor", actor));

        var afterExpiry = CreateExternalOfferService(
            fixture.AppDataSource,
            OccurredAt.AddHours(1));
        Assert.Empty((await afterExpiry.ListEligibleAsync(
            actor, scenario.OrganizationId, null, default)).Items);
        var expired = await Assert.ThrowsAsync<ExternalOfferConflictException>(() =>
            afterExpiry.AcceptAsync(
                new AcceptExternalOfferCommand(
                    actor,
                    scenario.OrganizationId,
                    $"ext-expired-{Guid.NewGuid():N}",
                    offer.Id,
                    "ext-001-expired"),
                default));
        Assert.Equal(ExternalOfferConflictCode.OfferExpired, expired.Code);

        await Assert.ThrowsAsync<ExternalOfferForbiddenException>(() =>
            CreateExternalOfferService(fixture.AppDataSource).ListEligibleAsync(
                actor,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                null,
                default));
    }

    [PostgreSqlContractFact]
    public async Task Coordinator_creates_one_accepted_assignment_transition_event_outbox_audits_and_replay()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var service = CreateAssignmentService(fixture.AppDataSource);
        var command = Command(scenario, costCents: (long)int.MaxValue + 100);

        var created = await service.CreateOwnDriverAssignmentAsync(command, default);
        await scenario.ExecuteAdminAsync(
            """
            UPDATE drivers.driver_profiles SET status='SUSPENDED' WHERE id=@driver;
            UPDATE drivers.driver_documents SET expires_at=@expired WHERE driver_id=@driver;
            """,
            P("driver", scenario.DriverId),
            P("expired", OccurredAt.AddMinutes(-1)));
        var replay = await service.CreateOwnDriverAssignmentAsync(command, default);

        Assert.Equal(created, replay);
        Assert.Equal("ACCEPTED", created.Status);
        Assert.Equal("MXN", created.Cost.Currency);
        Assert.Equal((long)int.MaxValue + 100, created.Cost.AmountCents);

        await using var query = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM dispatch.assignments WHERE order_id=@order),
              (SELECT status FROM dispatch.assignments WHERE order_id=@order),
              (SELECT accepted_at=created_at FROM dispatch.assignments WHERE order_id=@order),
              (SELECT status FROM orders.orders WHERE id=@order),
              (SELECT version FROM orders.orders WHERE id=@order),
              (SELECT count(*) FROM orders.order_events WHERE order_id=@order AND event_type='ORDER_STATUS_CHANGED'),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order AND topic='orders.status-changed'),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order AND topic='orders.timeline-event-added'),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order AND topic='dispatch.assignment-changed'),
              (SELECT count(DISTINCT id) FROM platform.outbox_events WHERE aggregate_id=@order
                AND topic IN ('orders.status-changed','orders.timeline-event-added','dispatch.assignment-changed')),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ASSIGNMENT_CREATED'),
              (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action='ORDER_STATUS_CHANGED'),
              (SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='DSP-002:ASSIGN_OWN_DRIVER' AND response_status=201);
            """);
        query.Parameters.AddWithValue("order", scenario.OrderId);
        query.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("ACCEPTED", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("ASSIGNED", reader.GetString(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal(1L, reader.GetInt64(7));
        Assert.Equal(1L, reader.GetInt64(8));
        Assert.Equal(3L, reader.GetInt64(9));
        Assert.Equal(1L, reader.GetInt64(10));
        Assert.Equal(1L, reader.GetInt64(11));
        Assert.Equal(1L, reader.GetInt64(12));
    }

    [PostgreSqlContractFact]
    public async Task Coordinator_canonicalizes_one_shared_timestamp_across_assignment_and_order()
    {
        var raw = DateTimeOffset.Parse(
            "2026-07-25T18:00:00.1234567Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = UtcMicrosecondPrecision.Normalize(raw);
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var service = CreateAssignmentService(fixture.AppDataSource, now: raw);
        var command = Command(
            scenario,
            idempotencyKey: "dsp002-microsecond-precision-0001");

        var created = await service.CreateOwnDriverAssignmentAsync(command, default);
        var replay = await service.CreateOwnDriverAssignmentAsync(command, default);

        Assert.Equal(created, replay);
        await using var query = fixture.AdminDataSource.CreateCommand(
            """
            SELECT a.created_at,a.accepted_at,o.updated_at,e.occurred_at,
              (SELECT created_at FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='orders.status-changed'),
              (SELECT available_at FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='orders.status-changed'),
              (SELECT payload->>'occurred_at' FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='orders.status-changed'),
              (SELECT created_at FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='orders.timeline-event-added'),
              (SELECT available_at FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='orders.timeline-event-added'),
              (SELECT payload->>'occurred_at' FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='orders.timeline-event-added'),
              (SELECT created_at FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='dispatch.assignment-changed'),
              (SELECT available_at FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='dispatch.assignment-changed'),
              (SELECT payload->>'occurred_at' FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic='dispatch.assignment-changed'),
              (SELECT count(*) FROM platform.audit_logs
                WHERE org_id=@org
                  AND action IN ('ASSIGNMENT_CREATED','ORDER_STATUS_CHANGED')
                  AND occurred_at<>@canonical),
              (SELECT created_at FROM platform.idempotency_keys
                WHERE owner_org_id=@org
                  AND scope='DSP-002:ASSIGN_OWN_DRIVER'
                  AND idempotency_key=@key),
              (SELECT count(DISTINCT id) FROM platform.outbox_events
                WHERE aggregate_id=o.id AND aggregate_version=2
                  AND topic IN (
                    'orders.status-changed',
                    'orders.timeline-event-added',
                    'dispatch.assignment-changed'))
            FROM dispatch.assignments a
            JOIN orders.orders o ON o.id=a.order_id
            JOIN orders.order_events e
              ON e.order_id=o.id AND e.aggregate_version=2
            WHERE a.id=@assignment
            """);
        query.Parameters.AddWithValue("assignment", created.Id);
        query.Parameters.AddWithValue("org", scenario.OrganizationId);
        query.Parameters.AddWithValue("canonical", canonical);
        query.Parameters.AddWithValue("key", command.IdempotencyKey);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        foreach (var ordinal in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 10, 11, 14 })
        {
            Assert.Equal(canonical, reader.GetFieldValue<DateTimeOffset>(ordinal));
        }

        foreach (var ordinal in new[] { 6, 9, 12 })
        {
            var payloadTimestamp = DateTimeOffset.Parse(
                reader.GetString(ordinal),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(canonical, payloadTimestamp);
            Assert.Equal(0, payloadTimestamp.UtcTicks % 10);
        }

        Assert.Equal(0L, reader.GetInt64(13));
        Assert.Equal(3L, reader.GetInt64(15));
    }

    [PostgreSqlContractFact]
    public async Task Same_key_concurrency_replays_and_different_keys_or_drivers_have_exactly_one_success()
    {
        await using (var sameKeyScenario = await DispatchScenario.CreateAsync(fixture))
        {
            var command = Command(sameKeyScenario);
            var results = await Task.WhenAll(
                CreateAssignmentService(fixture.CreateAppDataSource()).CreateOwnDriverAssignmentAsync(command, default),
                CreateAssignmentService(fixture.CreateAppDataSource()).CreateOwnDriverAssignmentAsync(command, default));
            Assert.Equal(results[0], results[1]);
            Assert.Equal(1L, await CountAssignmentsAsync(sameKeyScenario.OrderId));
        }

        await using (var differentKeyScenario = await DispatchScenario.CreateAsync(fixture))
        {
            var attempts = new[]
            {
                ExecuteOutcomeAsync(
                    CreateAssignmentService(fixture.CreateAppDataSource()),
                    Command(differentKeyScenario, idempotencyKey: Key())),
                ExecuteOutcomeAsync(
                    CreateAssignmentService(fixture.CreateAppDataSource()),
                    Command(differentKeyScenario, idempotencyKey: Key())),
            };
            var outcomes = await Task.WhenAll(attempts);
            Assert.Equal(1, outcomes.Count(value => value));
            Assert.Equal(1, outcomes.Count(value => !value));
            Assert.Equal(1L, await CountAssignmentsAsync(differentKeyScenario.OrderId));
        }
    }

    [PostgreSqlContractFact]
    public async Task Missing_and_cross_tenant_resources_execute_the_same_visibility_plan_and_roll_back()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        await using var foreign = await DispatchScenario.CreateAsync(fixture);
        var cases = new[]
        {
            Command(scenario) with { OrderId = Guid.NewGuid() },
            Command(scenario) with { OrderId = foreign.OrderId },
            Command(scenario) with { DriverId = Guid.NewGuid() },
            Command(scenario) with { DriverId = foreign.DriverId },
        };

        foreach (var command in cases)
        {
            var reader = new RecordingVisibilityDataReader(
                new PostgreSqlAssignmentVisibilityDataReader(
                    new PostgreSqlDispatchDriverEligibilityReader()));
            var service = CreateAssignmentService(
                fixture.AppDataSource,
                visibilityDataReader: reader);

            await Assert.ThrowsAsync<AssignmentNotFoundException>(() =>
                service.CreateOwnDriverAssignmentAsync(command, default));

            Assert.Equal(DispatchAssignmentVisibilityResolver.StructuralPlan, reader.Calls);
            await AssertNoAssignmentEffectsAsync(scenario, command.IdempotencyKey);
            await AssertNoAssignmentEffectsAsync(foreign, command.IdempotencyKey);
        }
    }

    [PostgreSqlContractFact]
    public async Task Capability_first_precedes_visibility_for_inactive_viewer_and_admin_without_MFA()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var reader = new RecordingVisibilityDataReader(
            new PostgreSqlAssignmentVisibilityDataReader(
                new PostgreSqlDispatchDriverEligibilityReader()));
        var service = CreateAssignmentService(
            fixture.AppDataSource,
            visibilityDataReader: reader);

        foreach (var (role, status, mfa) in new[]
        {
            ("VIEWER", "ACTIVE", false),
            ("DISPATCHER", "SUSPENDED", false),
            ("PLATFORM_ADMIN", "ACTIVE", false),
        })
        {
            await scenario.SetDispatcherMembershipAsync(role, status);
            foreach (var orderId in new[] { scenario.OrderId, Guid.NewGuid() })
            {
                var command = Command(scenario) with
                {
                    OrderId = orderId,
                    MfaSatisfied = mfa,
                };
                await Assert.ThrowsAsync<AssignmentForbiddenException>(() =>
                    service.CreateOwnDriverAssignmentAsync(command, default));
                await AssertNoAssignmentEffectsAsync(scenario, command.IdempotencyKey);
            }
        }

        Assert.Empty(reader.Calls);

        await scenario.SetDispatcherMembershipAsync("PLATFORM_ADMIN", "ACTIVE");
        var authorizedAdmin = Command(scenario) with
        {
            OrderId = Guid.NewGuid(),
            MfaSatisfied = true,
        };
        await Assert.ThrowsAsync<AssignmentNotFoundException>(() =>
            service.CreateOwnDriverAssignmentAsync(authorizedAdmin, default));
        Assert.Equal(DispatchAssignmentVisibilityResolver.StructuralPlan, reader.Calls);
        await AssertNoAssignmentEffectsAsync(scenario, authorizedAdmin.IdempotencyKey);
    }

    [PostgreSqlContractFact]
    public async Task Authorized_command_executes_authorization_before_idempotency_lock_and_read_once()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var calls = new List<string>();
        var service = CreateAssignmentService(
            fixture.AppDataSource,
            authorizationReader: new RecordingAuthorizationReader(
                new PostgreSqlDispatchAuthorizationReader(),
                calls),
            idempotencyAccess: new RecordingIdempotencyAccess(
                new PostgreSqlAssignmentIdempotencyAccess(),
                calls));

        await service.CreateOwnDriverAssignmentAsync(Command(scenario), default);

        Assert.Equal(
            ["authorization", "idempotency_lock", "idempotency_read"],
            calls);
        Assert.Equal(1, calls.Count(value => value == "authorization"));
    }

    [PostgreSqlContractFact]
    public async Task Denied_capability_never_observes_or_changes_any_idempotency_state()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        foreach (var (role, mfaSatisfied) in new[]
        {
            ("VIEWER", false),
            ("PLATFORM_ADMIN", false),
        })
        {
            await scenario.SetDispatcherMembershipAsync(role, "ACTIVE");
            foreach (var state in Enum.GetValues<SeededIdempotencyState>())
            {
                var command = Command(scenario) with
                {
                    IdempotencyKey = Key(),
                    MfaSatisfied = mfaSatisfied,
                };
                await SeedIdempotencyAsync(command, state);
                var before = await ReadIdempotencySnapshotAsync(command);
                var calls = new List<string>();
                var visibilityReader = new RecordingVisibilityDataReader(
                    new PostgreSqlAssignmentVisibilityDataReader(
                        new PostgreSqlDispatchDriverEligibilityReader()));
                var replayReader = new RecordingReplayEvidenceReader(
                    new PostgreSqlAssignmentReplayEvidenceReader());
                var service = CreateAssignmentService(
                    fixture.AppDataSource,
                    visibilityDataReader: visibilityReader,
                    authorizationReader: new RecordingAuthorizationReader(
                        new PostgreSqlDispatchAuthorizationReader(),
                        calls),
                    idempotencyAccess: new RecordingIdempotencyAccess(
                        new PostgreSqlAssignmentIdempotencyAccess(),
                        calls),
                    replayEvidenceReader: replayReader);

                await Assert.ThrowsAsync<AssignmentForbiddenException>(() =>
                    service.CreateOwnDriverAssignmentAsync(command, default));

                Assert.Equal(["authorization"], calls);
                Assert.Empty(visibilityReader.Calls);
                Assert.Empty(replayReader.Calls);
                Assert.Equal(before, await ReadIdempotencySnapshotAsync(command));
                await AssertNoAssignmentEffectsAsync(
                    scenario,
                    command.IdempotencyKey,
                    state == SeededIdempotencyState.Missing ? 0 : 1);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task Authorized_dispatcher_preserves_all_idempotency_and_replay_results()
    {
        await using (var missing = await DispatchScenario.CreateAsync(fixture))
        {
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(missing), default);
            Assert.Equal("ACCEPTED", created.Status);
        }

        await using (var completed = await DispatchScenario.CreateAsync(fixture))
        {
            var command = Command(completed);
            var created = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(command, default);
            var calls = new List<string>();
            var evidenceReader = new RecordingReplayEvidenceReader(
                new PostgreSqlAssignmentReplayEvidenceReader());
            var replay = await CreateAssignmentService(
                    fixture.AppDataSource,
                    authorizationReader: new RecordingAuthorizationReader(
                        new PostgreSqlDispatchAuthorizationReader(),
                        calls),
                    idempotencyAccess: new RecordingIdempotencyAccess(
                        new PostgreSqlAssignmentIdempotencyAccess(),
                        calls),
                    replayEvidenceReader: evidenceReader)
                .CreateOwnDriverAssignmentAsync(command, default);

            Assert.Equal(created, replay);
            Assert.Equal(
                ["authorization", "idempotency_lock", "idempotency_read"],
                calls);
            Assert.Equal(["replay_evidence"], evidenceReader.Calls);
        }

        await using (var changedHash = await DispatchScenario.CreateAsync(fixture))
        {
            var command = Command(changedHash);
            await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(command, default);
            var conflict = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource)
                    .CreateOwnDriverAssignmentAsync(
                        command with { DriverId = Guid.NewGuid() },
                        default));
            Assert.Equal(AssignmentConflictCode.IdempotencyConflict, conflict.Code);
        }

        await using (var incomplete = await DispatchScenario.CreateAsync(fixture))
        {
            var command = Command(incomplete);
            await SeedIdempotencyAsync(command, SeededIdempotencyState.Incomplete);
            var before = await ReadIdempotencySnapshotAsync(command);
            var conflict = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource)
                    .CreateOwnDriverAssignmentAsync(command, default));
            Assert.Equal(AssignmentConflictCode.IdempotencyConflict, conflict.Code);
            Assert.Equal(before, await ReadIdempotencySnapshotAsync(command));
            await AssertNoAssignmentEffectsAsync(incomplete, command.IdempotencyKey, 1);
        }

        await using (var inconsistent = await DispatchScenario.CreateAsync(fixture))
        {
            var command = Command(inconsistent);
            await SeedIdempotencyAsync(command, SeededIdempotencyState.CompletedMatchingHash);
            var evidenceReader = new RecordingReplayEvidenceReader(
                new PostgreSqlAssignmentReplayEvidenceReader());
            var conflict = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(
                        fixture.AppDataSource,
                        replayEvidenceReader: evidenceReader)
                    .CreateOwnDriverAssignmentAsync(command, default));
            Assert.Equal(AssignmentConflictCode.InconsistentReplayEvidence, conflict.Code);
            Assert.Equal(["replay_evidence"], evidenceReader.Calls);
            await AssertNoAssignmentEffectsAsync(inconsistent, command.IdempotencyKey, 1);
        }
    }

    [PostgreSqlContractFact]
    public async Task Visible_resources_preserve_ineligible_expired_and_order_conflict_results()
    {
        await using (var ineligible = await DispatchScenario.CreateAsync(fixture))
        {
            await ineligible.ExecuteAdminAsync(
                "UPDATE drivers.driver_profiles SET status='SUSPENDED' WHERE id=@driver",
                P("driver", ineligible.DriverId));
            var exception = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource)
                    .CreateOwnDriverAssignmentAsync(Command(ineligible), default));
            Assert.Equal(AssignmentConflictCode.DriverIneligible, exception.Code);
        }

        await using (var expired = await DispatchScenario.CreateAsync(fixture))
        {
            await expired.ExecuteAdminAsync(
                "UPDATE drivers.driver_documents SET expires_at=@expired WHERE driver_id=@driver",
                P("expired", OccurredAt),
                P("driver", expired.DriverId));
            var exception = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource)
                    .CreateOwnDriverAssignmentAsync(Command(expired), default));
            Assert.Equal(AssignmentConflictCode.DriverDocumentExpired, exception.Code);
        }

        await using (var invalidState = await DispatchScenario.CreateAsync(fixture))
        {
            await invalidState.ExecuteAdminAsync(
                "UPDATE orders.orders SET status='CONFIRMED' WHERE id=@order",
                P("order", invalidState.OrderId));
            var exception = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
                CreateAssignmentService(fixture.AppDataSource)
                    .CreateOwnDriverAssignmentAsync(Command(invalidState), default));
            Assert.Equal(AssignmentConflictCode.InvalidOrderState, exception.Code);
        }
    }

    [PostgreSqlContractFact]
    public async Task Every_fault_injection_stage_rolls_back_all_effects()
    {
        foreach (var stage in Enum.GetValues<AssignmentTransactionStage>())
        {
            await using var scenario = await DispatchScenario.CreateAsync(fixture);
            var injector = new ThrowingInjector(stage);
            var service = CreateAssignmentService(fixture.AppDataSource, injector);

            await Assert.ThrowsAsync<SyntheticDispatchFailure>(() =>
                service.CreateOwnDriverAssignmentAsync(Command(scenario), default));

            await using var query = fixture.AdminDataSource.CreateCommand(
                """
                SELECT
                  (SELECT count(*) FROM dispatch.assignments WHERE order_id=@order),
                  (SELECT status FROM orders.orders WHERE id=@order),
                  (SELECT version FROM orders.orders WHERE id=@order),
                  (SELECT count(*) FROM orders.order_events WHERE order_id=@order),
                  (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order),
                  (SELECT count(*) FROM platform.audit_logs WHERE org_id=@org),
                  (SELECT count(*) FROM platform.idempotency_keys
                    WHERE owner_org_id=@org AND scope='DSP-002:ASSIGN_OWN_DRIVER');
                """);
            query.Parameters.AddWithValue("order", scenario.OrderId);
            query.Parameters.AddWithValue("org", scenario.OrganizationId);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal("READY_FOR_PICKUP", reader.GetString(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(0L, reader.GetInt64(3));
            Assert.Equal(0L, reader.GetInt64(4));
            Assert.Equal(0L, reader.GetInt64(5));
            Assert.Equal(0L, reader.GetInt64(6));
        }
    }

    [PostgreSqlContractFact]
    public async Task Stops_query_uses_direct_projection_custody_and_minimized_addresses()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var assignment = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        var query = CreateStopsQuery(fixture.AppDataSource);

        var pickup = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("PICKUP", pickup.StopType);
        Assert.Equal("READY origin summary", pickup.AddressSummary);

        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status='IN_TRANSIT' WHERE id=@order",
            P("order", scenario.OrderId));
        var delivery = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("DELIVERY", delivery.StopType);
        Assert.Equal("READY destination summary", delivery.AddressSummary);

        await scenario.ExecuteAdminAsync(
            """
            UPDATE orders.orders SET status='FAILED_ATTEMPT' WHERE id=@order;
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,payload,occurred_at)
            VALUES (
              @event,@order,@org,3,'ORDER_STATUS_CHANGED',
              '{"previous_status":"AT_PICKUP","new_status":"PICKED_UP"}',@occurred);
            """,
            P("event", Guid.NewGuid()),
            P("order", scenario.OrderId),
            P("org", scenario.OrganizationId),
            P("occurred", OccurredAt.AddMinutes(1)));
        var custodyDelivery = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("DELIVERY", custodyDelivery.StopType);
        Assert.Equal("FAILED_ATTEMPT", custodyDelivery.Status);

        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status='RETURNING' WHERE id=@order",
            P("order", scenario.OrderId));
        var returning = Assert.Single(await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId,
            scenario.OrganizationId,
            default));
        Assert.Equal("RETURN", returning.StopType);
        Assert.Equal("READY origin summary", returning.AddressSummary);
        Assert.DoesNotContain(assignment.Id.ToString("D"), returning.AddressSummary, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task Dispatch_adoption_RLS_history_and_partial_index_are_canonical()
    {
        await using (var canonical = fixture.AdminDataSource.CreateCommand(
                         AdoptCanonicalDispatchAssignmentsBaseline.AdoptionSql))
        {
            await canonical.ExecuteNonQueryAsync();
        }

        await using (var connection = new NpgsqlConnection(fixture.DeploymentConnectionString))
        {
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DispatchDbContext>()
                .UseNpgsql(connection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_dispatch", "platform");
                }).Options;
            await using var context = new DispatchDbContext(options, new TenantDatabaseExecutionState());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }

        await using var security = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT h."MigrationId" FROM platform.__ef_migrations_history_dispatch h),
              (SELECT pg_get_userbyid(c.relowner)
               FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
               WHERE n.nspname='platform' AND c.relname='__ef_migrations_history_dispatch'),
              c.relrowsecurity,c.relforcerowsecurity,
              (SELECT NOT rolbypassrls FROM pg_roles WHERE rolname='paqueteria_app'),
              (SELECT pg_get_expr(i.indpred,i.indrelid)
               FROM pg_index i WHERE i.indexrelid='dispatch.one_active_assignment_per_order'::regclass),
              (SELECT count(*) FROM pg_policies
               WHERE schemaname='dispatch' AND tablename='assignments'
                 AND policyname='assignments_tenant')
            FROM pg_class c
            WHERE c.oid='dispatch.assignments'::regclass;
            """);
        await using var reader = await security.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("20260723_AdoptCanonicalDispatchAssignmentsBaseline", reader.GetString(0));
        Assert.Equal("paqueteria_migrator", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        var predicate = reader.GetString(5);
        Assert.Contains("ACCEPTED", predicate, StringComparison.Ordinal);
        Assert.Contains("ACTIVE", predicate, StringComparison.Ordinal);
        Assert.Equal(1L, reader.GetInt64(6));

        await using var empty = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            Guid.NewGuid(),
            []);
        await using var emptyQuery = new NpgsqlCommand(
            "SELECT count(*) FROM dispatch.assignments",
            empty.Connection,
            empty.Transaction);
        Assert.Equal(0L, await emptyQuery.ExecuteScalarAsync());
        await empty.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_missing_cost_check()
    {
        await AssertAdoptionRejectsAsync(
            "ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_cost_cents_check",
            "cost_cents non-negative check");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_incorrect_cost_check()
    {
        await AssertAdoptionRejectsAsync(
            """
            ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_cost_cents_check;
            ALTER TABLE dispatch.assignments
              ADD CONSTRAINT assignments_cost_cents_check CHECK (cost_cents >= -1);
            """,
            "cost_cents non-negative check");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_missing_foreign_key()
    {
        await AssertAdoptionRejectsAsync(
            "ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_order_id_fkey",
            "exactly five canonical foreign keys");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_foreign_key_on_the_wrong_local_column()
    {
        await AssertAdoptionRejectsAsync(
            """
            ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_owner_org_id_fkey;
            ALTER TABLE dispatch.assignments
              ADD CONSTRAINT assignments_owner_org_id_fkey
              FOREIGN KEY (driver_id) REFERENCES organizations.organizations(id) NOT VALID;
            """,
            "owner_org_id");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_foreign_key_to_the_wrong_table_or_referenced_column()
    {
        await AssertAdoptionRejectsAsync(
            """
            ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_owner_org_id_fkey;
            ALTER TABLE dispatch.assignments
              ADD CONSTRAINT assignments_owner_org_id_fkey
              FOREIGN KEY (owner_org_id) REFERENCES identity.users(id) NOT VALID;
            """,
            "owner_org_id");
        await AssertAdoptionRejectsAsync(
            """
            ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_order_id_fkey;
            ALTER TABLE dispatch.assignments
              ADD CONSTRAINT assignments_order_id_fkey
              FOREIGN KEY (order_id) REFERENCES orders.orders(quote_id) NOT VALID;
            """,
            "order_id");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_noncanonical_foreign_key_actions()
    {
        await AssertAdoptionRejectsAsync(
            """
            ALTER TABLE dispatch.assignments DROP CONSTRAINT assignments_route_id_fkey;
            ALTER TABLE dispatch.assignments
              ADD CONSTRAINT assignments_route_id_fkey
              FOREIGN KEY (route_id) REFERENCES routes.routes(id) ON DELETE CASCADE;
            """,
            "route_id");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_partial_index_with_a_different_predicate()
    {
        await AssertAdoptionRejectsAsync(
            """
            DROP INDEX dispatch.one_active_assignment_per_order;
            CREATE UNIQUE INDEX one_active_assignment_per_order
              ON dispatch.assignments(order_id)
              WHERE status='ACCEPTED';
            """,
            "active assignment index");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_RLS_or_FORCE_RLS_disabled()
    {
        await AssertAdoptionRejectsAsync(
            "ALTER TABLE dispatch.assignments DISABLE ROW LEVEL SECURITY",
            "RLS configuration");
        await AssertAdoptionRejectsAsync(
            "ALTER TABLE dispatch.assignments NO FORCE ROW LEVEL SECURITY",
            "RLS configuration");
    }

    [PostgreSqlContractFact]
    public async Task Adoption_rejects_incorrect_tenant_policy()
    {
        await AssertAdoptionRejectsAsync(
            """
            DROP POLICY assignments_tenant ON dispatch.assignments;
            CREATE POLICY assignments_tenant ON dispatch.assignments
              USING (security.app_allowed_org(owner_org_id))
              WITH CHECK (security.app_allowed_org(owner_org_id));
            """,
            "assignments_tenant policy");
    }

    private PostgreSqlAssignmentToOrderCoordinator CreateAssignmentService(
        NpgsqlDataSource dataSource,
        IAssignmentFailureInjector? injector = null,
        IAssignmentVisibilityDataReader? visibilityDataReader = null,
        IDispatchAuthorizationReader? authorizationReader = null,
        IAssignmentIdempotencyAccess? idempotencyAccess = null,
        IAssignmentReplayEvidenceReader? replayEvidenceReader = null,
        DateTimeOffset? now = null)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlAssignmentToOrderCoordinator(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            Options.Create(new DispatchOptions
            {
                Provider = DispatchProviderKind.PostgreSql,
                AssignmentPolicyVersion = "dsp-002-contract-v1",
            }),
            Options.Create(EligibilityOptions()),
            new DispatchAssignmentAuthorizer(),
            authorizationReader ?? new PostgreSqlDispatchAuthorizationReader(),
            idempotencyAccess ?? new PostgreSqlAssignmentIdempotencyAccess(),
            new DispatchAssignmentVisibilityResolver(
                visibilityDataReader ??
                new PostgreSqlAssignmentVisibilityDataReader(
                    new PostgreSqlDispatchDriverEligibilityReader())),
            replayEvidenceReader ?? new PostgreSqlAssignmentReplayEvidenceReader(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            injector ?? new NoOpAssignmentFailureInjector(),
            new FixedClock(now ?? OccurredAt),
            NullLogger<PostgreSqlAssignmentToOrderCoordinator>.Instance);
    }

    private static PostgreSqlExternalOfferService CreateExternalOfferService(
        NpgsqlDataSource dataSource,
        DateTimeOffset? now = null)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlExternalOfferService(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            Options.Create(new DispatchOptions
            {
                Provider = DispatchProviderKind.PostgreSql,
                AssignmentPolicyVersion = "ext-001-contract-v1",
            }),
            Options.Create(EligibilityOptions()),
            new DispatchAssignmentAuthorizer(),
            new PostgreSqlDispatchAuthorizationReader(),
            new PostgreSqlDispatchDriverEligibilityReader(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new FixedClock(now ?? OccurredAt));
    }

    private static PostgreSqlDriverStopsQuery CreateStopsQuery(NpgsqlDataSource dataSource)
    {
        var state = new TenantDatabaseExecutionState();
        var dbOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new DispatchDbContext(dbOptions, state);
        return new PostgreSqlDriverStopsQuery(
            new TenantTransactionContext<DispatchDbContext>(context, state),
            NullLogger<PostgreSqlDriverStopsQuery>.Instance);
    }

    private static DispatchDriverEligibilityOptions EligibilityOptions() => new()
    {
        PolicyVersion = "dsp-001-contract-v1",
        RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = ["IDENTITY"],
        },
        VehicleCapacity = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = new DispatchVehicleCapacityOptions
            {
                MaximumPackageCount = 4,
                MaximumTotalWeightGrams = 4_000,
                MaximumSinglePackageWeightGrams = 2_000,
                MaximumLengthMillimeters = 500,
                MaximumWidthMillimeters = 400,
                MaximumHeightMillimeters = 300,
                RequireDimensions = true,
            },
        },
    };

    private static CreateOwnDriverAssignmentCommand Command(
        DispatchScenario scenario,
        string? idempotencyKey = null,
        long costCents = 0) => new(
        scenario.DispatcherUserId,
        scenario.OrganizationId,
        idempotencyKey ?? Key(),
        scenario.OrderId,
        scenario.DriverId,
        "OWN",
        costCents,
        null,
        false,
        "dsp-002-contract-request");

    private async Task<long> CountAssignmentsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*) FROM dispatch.assignments WHERE order_id=@order");
        command.Parameters.AddWithValue("order", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertNoAssignmentEffectsAsync(
        DispatchScenario scenario,
        string idempotencyKey,
        long expectedIdempotencyRows = 0)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT status FROM orders.orders WHERE id=@order),
              (SELECT version FROM orders.orders WHERE id=@order),
              (SELECT count(*) FROM dispatch.assignments WHERE order_id=@order),
              (SELECT count(*) FROM orders.order_events WHERE order_id=@order),
              (SELECT count(*) FROM platform.outbox_events WHERE aggregate_id=@order),
              (SELECT count(*) FROM platform.audit_logs
                WHERE org_id=@org AND action IN ('ASSIGNMENT_CREATED','ORDER_STATUS_CHANGED')),
              (SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='DSP-002:ASSIGN_OWN_DRIVER'
                  AND idempotency_key=@key);
            """);
        command.Parameters.AddWithValue("order", scenario.OrderId);
        command.Parameters.AddWithValue("org", scenario.OrganizationId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("READY_FOR_PICKUP", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(0L, reader.GetInt64(4));
        Assert.Equal(0L, reader.GetInt64(5));
        Assert.Equal(expectedIdempotencyRows, reader.GetInt64(6));
    }

    private async Task SeedIdempotencyAsync(
        CreateOwnDriverAssignmentCommand command,
        SeededIdempotencyState state)
    {
        if (state == SeededIdempotencyState.Missing)
        {
            return;
        }

        var requestHash = state == SeededIdempotencyState.CompletedDifferentHash
            ? Enumerable.Repeat((byte)0xa5, 32).ToArray()
            : AssignmentCanonicalizer.ComputeSha256(command);
        var completed = state is
            SeededIdempotencyState.CompletedMatchingHash or
            SeededIdempotencyState.CompletedDifferentHash;
        var resourceId = Guid.NewGuid();
        var responseBody = JsonSerializer.Serialize(
            new AssignmentResult(
                resourceId,
                command.OrderId,
                command.DriverId,
                "ACCEPTED",
                new Dispatch.Application.Assignments.MoneyResult(
                    "MXN",
                    command.CostCents!.Value)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
        await using var seed = fixture.AdminDataSource.CreateCommand(
            completed
                ? """
                  INSERT INTO platform.idempotency_keys(
                    owner_org_id,scope,idempotency_key,request_hash,response_status,
                    response_body,resource_id,created_at,expires_at)
                  VALUES (@owner,'DSP-002:ASSIGN_OWN_DRIVER',@key,@hash,201,
                    @body,@resource,@created,@expires)
                  """
                : """
                  INSERT INTO platform.idempotency_keys(
                    owner_org_id,scope,idempotency_key,request_hash,response_status,
                    response_body,resource_id,created_at,expires_at)
                  VALUES (@owner,'DSP-002:ASSIGN_OWN_DRIVER',@key,@hash,NULL,
                    NULL,NULL,@created,@expires)
                  """);
        seed.Parameters.Add(P("owner", command.OrganizationId));
        seed.Parameters.Add(P("key", command.IdempotencyKey));
        seed.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea)
        {
            TypedValue = requestHash,
        });
        seed.Parameters.Add(P("created", OccurredAt));
        seed.Parameters.Add(P("expires", OccurredAt.AddDays(1)));
        if (completed)
        {
            seed.Parameters.Add(new NpgsqlParameter<string>("body", NpgsqlDbType.Jsonb)
            {
                TypedValue = responseBody,
            });
            seed.Parameters.Add(P("resource", resourceId));
        }

        Assert.Equal(1, await seed.ExecuteNonQueryAsync());
    }

    private async Task<IdempotencySnapshot?> ReadIdempotencySnapshotAsync(
        CreateOwnDriverAssignmentCommand command)
    {
        await using var query = fixture.AdminDataSource.CreateCommand(
            """
            SELECT encode(request_hash,'hex'),response_status,response_body::text,
                   resource_id,created_at,expires_at
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope='DSP-002:ASSIGN_OWN_DRIVER'
              AND idempotency_key=@key
            """);
        query.Parameters.Add(P("owner", command.OrganizationId));
        query.Parameters.Add(P("key", command.IdempotencyKey));
        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    private async Task AssertAdoptionRejectsAsync(string mutation, string expectedMessage)
    {
        await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var mutate = new NpgsqlCommand(mutation, connection, transaction))
            {
                await mutate.ExecuteNonQueryAsync();
            }

            await using var adopt = new NpgsqlCommand(
                AdoptCanonicalDispatchAssignmentsBaseline.AdoptionSql,
                connection,
                transaction);
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                adopt.ExecuteNonQueryAsync());
            Assert.Contains(expectedMessage, exception.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<bool> ExecuteOutcomeAsync(
        IAssignmentService service,
        CreateOwnDriverAssignmentCommand command)
    {
        try
        {
            await service.CreateOwnDriverAssignmentAsync(command, default);
            return true;
        }
        catch (AssignmentConflictException)
        {
            return false;
        }
    }

    private static string Key() => $"dsp-002-contract-{Guid.NewGuid():N}";

    private static NpgsqlParameter P(string name, object value) => new(name, value);

    private enum SeededIdempotencyState
    {
        Missing,
        CompletedMatchingHash,
        CompletedDifferentHash,
        Incomplete,
    }

    private sealed record IdempotencySnapshot(
        string RequestHash,
        int? ResponseStatus,
        string? ResponseBody,
        Guid? ResourceId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class ThrowingInjector(AssignmentTransactionStage target) : IAssignmentFailureInjector
    {
        public Task OnStageAsync(AssignmentTransactionStage stage, CancellationToken cancellationToken) =>
            stage == target
                ? throw new SyntheticDispatchFailure(stage)
                : Task.CompletedTask;
    }

    private sealed class SyntheticDispatchFailure(AssignmentTransactionStage stage)
        : Exception($"Synthetic failure at {stage}.");

    private sealed class RecordingAuthorizationReader(
        IDispatchAuthorizationReader inner,
        List<string> calls) : IDispatchAuthorizationReader
    {
        public async Task<DispatchAuthorizationSnapshot> ReadAsync(
            DbConnection connection,
            DbTransaction transaction,
            Guid actorId,
            Guid organizationId,
            CancellationToken cancellationToken)
        {
            calls.Add("authorization");
            return await inner.ReadAsync(
                connection,
                transaction,
                actorId,
                organizationId,
                cancellationToken);
        }
    }

    private sealed class RecordingIdempotencyAccess(
        IAssignmentIdempotencyAccess inner,
        List<string> calls) : IAssignmentIdempotencyAccess
    {
        public async Task AcquireLockAsync(
            DbConnection connection,
            DbTransaction transaction,
            CreateOwnDriverAssignmentCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("idempotency_lock");
            await inner.AcquireLockAsync(
                connection,
                transaction,
                command,
                cancellationToken);
        }

        public async Task<AssignmentIdempotencyRecord?> FindAsync(
            DbConnection connection,
            DbTransaction transaction,
            CreateOwnDriverAssignmentCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("idempotency_read");
            return await inner.FindAsync(
                connection,
                transaction,
                command,
                cancellationToken);
        }
    }

    private sealed class RecordingVisibilityDataReader(
        IAssignmentVisibilityDataReader inner) : IAssignmentVisibilityDataReader
    {
        public List<string> Calls { get; } = [];

        public async Task<AssignmentOrderVisibilityData> ReadOrderAndPackagesAsync(
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            Guid organizationId,
            Guid orderId,
            CancellationToken cancellationToken)
        {
            Calls.Add("order_packages");
            return await inner.ReadOrderAndPackagesAsync(
                connection,
                transaction,
                organizationId,
                orderId,
                cancellationToken);
        }

        public async Task<Drivers.Application.Eligibility.DriverEligibilitySnapshot?>
            ReadDriverProfileAndDocumentsAsync(
                System.Data.Common.DbConnection connection,
                System.Data.Common.DbTransaction transaction,
                Guid organizationId,
                Guid driverId,
                Guid cityId,
                Guid? serviceAreaId,
                CancellationToken cancellationToken)
        {
            Calls.Add("driver_profile_documents");
            return await inner.ReadDriverProfileAndDocumentsAsync(
                connection,
                transaction,
                organizationId,
                driverId,
                cityId,
                serviceAreaId,
                cancellationToken);
        }
    }

    private sealed class RecordingReplayEvidenceReader(
        IAssignmentReplayEvidenceReader inner) : IAssignmentReplayEvidenceReader
    {
        public List<string> Calls { get; } = [];

        public async Task<AssignmentReplayEvidence> ReadAsync(
            DbConnection connection,
            DbTransaction transaction,
            Guid organizationId,
            Guid assignmentId,
            Guid orderId,
            Guid driverId,
            long costCents,
            CancellationToken cancellationToken)
        {
            Calls.Add("replay_evidence");
            return await inner.ReadAsync(
                connection,
                transaction,
                organizationId,
                assignmentId,
                orderId,
                driverId,
                costCents,
                cancellationToken);
        }
    }

    private sealed class DispatchScenario : IAsyncDisposable
    {
        private readonly SyntheticOrderScenario order;
        private readonly List<Guid> driverUserIds = [];
        private readonly List<Guid> driverDocumentIds = [];

        private DispatchScenario(PostgreSqlContractFixture fixture)
        {
            order = new SyntheticOrderScenario(fixture);
            driverUserIds.Add(DriverUserId);
            driverDocumentIds.Add(DriverDocumentId);
        }

        public Guid OrganizationId => order.OrganizationId;
        public Guid DispatcherUserId => order.UserId;
        public Guid OrderId => order.OrderId;
        public Guid DriverId { get; } = Guid.NewGuid();
        public Guid DriverUserId { get; } = Guid.NewGuid();
        private Guid DriverMembershipId { get; } = Guid.NewGuid();
        private Guid DriverDocumentId { get; } = Guid.NewGuid();
        public Guid OriginLocationId => order.OriginLocationId;
        public Guid DestinationLocationId => order.DestinationLocationId;

        public static async Task<DispatchScenario> CreateAsync(PostgreSqlContractFixture fixture)
        {
            var scenario = new DispatchScenario(fixture);
            await scenario.order.InitializeAsync("READY_FOR_PICKUP", "USED");
            await scenario.ExecuteAdminAsync(
                """
                UPDATE locations.locations SET address_summary='READY origin summary' WHERE id=@origin;
                UPDATE locations.locations SET address_summary='READY destination summary' WHERE id=@destination;
                INSERT INTO orders.package_items(
                  id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
                VALUES (
                  @package,@order,@org,'synthetic package',500,0,
                  '{"length_mm":100,"width_mm":80,"height_mm":60}');
                INSERT INTO identity.users(id,identity_subject,status,created_at)
                VALUES (@driver_user,@subject,'ACTIVE',@created);
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (@membership,@driver_user,@org,'DRIVER','ACTIVE',true,@created);
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                VALUES (@driver,@driver_user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
                INSERT INTO drivers.driver_documents(
                  id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
                VALUES (
                  @document,@driver,@org,'IDENTITY','synthetic/dsp002',
                  decode(repeat('ab',32),'hex'),@expires,'VALID',@created);
                """,
                P("origin", scenario.OriginLocationId),
                P("destination", scenario.DestinationLocationId),
                P("package", Guid.NewGuid()),
                P("order", scenario.OrderId),
                P("org", scenario.OrganizationId),
                P("driver_user", scenario.DriverUserId),
                P("subject", $"dsp002-driver-{scenario.DriverUserId:N}"),
                P("created", OccurredAt.AddDays(-1)),
                P("membership", scenario.DriverMembershipId),
                P("driver", scenario.DriverId),
                P("city", scenario.order.CityId),
                P("document", scenario.DriverDocumentId),
                P("expires", OccurredAt.AddDays(30)));
            return scenario;
        }

        public Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters) =>
            order.ExecuteAdminAsync(sql, parameters);

        public Task SetDispatcherMembershipAsync(string role, string status) =>
            ExecuteAdminAsync(
                """
                UPDATE organizations.organization_memberships
                SET role=@role,status=@status
                WHERE user_id=@user AND organization_id=@org;
                """,
                P("role", role),
                P("status", status),
                P("user", DispatcherUserId),
                P("org", OrganizationId));

        public async Task<IReadOnlyList<Guid>> CreateExternalDriversAsync(int count)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            await ExecuteAdminAsync(
                "UPDATE drivers.driver_profiles SET driver_type='EXTERNAL' WHERE id=@driver;",
                P("driver", DriverId));
            var actors = new List<Guid>(count) { DriverUserId };
            for (var index = 1; index < count; index++)
            {
                var userId = Guid.NewGuid();
                var driverId = Guid.NewGuid();
                var documentId = Guid.NewGuid();
                actors.Add(userId);
                await ExecuteAdminAsync(
                    """
                    INSERT INTO identity.users(id,identity_subject,status,created_at)
                    VALUES (@user,@subject,'ACTIVE',@created);
                    INSERT INTO organizations.organization_memberships(
                      id,user_id,organization_id,role,status,is_default,granted_at)
                    VALUES (@membership,@user,@org,'DRIVER','ACTIVE',true,@created);
                    INSERT INTO drivers.driver_profiles(
                      id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
                    VALUES (@driver,@user,@org,@city,'EXTERNAL','MOTORCYCLE','ACTIVE',@created);
                    INSERT INTO drivers.driver_documents(
                      id,driver_id,org_id,document_type,object_key,sha256,expires_at,status,created_at)
                    VALUES (@document,@driver,@org,'IDENTITY',@object_key,
                      decode(repeat('cd',32),'hex'),@expires,'VALID',@created);
                    """,
                    P("user", userId),
                    P("subject", $"ext001-{userId:N}"),
                    P("created", OccurredAt.AddDays(-1)),
                    P("membership", Guid.NewGuid()),
                    P("org", OrganizationId),
                    P("driver", driverId),
                    P("city", order.CityId),
                    P("document", documentId),
                    P("object_key", $"synthetic/ext001/{driverId:N}"),
                    P("expires", OccurredAt.AddDays(30)));
                driverUserIds.Add(userId);
                driverDocumentIds.Add(documentId);
            }

            return actors;
        }

        public async ValueTask DisposeAsync()
        {
            await ExecuteAdminAsync(
                """
                DELETE FROM dispatch.assignments WHERE order_id=@order;
                DELETE FROM dispatch.external_offers WHERE order_id=@order;
                DELETE FROM drivers.driver_documents WHERE id=ANY(@documents);
                """,
                P("order", OrderId),
                P("documents", driverDocumentIds.ToArray()));
            await order.DisposeAsync();
            await ExecuteAdminAsync(
                "DELETE FROM identity.users WHERE id=ANY(@users);",
                P("users", driverUserIds.ToArray()));
        }
    }
}
