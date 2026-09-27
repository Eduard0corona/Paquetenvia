using System.Text.Json;
using Dispatch.Application.Assignments;
using Dispatch.Infrastructure.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Realtime.Infrastructure.Dispatching;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// D8 (D8-DISPATCH-OUTBOX-CLOSURE), D8-OUTBOX-LANE-DISPATCH, AI12-ASSIGNMENT-TERMINAL-STATES and
/// D8-REASSIGNMENT-NEW-ASSIGNMENT against real PostgreSQL with FORCE RLS.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    private const string ReactionTopic = "dispatch.order-status-reaction-requested";

    [PostgreSqlContractFact]
    public async Task D8_dispatch_lane_routes_only_the_reaction_topic_and_is_worker_only()
    {
        Assert.Equal("DISPATCH", await AdminScalarAsync<string>(
            "SELECT security.resolve_outbox_consumer('dispatch.order-status-reaction-requested')"));
        foreach (var realtime in new[]
        {
            "orders.status-changed", "orders.timeline-event-added", "dispatch.assignment-changed",
            "dispatch.external-offer-changed", "notifications.status-changed", "routes.route-changed",
        })
        {
            Assert.Equal("REALTIME", await AdminScalarAsync<string>(
                "SELECT security.resolve_outbox_consumer(@topic)", P("topic", realtime)));
        }

        Assert.Equal("NOTIFICATIONS", await AdminScalarAsync<string>(
            "SELECT security.resolve_outbox_consumer('orders.created')"));
        Assert.Equal("UNROUTED", await AdminScalarAsync<string>(
            "SELECT security.resolve_outbox_consumer('dispatch.order-status-reaction')"));

        foreach (var signature in new[]
        {
            "security.claim_dispatch_outbox(text,integer,interval)",
            "security.requeue_stale_dispatch_outbox(interval,integer,integer)",
        })
        {
            Assert.Equal("paqueteria_outbox_executor|True|search_path=pg_catalog, platform, security, pg_temp",
                await AdminScalarAsync<string>(
                    """
                    SELECT pg_get_userbyid(p.proowner) || '|' || CASE WHEN p.prosecdef THEN 'True' ELSE 'False' END
                           || '|' || array_to_string(p.proconfig,',')
                    FROM pg_proc p WHERE p.oid=to_regprocedure(@signature)
                    """,
                    P("signature", signature)));
            foreach (var (role, expected) in new[]
            {
                ("paqueteria_worker", true), ("paqueteria_app", false), ("paqueteria_maintenance", false),
                ("paqueteria_bootstrap", false), ("paqueteria_lifecycle_executor", false),
            })
            {
                Assert.Equal(expected, await AdminScalarAsync<bool>(
                    "SELECT has_function_privilege(@role,@signature,'EXECUTE')",
                    P("role", role),
                    P("signature", signature)));
            }

            Assert.False(await AdminScalarAsync<bool>(
                """
                SELECT EXISTS (
                  SELECT 1 FROM pg_proc p, aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) acl
                  WHERE p.oid=to_regprocedure(@signature) AND acl.grantee=0)
                """,
                P("signature", signature)));
        }

        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        await using var app = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", scenario.DispatcherUserId, [scenario.OrganizationId]);
        await using var claim = new NpgsqlCommand(
            "SELECT count(*) FROM security.claim_dispatch_outbox('d8-app',1,interval '30 seconds')",
            app.Connection,
            app.Transaction);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => claim.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task D8_dispatch_lane_claim_lease_retry_and_dead_letter_match_the_other_lanes()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var store = new PostgreSqlDispatchOutboxStore(new DispatchWorkerConnectionFactory(fixture.WorkerConnectionString));
        var dispatchRow = Guid.NewGuid();
        var futureRow = Guid.NewGuid();
        var realtimeRow = Guid.NewGuid();
        await InsertOutboxAsProducerAsync(scenario, dispatchRow, ReactionTopic, "{}", DateTimeOffset.UtcNow.AddMinutes(-1));
        await InsertOutboxAsProducerAsync(scenario, futureRow, ReactionTopic, "{}", DateTimeOffset.UtcNow.AddHours(1));
        await InsertOutboxAsProducerAsync(scenario, realtimeRow, "orders.status-changed", "{}", DateTimeOffset.UtcNow.AddMinutes(-1));

        var claims = await store.ClaimAsync("d8-worker-a", 200, TimeSpan.FromSeconds(30), default);
        var claimed = Assert.Single(claims, message => message.Id == dispatchRow);
        Assert.DoesNotContain(claims, message => message.Id == futureRow || message.Id == realtimeRow);
        Assert.All(claims, message => Assert.Equal(ReactionTopic, message.Topic));
        Assert.Equal(1, claimed.Attempts);
        Assert.NotEqual(Guid.Empty, claimed.LeaseToken);
        Assert.Equal(("PROCESSING", "d8-worker-a"), await OutboxStateAsync(dispatchRow));
        Assert.DoesNotContain(
            await store.ClaimAsync("d8-worker-b", 200, TimeSpan.FromSeconds(30), default),
            message => message.Id == dispatchRow);

        // orders.status-changed keeps its REALTIME lane: only the realtime claim sees it.
        var realtimeClaims = await WorkerQueryIdsAsync(
            "SELECT id FROM security.claim_realtime_outbox('d8-realtime',200,interval '30 seconds')");
        Assert.Contains(realtimeRow, realtimeClaims);
        Assert.DoesNotContain(dispatchRow, realtimeClaims);
        Assert.DoesNotContain(futureRow, realtimeClaims);

        Assert.False(await store.SettleAsync(dispatchRow, Guid.NewGuid(), "PROCESSED", null, null, default));
        await scenario.ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id;",
            P("id", dispatchRow));
        Assert.False(await store.SettleAsync(dispatchRow, claimed.LeaseToken, "PROCESSED", null, null, default));
        await WorkerScalarAsync<int>("SELECT security.requeue_stale_realtime_outbox(interval '0 seconds',1000,10)");
        await WorkerScalarAsync<int>("SELECT security.requeue_stale_unowned_outbox(interval '0 seconds',1000,10)");
        Assert.Equal(("PROCESSING", "d8-worker-a"), await OutboxStateAsync(dispatchRow));
        Assert.True(await store.RequeueStaleAsync(1000, 10, default) >= 1);
        Assert.Equal(("RETRY", (string?)null), await OutboxStateAsync(dispatchRow));
        Assert.Equal("LEASE_EXPIRED", await AdminScalarAsync<string>(
            "SELECT last_error FROM platform.outbox_events WHERE id=@id", P("id", dispatchRow)));

        var retried = Assert.Single(
            await store.ClaimAsync("d8-worker-c", 200, TimeSpan.FromSeconds(30), default),
            message => message.Id == dispatchRow);
        Assert.Equal(2, retried.Attempts);
        await scenario.ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id;",
            P("id", dispatchRow));
        Assert.True(await store.RequeueStaleAsync(1000, 2, default) >= 1);
        Assert.Equal(("DEAD", (string?)null), await OutboxStateAsync(dispatchRow));
        Assert.True(await AdminScalarAsync<bool>(
            "SELECT processed_at IS NOT NULL AND lease_token IS NULL FROM platform.outbox_events WHERE id=@id",
            P("id", dispatchRow)));

        var settled = Guid.NewGuid();
        await InsertOutboxAsProducerAsync(scenario, settled, ReactionTopic, "{}", DateTimeOffset.UtcNow.AddMinutes(-1));
        var settleClaim = Assert.Single(
            await store.ClaimAsync("d8-worker-d", 200, TimeSpan.FromSeconds(30), default),
            message => message.Id == settled);
        Assert.True(await store.SettleAsync(settled, settleClaim.LeaseToken, "PROCESSED", null, null, default));
        Assert.False(await store.SettleAsync(settled, settleClaim.LeaseToken, "PROCESSED", null, null, default));
        Assert.Equal(("PROCESSED", (string?)null), await OutboxStateAsync(settled));
    }

    [PostgreSqlContractFact]
    public async Task D8_unassign_reaction_cancels_once_is_idempotent_rls_isolated_and_stale_safe()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var first = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        Assert.Equal("ACCEPTED", first.Status);

        // ORD-002 unassign (ASSIGNED -> READY_FOR_PICKUP) committed by Orders; Dispatch has not reacted yet.
        var transition = await CommitOrderTransitionAsync(scenario, "ASSIGNED", "READY_FOR_PICKUP", OccurredAt.AddMinutes(5));
        var reaction = await InsertReactionAsync(scenario, transition, first.Id);
        var blocked = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
            CreateAssignmentService(fixture.AppDataSource, now: OccurredAt.AddMinutes(10))
                .CreateOwnDriverAssignmentAsync(Command(scenario), default));
        Assert.Equal(AssignmentConflictCode.ActiveAssignmentExists, blocked.Code);

        // RLS: the same fact attributed to another tenant cannot see or close the assignment.
        var foreignOrganization = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
            VALUES (@id,'D8 foreign','D8 foreign','BUSINESS');
            """,
            P("id", foreignOrganization));
        try
        {
            var foreign = Guid.NewGuid();
            await InsertOutboxAsProducerAsync(
                foreignOrganization,
                scenario.DispatcherUserId,
                foreign,
                ReactionTopic,
                ReactionPayload(scenario, transition, first.Id),
                transition.OccurredAt,
                transition.Version,
                scenario.OrderId);
            Assert.Equal(AssignmentReactionOutcome.NoOp, await ProcessAsync(foreign));
            Assert.Equal("ACCEPTED", await ReadAssignmentStatusAsync(first.Id));
            Assert.Equal(("PROCESSED", (string?)null), await OutboxStateAsync(foreign));
        }
        finally
        {
            await scenario.ExecuteAdminAsync(
                """
                DELETE FROM platform.outbox_events WHERE owner_org_id=@id;
                DELETE FROM organizations.organizations WHERE id=@id;
                """,
                P("id", foreignOrganization));
        }

        Assert.Equal(AssignmentReactionOutcome.Closed, await ProcessAsync(reaction));
        Assert.Equal("CANCELLED", await ReadAssignmentStatusAsync(first.Id));
        Assert.Equal(("PROCESSED", (string?)null), await OutboxStateAsync(reaction));
        await AssertClosureEffectsAsync(scenario, first.Id, "CANCELLED", transition.Version, transition.OccurredAt);

        // AI12-ASSIGNMENT-TERMINAL-STATES: Realtime finds evidence for the closed assignment at the
        // closing order version, with the affected driver still an authorized audience.
        await using (var realtimeConnections = new RealtimeWorkerConnectionFactory(fixture.WorkerConnectionString))
        {
            var evidenceReader = new PostgreSqlRealtimeOutboxEvidenceReader(realtimeConnections);
            var evidence = await evidenceReader.ReadClosedAssignmentAsync(
                scenario.OrganizationId, first.Id, transition.Version, default);
            Assert.NotNull(evidence);
            Assert.Equal("CANCELLED", evidence.Status);
            Assert.Equal(transition.Version, evidence.OrderVersion);
            Assert.Equal(transition.OrderEventId, evidence.OrderEventId);
            Assert.Equal(scenario.DriverId, evidence.DriverId);
            Assert.True(evidence.DriverAudienceAuthorized);
            Assert.Null(await evidenceReader.ReadClosedAssignmentAsync(
                Guid.NewGuid(), first.Id, transition.Version, default));
            Assert.Null(await evidenceReader.ReadClosedAssignmentAsync(
                scenario.OrganizationId, first.Id, transition.Version + 1, default));
        }

        // Idempotent replay: a duplicate delivery of the same fact changes nothing.
        var replay = await InsertReactionAsync(scenario, transition, first.Id);
        Assert.Equal(AssignmentReactionOutcome.NoOp, await ProcessAsync(replay));
        await AssertClosureEffectsAsync(scenario, first.Id, "CANCELLED", transition.Version, transition.OccurredAt);

        var second = await CreateAssignmentService(fixture.AppDataSource, now: OccurredAt.AddMinutes(10))
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("ACCEPTED", second.Status);
        Assert.Equal("ASSIGNED", await ReadOrderStatusAsync(scenario.OrderId));

        // A late redelivery of the old fact never closes the newer assignment.
        var stale = await InsertReactionAsync(scenario, transition, first.Id);
        Assert.Equal(AssignmentReactionOutcome.NoOp, await ProcessAsync(stale));
        Assert.Equal("ACCEPTED", await ReadAssignmentStatusAsync(second.Id));
        Assert.Equal(2, await CountAssignmentsAsync(scenario.OrderId));
        await AssertClosureEffectsAsync(scenario, first.Id, "CANCELLED", transition.Version, transition.OccurredAt);
    }

    [PostgreSqlContractFact]
    public async Task D8_reschedule_cancels_the_previous_assignment_and_reassignment_creates_a_new_one()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var first = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        await CommitOrderTransitionAsync(scenario, "ASSIGNED", "AT_PICKUP", OccurredAt.AddMinutes(1));
        await CommitOrderTransitionAsync(scenario, "AT_PICKUP", "FAILED_ATTEMPT", OccurredAt.AddMinutes(2));
        var rescheduled = await CommitOrderTransitionAsync(
            scenario, "FAILED_ATTEMPT", "RESCHEDULED", OccurredAt.AddMinutes(3));
        var reaction = await InsertReactionAsync(scenario, rescheduled, first.Id);

        // D8-REASSIGNMENT-NEW-ASSIGNMENT: until Dispatch reacts, the old assignment blocks reuse.
        var blocked = await Assert.ThrowsAsync<AssignmentConflictException>(() =>
            CreateAssignmentService(fixture.AppDataSource, now: OccurredAt.AddMinutes(4))
                .CreateOwnDriverAssignmentAsync(Command(scenario), default));
        Assert.Equal(AssignmentConflictCode.ActiveAssignmentExists, blocked.Code);

        Assert.Equal(AssignmentReactionOutcome.Closed, await ProcessAsync(reaction));
        Assert.Equal("CANCELLED", await ReadAssignmentStatusAsync(first.Id));
        await AssertClosureEffectsAsync(scenario, first.Id, "CANCELLED", rescheduled.Version, rescheduled.OccurredAt);

        var second = await CreateAssignmentService(fixture.AppDataSource, now: OccurredAt.AddMinutes(5))
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("ACCEPTED", second.Status);
        Assert.Equal("ASSIGNED", await ReadOrderStatusAsync(scenario.OrderId));
        Assert.Equal("CANCELLED", await ReadAssignmentStatusAsync(first.Id));
        Assert.Equal(2, await CountAssignmentsAsync(scenario.OrderId));
    }

    [PostgreSqlContractFact]
    public async Task D8_delivered_and_returned_complete_while_a_retry_keeps_the_assignment()
    {
        foreach (var (path, expected) in new (string[] Path, string Expected)[]
        {
            (["ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING", "DELIVERED"], "COMPLETED"),
            (["ASSIGNED", "AT_PICKUP", "PICKED_UP", "RETURNING", "RETURNED"], "COMPLETED"),
            (["ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING", "FAILED_ATTEMPT", "DELIVERING"], "ACCEPTED"),
            (["ASSIGNED", "CANCELLED"], "CANCELLED"),
            (["ASSIGNED", "AT_PICKUP", "CANCELLED"], "CANCELLED"),
        })
        {
            await using var scenario = await DispatchScenario.CreateAsync(fixture);
            var assignment = await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario), default);
            CommittedTransition? last = null;
            for (var index = 1; index < path.Length; index++)
            {
                last = await CommitOrderTransitionAsync(
                    scenario, path[index - 1], path[index], OccurredAt.AddMinutes(index));
            }

            var reaction = await InsertReactionAsync(scenario, last!, assignment.Id);
            var outcome = await ProcessAsync(reaction);
            Assert.Equal(
                expected == "ACCEPTED" ? AssignmentReactionOutcome.NoOp : AssignmentReactionOutcome.Closed,
                outcome);
            Assert.Equal(expected, await ReadAssignmentStatusAsync(assignment.Id));
            Assert.Equal(("PROCESSED", (string?)null), await OutboxStateAsync(reaction));
            if (expected != "ACCEPTED")
            {
                await AssertClosureEffectsAsync(scenario, assignment.Id, expected, last!.Version, last.OccurredAt);
            }

            var replay = await InsertReactionAsync(scenario, last!, assignment.Id);
            Assert.Equal(AssignmentReactionOutcome.NoOp, await ProcessAsync(replay));
            Assert.Equal(expected, await ReadAssignmentStatusAsync(assignment.Id));
        }
    }

    [PostgreSqlContractFact]
    public async Task D8_malformed_reaction_rows_are_dead_lettered_without_touching_assignments()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var assignment = await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        var transition = await CommitOrderTransitionAsync(scenario, "ASSIGNED", "CANCELLED", OccurredAt.AddMinutes(1));
        var malformed = Guid.NewGuid();
        var payload = ReactionPayload(scenario, transition, assignment.Id)
            .Replace("order-status-reaction-v1", "order-status-reaction-v9", StringComparison.Ordinal);
        await InsertOutboxAsProducerAsync(
            scenario.OrganizationId,
            scenario.DispatcherUserId,
            malformed,
            ReactionTopic,
            payload,
            transition.OccurredAt,
            transition.Version,
            scenario.OrderId);

        Assert.Null(await ProcessAsync(malformed, expectOutcome: false));
        Assert.Equal(("DEAD", (string?)null), await OutboxStateAsync(malformed));
        Assert.Equal(AssignmentLifecycleErrorCodes.InvalidPayload, await AdminScalarAsync<string>(
            "SELECT last_error FROM platform.outbox_events WHERE id=@id", P("id", malformed)));
        Assert.Equal("ACCEPTED", await ReadAssignmentStatusAsync(assignment.Id));
    }

    private sealed record CommittedTransition(
        Guid OrderEventId,
        string PreviousStatus,
        string NewStatus,
        int Version,
        DateTimeOffset OccurredAt);

    /// <summary>Stands in for a committed ORD-002 transition: order row plus its append-only event.</summary>
    private async Task<CommittedTransition> CommitOrderTransitionAsync(
        DispatchScenario scenario,
        string previous,
        string next,
        DateTimeOffset occurredAt)
    {
        var eventId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
            """
            UPDATE orders.orders SET status=@next,version=version+1 WHERE id=@order AND status=@previous;
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,
              public_event_code,payload,actor_id,occurred_at)
            SELECT @event,o.id,o.owner_org_id,NULL,o.version,'ORDER_STATUS_CHANGED',NULL,
                   jsonb_build_object('previous_status',@previous,'new_status',@next,'reason_redacted','d8'),
                   @actor,@occurred
            FROM orders.orders o WHERE o.id=@order AND o.status=@next;
            """,
            P("next", next),
            P("previous", previous),
            P("order", scenario.OrderId),
            P("event", eventId),
            P("actor", scenario.DispatcherUserId),
            P("occurred", occurredAt));
        var version = await AdminScalarAsync<int>(
            "SELECT aggregate_version FROM orders.order_events WHERE id=@event", P("event", eventId));
        return new(eventId, previous, next, version, occurredAt);
    }

    private static string ReactionPayload(DispatchScenario scenario, CommittedTransition transition, Guid assignmentId) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schema_version"] = "order-status-reaction-v1",
            ["order_event_id"] = transition.OrderEventId,
            ["order_id"] = scenario.OrderId,
            ["previous_status"] = transition.PreviousStatus,
            ["new_status"] = transition.NewStatus,
            ["occurred_at"] = transition.OccurredAt,
            ["assignment_id"] = assignmentId,
        });

    private async Task<Guid> InsertReactionAsync(
        DispatchScenario scenario,
        CommittedTransition transition,
        Guid assignmentId)
    {
        var id = Guid.NewGuid();
        await InsertOutboxAsProducerAsync(
            scenario.OrganizationId,
            scenario.DispatcherUserId,
            id,
            ReactionTopic,
            ReactionPayload(scenario, transition, assignmentId),
            transition.OccurredAt,
            transition.Version,
            scenario.OrderId);
        return id;
    }

    private Task InsertOutboxAsProducerAsync(
        DispatchScenario scenario,
        Guid id,
        string topic,
        string payload,
        DateTimeOffset availableAt) =>
        InsertOutboxAsProducerAsync(
            scenario.OrganizationId,
            scenario.DispatcherUserId,
            id,
            topic,
            payload,
            availableAt,
            1,
            scenario.OrderId);

    /// <summary>Runtime producers insert with every value supplied and without RETURNING.</summary>
    private async Task InsertOutboxAsProducerAsync(
        Guid organizationId,
        Guid userId,
        Guid id,
        string topic,
        string payload,
        DateTimeOffset availableAt,
        int aggregateVersion,
        Guid orderId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource, "paqueteria_app", userId, [organizationId]);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
              priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,
              last_error,created_at,processed_at)
            VALUES (@id,@org,jsonb_build_object('organization_ids',jsonb_build_array(@org::text)),@topic,'Order',
                    @order,@version,@payload::jsonb,100,'PENDING',0,@available,NULL,NULL,NULL,NULL,NULL,@created,NULL)
            """,
            tenant.Connection,
            tenant.Transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("topic", topic);
        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("version", aggregateVersion);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("available", availableAt);
        command.Parameters.AddWithValue("created", availableAt.AddMinutes(-1));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await tenant.CommitAsync();
    }

    private async Task<AssignmentReactionOutcome?> ProcessAsync(Guid outboxId, bool expectOutcome = true)
    {
        // The Worker registration itself (D8-OUTBOX-LANE-DISPATCH), without starting its hosted loop.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaqueteriaWorker"] = fixture.WorkerConnectionString,
                ["Dispatch:AssignmentLifecycle:Provider"] = "PostgreSql",
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddDispatchAssignmentLifecycleWorker(configuration);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var store = provider.GetRequiredService<IDispatchOutboxStore>();
        var processor = provider.GetRequiredService<AssignmentLifecycleProcessor>();
        var claims = await store.ClaimAsync("d8-contract", 200, TimeSpan.FromSeconds(60), default);
        var message = Assert.Single(claims, claim => claim.Id == outboxId);
        foreach (var other in claims.Where(claim => claim.Id != outboxId))
        {
            // Leave rows owned by other tests exactly as claimable as before.
            await store.SettleAsync(other.Id, other.LeaseToken, "RETRY", "D8_CONTRACT_RELEASE", DateTimeOffset.UtcNow, default);
        }

        var outcome = await processor.ProcessAsync(message, default);
        Assert.Equal(expectOutcome, outcome is not null);
        return outcome;
    }

    private async Task AssertClosureEffectsAsync(
        DispatchScenario scenario,
        Guid assignmentId,
        string status,
        int orderVersion,
        DateTimeOffset occurredAt)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM platform.outbox_events x
                WHERE x.owner_org_id=@org AND x.topic='dispatch.assignment-changed'
                  AND x.payload->>'assignment_id'=@assignment_text
                  AND x.payload->>'assignment_status'=@status),
              (SELECT min(x.aggregate_version) FROM platform.outbox_events x
                WHERE x.owner_org_id=@org AND x.topic='dispatch.assignment-changed'
                  AND x.payload->>'assignment_id'=@assignment_text
                  AND x.payload->>'assignment_status'=@status),
              (SELECT min((x.payload->>'occurred_at')::timestamptz) FROM platform.outbox_events x
                WHERE x.owner_org_id=@org AND x.topic='dispatch.assignment-changed'
                  AND x.payload->>'assignment_id'=@assignment_text
                  AND x.payload->>'assignment_status'=@status),
              (SELECT count(*) FROM platform.audit_logs a
                WHERE a.org_id=@org AND a.entity_id=@assignment AND a.action='ASSIGNMENT_CLOSED'
                  AND a.actor_id IS NULL AND a.payload_redacted->>'status'=@status)
            """);
        command.Parameters.AddWithValue("org", scenario.OrganizationId);
        command.Parameters.AddWithValue("assignment", assignmentId);
        command.Parameters.AddWithValue("assignment_text", assignmentId.ToString("D"));
        command.Parameters.AddWithValue("status", status);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(orderVersion, reader.GetInt32(1));
        Assert.Equal(occurredAt.UtcDateTime, reader.GetFieldValue<DateTimeOffset>(2).UtcDateTime);
        Assert.Equal(1L, reader.GetInt64(3));
    }

    private async Task<(string Status, string? LockedBy)> OutboxStateAsync(Guid id)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT status,locked_by FROM platform.outbox_events WHERE id=@id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T> WorkerScalarAsync<T>(string sql)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return value;
    }

    private async Task<IReadOnlyList<Guid>> WorkerQueryIdsAsync(string sql)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        var ids = new List<Guid>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync();
        return ids;
    }

    private async Task<string> ReadAssignmentStatusAsync(Guid assignmentId) =>
        await AdminScalarAsync<string>(
            "SELECT status FROM dispatch.assignments WHERE id=@id;", P("id", assignmentId));

    private async Task<string> ReadOrderStatusAsync(Guid orderId) =>
        await AdminScalarAsync<string>(
            "SELECT status FROM orders.orders WHERE id=@id;", P("id", orderId));
}
