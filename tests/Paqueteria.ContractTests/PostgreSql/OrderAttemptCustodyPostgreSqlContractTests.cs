using Dispatch.Infrastructure.Persistence;
using Dispatch.Infrastructure.Stops;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure;
using Incidents.Infrastructure.Incidents;
using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// ORD-002 proofs per attempt, a single custody derivation shared by ORD-002, INC-001 and the
/// driver stops view, DSP-002 driver eligibility towards ASSIGNED, one incident per failed attempt
/// with its next action honoured, the EXTERNAL realtime audience, the incident order lock and
/// the route order of the driver stops — all against a real PostgreSQL baseline and driven through
/// the real services with the synthetic dispatcher of each scenario.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class OrderAttemptCustodyPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    // ------------------------------------------------------------ 1. proofs per attempt

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Delivered_rejects_a_delivery_photo_of_an_earlier_failed_attempt()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        // The immediate retry (D8-RESCHEDULED-NO-DIRECT-DELIVERY removed RESCHEDULED -> DELIVERING).
        await TransitionAsync(world, OrderStatus.Delivering);

        var stale = await RefusedAsync(world, OrderStatus.Delivered);
        Assert.Equal("delivery_proof_complete", stale.GuardCode);

        await InsertProofAsync(world, "DELIVERY_PHOTO");
        var delivered = await TransitionAsync(world, OrderStatus.Delivered);
        Assert.Equal("DELIVERED", delivered.Status);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Delivered_rejects_a_delivery_photo_used_as_incident_evidence()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);

        var conflict = await RefusedAsync(world, OrderStatus.Delivered);

        Assert.Equal("delivery_proof_complete", conflict.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Picked_up_rejects_a_pickup_photo_of_an_earlier_failed_attempt()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("AT_PICKUP"));
        var evidence = await InsertProofAsync(world, "PICKUP_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        await TransitionAsync(world, OrderStatus.Rescheduled);
        await TransitionAsync(world, OrderStatus.Assigned);
        await TransitionAsync(world, OrderStatus.AtPickup);

        var stale = await RefusedAsync(world, OrderStatus.PickedUp);
        Assert.Equal("pickup_proof_complete", stale.GuardCode);

        await InsertProofAsync(world, "PICKUP_PHOTO");
        var pickedUp = await TransitionAsync(world, OrderStatus.PickedUp);
        Assert.Equal("PICKED_UP", pickedUp.Status);
    }

    // ------------------------------------------ 2. custody comes from the PICKED_UP event

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_pickup_incident_photo_is_not_custody_so_the_order_can_still_be_cancelled()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("AT_PICKUP"));
        var evidence = await InsertProofAsync(world, "PICKUP_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        Assert.False(incident.CustodyAcquired);

        var cancelled = await TransitionAsync(world, OrderStatus.Cancelled);

        Assert.Equal("CANCELLED", cancelled.Status);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_pickup_incident_photo_never_unlocks_returning_or_redelivery()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("AT_PICKUP"));
        var evidence = await InsertProofAsync(world, "PICKUP_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        var returning = await RefusedAsync(world, OrderStatus.Returning);
        Assert.Equal("custody_acquired_true", returning.GuardCode);
        var redelivery = await RefusedAsync(world, OrderStatus.Delivering);
        Assert.Equal("retry_custody_acquired_true", redelivery.GuardCode);

        // D8-RESCHEDULED-NO-DIRECT-DELIVERY: RESCHEDULED -> DELIVERING is not an edge at all, so it is
        // refused before any guard, with or without custody.
        await TransitionAsync(world, OrderStatus.Rescheduled);
        await using var scope = CreateTransitionScope();
        var fromRescheduled = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            scope.Service.TransitionAsync(
                TransitionCommand(world, OrderStatus.Delivering, null),
                CancellationToken.None));
        Assert.Equal(OrderTransitionConflictCode.InvalidState, fromRescheduled.Code);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_driver_stop_of_a_pickup_failure_stays_a_pickup_at_the_origin()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("AT_PICKUP"));
        var evidence = await InsertProofAsync(world, "PICKUP_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        var stop = Assert.Single(await CreateStopsQuery().ListCurrentDriverStopsAsync(
            world.DriverUserId,
            world.Scenario.OrganizationId,
            CancellationToken.None));

        Assert.Equal("FAILED_ATTEMPT", stop.Status);
        Assert.Equal("PICKUP", stop.StopType);
        Assert.Equal("Synthetic origin", stop.AddressSummary);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_parcel_picked_up_in_an_earlier_attempt_keeps_custody_at_a_new_pickup()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            [.. OrderStatusHistory.ToDelivering, "FAILED_ATTEMPT", "RESCHEDULED", "ASSIGNED", "AT_PICKUP"]);
        var evidence = await InsertProofAsync(world, "PICKUP_PHOTO");

        // INC-001 derives custody from the same PICKED_UP history ORD-002 reads.
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Returning);
        Assert.True(incident.CustodyAcquired);

        var cancel = await RefusedAsync(world, OrderStatus.Cancelled);
        Assert.Equal("if_from_at_pickup_then_custody_not_acquired", cancel.GuardCode);

        var stop = Assert.Single(await CreateStopsQuery().ListCurrentDriverStopsAsync(
            world.DriverUserId,
            world.Scenario.OrganizationId,
            CancellationToken.None));
        Assert.Equal("PICKUP", stop.StopType);

        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        var returning = await TransitionAsync(world, OrderStatus.Returning);
        Assert.Equal("RETURNING", returning.Status);
    }

    // ------------------------------------------------ 3. DSP-002 eligibility towards ASSIGNED

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Assigned_rejects_a_driver_with_an_expired_document()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("READY_FOR_PICKUP"),
            documentExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var conflict = await RefusedAsync(world, OrderStatus.Assigned);

        Assert.Equal("eligible_driver", conflict.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Assigned_rejects_packages_beyond_the_vehicle_capacity()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("READY_FOR_PICKUP"),
            packageWeightGrams: 10_000);

        var conflict = await RefusedAsync(world, OrderStatus.Assigned);

        Assert.Equal("capacity_available", conflict.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Assigned_accepts_a_driver_the_eligibility_policy_accepts()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("READY_FOR_PICKUP"));

        var assigned = await TransitionAsync(world, OrderStatus.Assigned);

        Assert.Equal("ASSIGNED", assigned.Status);
    }

    // ---------------------------------- 4. one incident per failed attempt and its next action

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task One_incident_justifies_exactly_one_failed_attempt()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        await TransitionAsync(world, OrderStatus.Delivering);

        var reused = await RefusedAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        Assert.Equal("attempt_stage_recorded", reused.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_incident_opened_in_an_earlier_stage_does_not_justify_the_current_attempt()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("IN_TRANSIT"));
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.Delivering);

        var stale = await RefusedAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        Assert.Equal("attempt_stage_recorded", stale.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_rescheduled_next_action_refuses_returning()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Rescheduled);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        var returning = await RefusedAsync(world, OrderStatus.Returning);
        Assert.Equal("failed_attempt_next_action_respected", returning.GuardCode);

        var rescheduled = await TransitionAsync(world, OrderStatus.Rescheduled);
        Assert.Equal("RESCHEDULED", rescheduled.Status);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_returning_next_action_refuses_rescheduling_and_redelivery()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenIncidentAsync(world, [evidence], IncidentContract.Returning);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        var rescheduled = await RefusedAsync(world, OrderStatus.Rescheduled);
        Assert.Equal("failed_attempt_next_action_respected", rescheduled.GuardCode);
        var redelivery = await RefusedAsync(world, OrderStatus.Delivering);
        Assert.Equal("failed_attempt_next_action_respected", redelivery.GuardCode);

        var returning = await TransitionAsync(world, OrderStatus.Returning);
        Assert.Equal("RETURNING", returning.Status);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_adopted_incident_backfilled_as_returning_does_not_bind_rescheduling()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var adopted = await InsertAdoptedIncidentAsync(world);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(adopted));

        var rescheduled = await TransitionAsync(world, OrderStatus.Rescheduled);

        Assert.Equal("RESCHEDULED", rescheduled.Status);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_adopted_incident_backfilled_as_returning_does_not_bind_redelivery()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var adopted = await InsertAdoptedIncidentAsync(world);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(adopted));

        var redelivery = await TransitionAsync(world, OrderStatus.Delivering);

        Assert.Equal("DELIVERING", redelivery.Status);
    }

    // ------------------------------------------ 4b. recorded times follow the order lock

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_transition_that_waited_for_an_incident_is_recorded_after_it()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("IN_TRANSIT"));
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incidentId = Guid.NewGuid();

        // An INC-001 opening holds the order FOR SHARE; the transition reads its clock first and
        // then waits. The incident is committed with a later time than that clock reading.
        await using var opener = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await opener.BeginTransactionAsync();
        await ExecuteAsync(opener, transaction,
            "SELECT 1 FROM orders.orders WHERE id=@order FOR SHARE",
            SyntheticOrderScenario.P("order", world.Scenario.OrderId));

        await using var scope = CreateTransitionScope();
        var transition = scope.Service.TransitionAsync(
            TransitionCommand(world, OrderStatus.Delivering, null),
            CancellationToken.None);
        await Task.WhenAny(transition, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.False(transition.IsCompleted);
        await InsertOpenIncidentAsync(opener, transaction, world, incidentId, evidence);
        await transaction.CommitAsync();

        var delivering = await transition;
        world.Version = delivering.Version;

        Assert.True(await ScalarAsync<bool>(
            """
            SELECT e.occurred_at > i.created_at
            FROM orders.order_events e, incidents.incidents i
            WHERE e.order_id=@order AND e.aggregate_version=@version AND i.id=@incident
            """,
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("version", delivering.Version),
            SyntheticOrderScenario.P("incident", incidentId)));
        // The incident belongs to the previous stage, so it cannot justify this attempt.
        var stale = await RefusedAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incidentId));
        Assert.Equal("attempt_stage_recorded", stale.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_incident_that_waited_for_a_transition_is_recorded_after_it()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("IN_TRANSIT"));
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");

        // A transition into DELIVERING holds the order FOR UPDATE; the opening reads its clock
        // first and then waits. The status change is committed with a later time than that.
        await using var writer = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await ExecuteAsync(writer, transaction,
            "UPDATE orders.orders SET status='DELIVERING',version=version+1 WHERE id=@order",
            SyntheticOrderScenario.P("order", world.Scenario.OrderId));

        await using var scope = CreateIncidentScope();
        var opening = scope.Service.OpenAsync(
            OpenCommand(world, [evidence], IncidentContract.Rescheduled),
            CancellationToken.None);
        await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.False(opening.IsCompleted);
        await ExecuteAsync(writer, transaction,
            """
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,aggregate_version,event_type,payload,actor_id,occurred_at)
            VALUES (
              gen_random_uuid(),@order,@org,@version,'ORDER_STATUS_CHANGED',
              jsonb_build_object('previous_status','IN_TRANSIT','new_status','DELIVERING'),
              @actor,clock_timestamp())
            """,
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
            SyntheticOrderScenario.P("version", world.Version + 1),
            SyntheticOrderScenario.P("actor", world.Scenario.UserId));
        await transaction.CommitAsync();
        world.Version++;

        var incident = await opening;

        // Opened in the attempt the status change started, so it justifies that attempt.
        var failed = await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        Assert.Equal("FAILED_ATTEMPT", failed.Status);
    }

    // ------------------------------------------------------ 5. EXTERNAL realtime audience

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_realtime_audience_includes_the_active_external_driver()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("ASSIGNED"),
            driverType: "EXTERNAL");

        var result = await TransitionAsync(world, OrderStatus.AtPickup);

        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT payload->>'authorized_driver_id',payload->>'assignment_id'
            FROM platform.outbox_events
            WHERE aggregate_id=@order AND aggregate_version=@version AND topic='orders.status-changed'
            """);
        command.Parameters.AddWithValue("order", world.Scenario.OrderId);
        command.Parameters.AddWithValue("version", result.Version);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(world.DriverId.ToString("D"), reader.IsDBNull(0) ? null : reader.GetString(0));
        Assert.Equal(world.AssignmentId.ToString("D"), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    // ------------------------------------------------ 6. incident opening locks the order

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Opening_an_incident_reads_the_order_under_a_share_lock()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var evidence = await InsertProofAsync(world, "DELIVERY_PHOTO");

        // A concurrent writer moves the order out of DELIVERING and has not committed yet.
        await using var writer = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand(
                         "UPDATE orders.orders SET status='DELIVERED',version=version+1 WHERE id=@order",
                         writer,
                         transaction))
        {
            update.Parameters.AddWithValue("order", world.Scenario.OrderId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        await using var scope = CreateIncidentScope();
        var opening = scope.Service.OpenAsync(
            OpenCommand(world, [evidence], IncidentContract.Rescheduled),
            CancellationToken.None);
        await Task.WhenAny(opening, Task.Delay(TimeSpan.FromSeconds(1)));
        await transaction.CommitAsync();

        // The opening waited for the writer and then saw the delivered order.
        var failure = await Assert.ThrowsAsync<IncidentConflictException>(() => opening);
        Assert.Equal("ORDER_STATE_NOT_ALLOWED", failure.Code);
    }

    // ------------------------------------------------------- 7. stops follow the route

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Driver_stops_follow_the_route_sequence_before_assignment_age()
    {
        await using var world = await AttemptWorld.CreateAsync(
            fixture,
            OrderStatusHistory.CanonicalPathTo("ASSIGNED"));
        var sibling = await world.AddSiblingAssignmentAsync();
        var unrouted = await world.AddSiblingAssignmentAsync();
        await world.AddRouteAsync([sibling, world.Scenario.OrderId]);

        var stops = await CreateStopsQuery().ListCurrentDriverStopsAsync(
            world.DriverUserId,
            world.Scenario.OrganizationId,
            CancellationToken.None);

        Assert.Equal(
            [sibling, world.Scenario.OrderId, unrouted],
            stops.Select(stop => stop.OrderId).ToArray());
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Driver_stops_follow_the_sequence_of_the_stop_they_are_heading_to()
    {
        // The main order is in custody, so its stop is DELIVERY (sequence 4) even though its
        // PICKUP stop comes first on the route; the sibling still has to be picked up (sequence 2).
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var sibling = await world.AddSiblingAssignmentAsync();
        await world.AddTypedRouteAsync(
        [
            (world.Scenario.OrderId, "PICKUP"),
            (sibling, "PICKUP"),
            (sibling, "DELIVERY"),
            (world.Scenario.OrderId, "DELIVERY"),
        ]);

        var stops = await CreateStopsQuery().ListCurrentDriverStopsAsync(
            world.DriverUserId,
            world.Scenario.OrganizationId,
            CancellationToken.None);

        Assert.Equal(
            [(sibling, "PICKUP"), (world.Scenario.OrderId, "DELIVERY")],
            stops.Select(stop => (stop.OrderId, stop.StopType)).ToArray());
    }

    // ------------------------------------------------------------------- helpers

    private static string IncidentMetadata(IncidentResult incident) => IncidentMetadata(incident.Id);

    private static string IncidentMetadata(Guid incidentId) =>
        $$"""{"incident_id":"{{incidentId:D}}"}""";

    /// <summary>
    /// An incident as a pre-INC-001 installation left it after the INC-001 adoption: no evidence
    /// (the evidence table did not exist yet) and the next action the backfill derived from
    /// custody, here RETURNING. The row predates the evidence trigger, so the seed skips triggers
    /// for its own transaction only; foreign keys and check constraints still hold.
    /// </summary>
    private async Task<Guid> InsertAdoptedIncidentAsync(AttemptWorld world)
    {
        var incidentId = Guid.NewGuid();
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role=replica");
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              created_by,created_at,reason_code,next_action,occurred_at,sla_due_at)
            VALUES (
              @incident,@order,@org,'FAILED_DELIVERY_ATTEMPT','MEDIUM','OPEN',true,
              @user,clock_timestamp(),'FAILED_DELIVERY_ATTEMPT','RETURNING',
              clock_timestamp(),clock_timestamp()+interval '24 hours')
            """,
            SyntheticOrderScenario.P("incident", incidentId),
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
            SyntheticOrderScenario.P("user", world.Scenario.UserId));
        await transaction.CommitAsync();
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT count(*) FROM incidents.incident_evidence WHERE incident_id=@incident",
            SyntheticOrderScenario.P("incident", incidentId)));
        return incidentId;
    }

    /// <summary>A pending INC-001 incident with its evidence, written inside the caller's transaction.</summary>
    private static Task InsertOpenIncidentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AttemptWorld world,
        Guid incidentId,
        Guid proofId) =>
        ExecuteAsync(connection, transaction,
            """
            INSERT INTO incidents.incidents(
              id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
              created_by,created_at,reason_code,next_action,occurred_at,sla_due_at)
            VALUES (
              @incident,@order,@org,'FAILED_DELIVERY_ATTEMPT','MEDIUM','OPEN',true,
              @user,clock_timestamp(),'RECIPIENT_ABSENT','RESCHEDULED',
              clock_timestamp(),clock_timestamp()+interval '24 hours');
            INSERT INTO incidents.incident_evidence(
              id,incident_id,order_id,owner_org_id,operator_org_id,proof_id,created_by,created_at)
            VALUES (gen_random_uuid(),@incident,@order,@org,NULL,@proof,@user,clock_timestamp());
            """,
            SyntheticOrderScenario.P("incident", incidentId),
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
            SyntheticOrderScenario.P("user", world.Scenario.UserId),
            SyntheticOrderScenario.P("proof", proofId));

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<OrderResult> TransitionAsync(
        AttemptWorld world,
        OrderStatus target,
        string? metadata = null)
    {
        await using var scope = CreateTransitionScope();
        var result = await scope.Service.TransitionAsync(
            TransitionCommand(world, target, metadata),
            CancellationToken.None);
        world.Version = result.Version;
        return result;
    }

    private async Task<OrderTransitionConflictException> RefusedAsync(
        AttemptWorld world,
        OrderStatus target,
        string? metadata = null)
    {
        await using var scope = CreateTransitionScope();
        var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            scope.Service.TransitionAsync(
                TransitionCommand(world, target, metadata),
                CancellationToken.None));
        Assert.Equal(OrderTransitionConflictCode.GuardNotSatisfied, conflict.Code);
        return conflict;
    }

    private static TransitionOrderCommand TransitionCommand(
        AttemptWorld world,
        OrderStatus target,
        string? metadata) =>
        new(
            world.Scenario.UserId,
            world.Scenario.OrganizationId,
            $"ord002-attempt-{Guid.NewGuid():N}",
            world.Scenario.OrderId,
            target.ToContractValue(),
            "synthetic attempt reason",
            world.Version,
            metadata,
            true,
            "synthetic-request-id");

    private async Task<IncidentResult> OpenIncidentAsync(
        AttemptWorld world,
        IReadOnlyList<Guid> evidence,
        string nextAction)
    {
        await using var scope = CreateIncidentScope();
        return await scope.Service.OpenAsync(
            OpenCommand(world, evidence, nextAction),
            CancellationToken.None);
    }

    private static OpenIncidentCommand OpenCommand(
        AttemptWorld world,
        IReadOnlyList<Guid> evidence,
        string nextAction) =>
        new(
            world.Scenario.UserId,
            world.Scenario.OrganizationId,
            MfaSatisfied: true,
            $"inc001-attempt-{Guid.NewGuid():N}",
            world.Scenario.OrderId,
            "FAILED_DELIVERY_ATTEMPT",
            IncidentContract.Medium,
            IncidentsPostgreSqlContractTests.TestDescription,
            IncidentContract.RecipientAbsent,
            nextAction,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            evidence,
            "synthetic-request-id");

    /// <summary>
    /// A proof of the scenario's order as POD-001 would finalize it: its creation time comes from
    /// the application clock, after whatever transition happened before this call.
    /// </summary>
    private static async Task<Guid> InsertProofAsync(AttemptWorld world, string proofType)
    {
        var uploadId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
        await world.Scenario.ExecuteAdminAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,
              expected_content_type,maximum_bytes,status,expires_at)
            VALUES (
              @upload,@order,@org,@user,@quarantine,'image/jpeg',1024,'CONSUMED',clock_timestamp()+interval '1 day');
            INSERT INTO custody.proofs(
              id,order_id,owner_org_id,upload_session_id,proof_type,object_key,sha256,
              content_type,size_bytes,captured_at,created_by,created_at)
            VALUES (
              @proof,@order,@org,@upload,@proof_type,@object_key,
              decode(repeat('06',32),'hex'),'image/jpeg',100,@created,@user,@created);
            """,
            SyntheticOrderScenario.P("upload", uploadId),
            SyntheticOrderScenario.P("proof", proofId),
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
            SyntheticOrderScenario.P("user", world.Scenario.UserId),
            SyntheticOrderScenario.P("quarantine", $"quarantine/{uploadId:N}"),
            SyntheticOrderScenario.P("proof_type", proofType),
            SyntheticOrderScenario.P("object_key", $"proofs/{uploadId:N}"),
            SyntheticOrderScenario.P("created", UtcMicrosecondPrecision.Normalize(DateTimeOffset.UtcNow)));
        return proofId;
    }

    internal static OrderTransitionDriverEligibilityOptions EligibilityOptions() => new()
    {
        PolicyVersion = "ord-002-contract-v1",
        RequiredDocumentTypesByVehicleType = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = ["IDENTITY"],
        },
        VehicleCapacity = new(StringComparer.Ordinal)
        {
            ["MOTORCYCLE"] = new OrderTransitionVehicleCapacityOptions
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

    private Scope<IOrderTransitionService> CreateTransitionScope()
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new OrdersDbContext(options, state);
        var service = new PostgreSqlOrderTransitionService(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new PostgreSqlOrderTransitionAuthorizationReader(),
            new PostgreSqlOrderTransitionReplayAuthorizationReader(),
            new PostgreSqlOrderQuoteAcceptanceGuardReader(),
            new PostgreSqlOrderAssignmentGuardReader(Options.Create(EligibilityOptions())),
            new PostgreSqlOrderProofGuardReader(),
            new PostgreSqlOrderCustodyGuardReader(),
            new PostgreSqlOrderIncidentGuardReader(),
            new PostgreSqlOrderCodGuardReader(),
            new OrderTransitionAuthorizer(),
            new OrderTransitionGuardRegistry(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new NoOpOrderTransitionFailureInjector(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                IdempotencyLifetimeMinutes = 60,
                ClaimWindowHours = 72,
                TransitionMetadataMaximumBytes = 4_096,
            }),
            new SystemClock());
        return new Scope<IOrderTransitionService>(context, service);
    }

    private Scope<IIncidentService> CreateIncidentScope()
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<IncidentsDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new IncidentsDbContext(options, state);
        var service = new PostgreSqlIncidentService(
            new TenantTransactionContext<IncidentsDbContext>(context, state),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new DeterministicMockIncidentPiiProtector(),
            Options.Create(new IncidentsOptions
            {
                PiiProtector = IncidentPiiProtectorKind.Mock,
                PiiKeyVersion = IncidentsPostgreSqlContractTests.TestKeyVersion,
            }),
            new SystemClock());
        return new Scope<IIncidentService>(context, service);
    }

    private PostgreSqlDriverStopsQuery CreateStopsQuery()
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        return new PostgreSqlDriverStopsQuery(
            new TenantTransactionContext<DispatchDbContext>(new DispatchDbContext(options, state), state),
            NullLogger<PostgreSqlDriverStopsQuery>.Instance);
    }

    private sealed class Scope<TService>(DbContext context, TService service) : IAsyncDisposable
    {
        internal TService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    /// <summary>
    /// A scenario order with a real history, an ACTIVE assignment to a synthetic driver with an
    /// active DRIVER membership, one IDENTITY document and one package, so every ORD-002 guard
    /// reads real rows. Anything added beyond <see cref="SyntheticOrderScenario"/> is removed here.
    /// </summary>
    private sealed class AttemptWorld : IAsyncDisposable
    {
        private readonly List<Guid> routeIds = [];

        private AttemptWorld(PostgreSqlContractFixture fixture) => Scenario = new(fixture);

        public SyntheticOrderScenario Scenario { get; }
        public Guid DriverUserId { get; } = Guid.NewGuid();
        public Guid DriverId { get; } = Guid.NewGuid();
        public Guid AssignmentId { get; } = Guid.NewGuid();
        public int Version { get; set; }
        private string DriverType { get; init; } = "OWN";

        public static async Task<AttemptWorld> CreateAsync(
            PostgreSqlContractFixture fixture,
            IReadOnlyList<string> history,
            string driverType = "OWN",
            DateTimeOffset? documentExpiresAt = null,
            int packageWeightGrams = 500)
        {
            var world = new AttemptWorld(fixture) { DriverType = driverType };
            await world.Scenario.InitializeAsync(history[^1], "USED");
            await world.Scenario.ExecuteAdminAsync(
                """
                INSERT INTO identity.users(id,identity_subject,status)
                VALUES (@driver_user,@subject,'ACTIVE');
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default)
                VALUES (gen_random_uuid(),@driver_user,@org,'DRIVER','ACTIVE',true);
                INSERT INTO drivers.driver_profiles(
                  id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
                VALUES (@driver,@driver_user,@org,@city,@driver_type,'MOTORCYCLE','ACTIVE');
                INSERT INTO drivers.driver_documents(
                  id,driver_id,org_id,document_type,object_key,sha256,expires_at,status)
                VALUES (
                  gen_random_uuid(),@driver,@org,'IDENTITY',@document_key,
                  decode(repeat('ab',32),'hex'),@expires,'VALID');
                INSERT INTO orders.package_items(
                  id,order_id,owner_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
                VALUES (
                  gen_random_uuid(),@order,@org,'synthetic package',@weight,0,
                  '{"length_mm":100,"width_mm":80,"height_mm":60}');
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents,accepted_at,created_at)
                VALUES (
                  @assignment,@order,@org,@driver,@driver_type,'ACTIVE',100,
                  clock_timestamp()-interval '1 hour',clock_timestamp()-interval '1 hour');
                """,
                SyntheticOrderScenario.P("driver_user", world.DriverUserId),
                SyntheticOrderScenario.P("subject", $"ord002-attempt-driver|{world.DriverUserId:N}"),
                SyntheticOrderScenario.P("org", world.Scenario.OrganizationId),
                SyntheticOrderScenario.P("driver", world.DriverId),
                SyntheticOrderScenario.P("city", world.Scenario.CityId),
                SyntheticOrderScenario.P("driver_type", driverType),
                SyntheticOrderScenario.P("document_key", $"synthetic/ord002/{world.DriverId:N}"),
                SyntheticOrderScenario.P("expires", documentExpiresAt ?? DateTimeOffset.UtcNow.AddDays(30)),
                SyntheticOrderScenario.P("order", world.Scenario.OrderId),
                SyntheticOrderScenario.P("weight", packageWeightGrams),
                SyntheticOrderScenario.P("assignment", world.AssignmentId));
            world.Version = await OrderStatusHistory.SeedAsync(world.Scenario, history);
            return world;
        }

        /// <summary>
        /// Another ASSIGNED order of the same tenant, assigned to the same driver after the main
        /// one, so assignment age alone would list it later.
        /// </summary>
        public async Task<Guid> AddSiblingAssignmentAsync()
        {
            var quoteId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            await Scenario.ExecuteAdminAsync(
                """
                INSERT INTO pricing.quotes(
                  id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
                  consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                  minimum_total_cents_snapshot,currency,pricing_policy_version,request_snapshot_redacted,
                  package_snapshot,breakdown,input_hash,status,expires_at)
                SELECT @quote,q.owner_org_id,q.city_id,q.origin_location_id,q.destination_location_id,
                  q.service_type,q.pricing_tier,q.consolidated_route,q.subtotal_cents,q.discount_cents,
                  q.tax_cents,q.total_cents,q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,
                  q.request_snapshot_redacted,q.package_snapshot,q.breakdown,@input_hash,q.status,
                  clock_timestamp()+interval '1 day'
                FROM pricing.quotes q WHERE q.id=@source_quote;
                INSERT INTO orders.orders(
                  id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,
                  service_type,pricing_tier,consolidated_route,payer_type,status,subtotal_cents,discount_cents,
                  tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
                  package_snapshot,cod_expected_cents,version)
                SELECT @order,@public_id,q.id,q.owner_org_id,q.city_id,q.origin_location_id,
                  q.destination_location_id,q.service_type,q.pricing_tier,q.consolidated_route,'SENDER',
                  'ASSIGNED',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
                  q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,0,1
                FROM pricing.quotes q WHERE q.id=@quote;
                INSERT INTO dispatch.assignments(
                  id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents,accepted_at,created_at)
                VALUES (
                  gen_random_uuid(),@order,@org,@driver,@driver_type,'ACTIVE',100,
                  clock_timestamp(),clock_timestamp());
                """,
                SyntheticOrderScenario.P("quote", quoteId),
                SyntheticOrderScenario.P("source_quote", Scenario.QuoteId),
                SyntheticOrderScenario.P("order", orderId),
                SyntheticOrderScenario.P("public_id", $"ORD002-{orderId:N}"),
                SyntheticOrderScenario.P("input_hash", new byte[32]),
                SyntheticOrderScenario.P("org", Scenario.OrganizationId),
                SyntheticOrderScenario.P("driver", DriverId),
                SyntheticOrderScenario.P("driver_type", DriverType));
            return orderId;
        }

        /// <summary>One RTE-001 route of the driver whose DELIVERY stops follow the given order.</summary>
        public Task AddRouteAsync(IReadOnlyList<Guid> orderIds) =>
            AddTypedRouteAsync(orderIds.Select(orderId => (orderId, "DELIVERY")).ToArray());

        /// <summary>One RTE-001 route of the driver whose stops follow the given order and types.</summary>
        public async Task AddTypedRouteAsync(IReadOnlyList<(Guid OrderId, string StopType)> stops)
        {
            var routeId = Guid.NewGuid();
            routeIds.Add(routeId);
            await Scenario.ExecuteAdminAsync(
                """
                INSERT INTO routes.routes(id,operator_org_id,city_id,driver_id,status,version)
                VALUES (@route,@org,@city,@driver,'PLANNED',1);
                """,
                SyntheticOrderScenario.P("route", routeId),
                SyntheticOrderScenario.P("org", Scenario.OrganizationId),
                SyntheticOrderScenario.P("city", Scenario.CityId),
                SyntheticOrderScenario.P("driver", DriverId));
            for (var index = 0; index < stops.Count; index++)
            {
                await Scenario.ExecuteAdminAsync(
                    """
                    INSERT INTO routes.route_stops(
                      id,route_id,order_id,operator_org_id,sequence,stop_type,status)
                    VALUES (gen_random_uuid(),@route,@order,@org,@sequence,@stop_type,'PENDING');
                    UPDATE dispatch.assignments SET route_id=@route
                    WHERE order_id=@order AND status='ACTIVE';
                    """,
                    SyntheticOrderScenario.P("route", routeId),
                    SyntheticOrderScenario.P("order", stops[index].OrderId),
                    SyntheticOrderScenario.P("org", Scenario.OrganizationId),
                    SyntheticOrderScenario.P("sequence", index + 1),
                    SyntheticOrderScenario.P("stop_type", stops[index].StopType));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Scenario.ExecuteAdminAsync(
                """
                DELETE FROM routes.route_stops WHERE route_id=ANY(@routes);
                DELETE FROM dispatch.assignments WHERE owner_org_id=@org;
                DELETE FROM routes.routes WHERE id=ANY(@routes);
                DELETE FROM drivers.driver_documents WHERE org_id=@org;
                """,
                SyntheticOrderScenario.P("routes", routeIds.ToArray()),
                SyntheticOrderScenario.P("org", Scenario.OrganizationId));
            await Scenario.DisposeAsync();
            await Scenario.ExecuteAdminAsync(
                "DELETE FROM identity.users WHERE id=@user;",
                SyntheticOrderScenario.P("user", DriverUserId));
        }
    }
}
