using System.Text;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure;
using Incidents.Infrastructure.Incidents;
using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
/// INC-001 against a real PostgreSQL baseline: opening an incident, the guards that make reason,
/// evidence, next action and SLA mandatory, idempotent replay, tenant isolation, and the handover
/// to the authoritative ORD-002 failed-attempt transition.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class IncidentsPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Opening_an_incident_persists_reason_next_action_sla_and_evidence()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var now = DateTimeOffset.Parse("2026-09-22T18:00:00Z", Culture);
        await using var scope = CreateIncidentScope(now);

        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId], now.AddMinutes(-5)),
            CancellationToken.None);

        Assert.Equal(IncidentContract.Open, result.Status);
        Assert.Equal(IncidentContract.RecipientAbsent, result.ReasonCode);
        Assert.Equal(IncidentContract.Rescheduled, result.NextAction);
        Assert.True(result.CustodyAcquired);
        // MEDIUM resolves in 24 hours, measured from the attempt rather than from the request.
        Assert.Equal(now.AddMinutes(-5).AddHours(24), result.SlaDueAt);

        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT i.status,i.severity,i.reason_code,i.next_action,i.custody_acquired,
                   i.occurred_at,i.sla_due_at,i.owner_org_id,i.created_by,
                   (SELECT count(*) FROM incidents.incident_evidence e WHERE e.incident_id=i.id),
                   (SELECT e.proof_id FROM incidents.incident_evidence e WHERE e.incident_id=i.id)
            FROM incidents.incidents i
            WHERE i.id=@incident
            """);
        verify.Parameters.AddWithValue("incident", result.Id);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(IncidentContract.Open, reader.GetString(0));
        Assert.Equal(IncidentContract.Medium, reader.GetString(1));
        Assert.Equal(IncidentContract.RecipientAbsent, reader.GetString(2));
        Assert.Equal(IncidentContract.Rescheduled, reader.GetString(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal(now.AddMinutes(-5), reader.GetFieldValue<DateTimeOffset>(5));
        Assert.Equal(now.AddMinutes(-5).AddHours(24), reader.GetFieldValue<DateTimeOffset>(6));
        Assert.Equal(scenario.OrganizationId, reader.GetGuid(7));
        Assert.Equal(scenario.UserId, reader.GetGuid(8));
        Assert.Equal(1, reader.GetInt64(9));
        Assert.Equal(proofId, reader.GetGuid(10));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Opening_an_incident_writes_one_audit_entry_and_never_moves_the_order()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM platform.audit_logs
                WHERE entity_id=@incident AND action='incidents.incident.opened' AND org_id=@org),
              (SELECT status FROM orders.orders WHERE id=@order),
              (SELECT version FROM orders.orders WHERE id=@order),
              (SELECT count(*) FROM orders.order_events WHERE order_id=@order),
              (SELECT count(*) FROM platform.idempotency_keys
                WHERE owner_org_id=@org AND scope='INC-001:OPEN_INCIDENT' AND response_status=201);
            """);
        verify.Parameters.AddWithValue("incident", result.Id);
        verify.Parameters.AddWithValue("org", scenario.OrganizationId);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        // INC-001 is never the writer of order state; ORD-002 remains authoritative.
        Assert.Equal("DELIVERING", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt64(3));
        Assert.Equal(1, reader.GetInt64(4));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_missing_reason_is_rejected_and_persists_nothing()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [proofId]) with { ReasonCode = string.Empty },
                CancellationToken.None));

        Assert.Equal("INVALID_REQUEST", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_missing_next_action_is_rejected_and_persists_nothing()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [proofId]) with { NextAction = string.Empty },
                CancellationToken.None));

        Assert.Equal("INVALID_REQUEST", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_next_action_outside_the_state_machine_is_rejected()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [proofId]) with { NextAction = "DELIVERED" },
                CancellationToken.None));

        Assert.Equal("INVALID_REQUEST", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Missing_evidence_is_rejected_and_persists_nothing()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, []),
                CancellationToken.None));

        Assert.Equal("INVALID_REQUEST", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_that_is_not_a_proof_of_this_order_is_rejected()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [Guid.NewGuid()]),
                CancellationToken.None));

        Assert.Equal("EVIDENCE_NOT_AVAILABLE", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_belonging_to_another_tenant_is_rejected()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");
        var foreignProofId = await InsertProofAsync(foreign);

        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [foreignProofId]),
                CancellationToken.None));

        Assert.Equal("EVIDENCE_NOT_AVAILABLE", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_order_of_another_tenant_is_indistinguishable_from_an_absent_order()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");
        var foreignProofId = await InsertProofAsync(foreign);

        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var scope = CreateIncidentScope();

        // The actor authenticates in their own tenant but names the foreign order.
        await Assert.ThrowsAsync<IncidentNotFoundException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [foreignProofId]) with { OrderId = foreign.OrderId },
                CancellationToken.None));

        await AssertNoIncidentArtifactsAsync(foreign);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_order_state_that_cannot_reach_a_failed_attempt_is_rejected()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "PICKED_UP");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [proofId]),
                CancellationToken.None));

        Assert.Equal("ORDER_STATE_NOT_ALLOWED", conflict.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Opening_without_a_valid_idempotency_key_is_rejected()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        foreach (var key in new[] { string.Empty, "short", new string('k', 129) })
        {
            var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
                scope.Service.OpenAsync(
                    OpenCommand(scenario, [proofId]) with { IdempotencyKey = key },
                    CancellationToken.None));
            Assert.Equal("INVALID_REQUEST", conflict.Code);
        }

        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Replaying_the_same_opening_returns_the_same_incident_exactly_once()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var command = OpenCommand(scenario, [proofId]);

        await using var first = CreateIncidentScope();
        var opened = await first.Service.OpenAsync(command, CancellationToken.None);
        await using var second = CreateIncidentScope();
        var replayed = await second.Service.OpenAsync(command, CancellationToken.None);

        Assert.Equal(opened.Id, replayed.Id);
        Assert.Equal(opened.SlaDueAt, replayed.SlaDueAt);
        Assert.Equal(opened.OccurredAt, replayed.OccurredAt);
        Assert.Equal(opened.NextAction, replayed.NextAction);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Reusing_a_key_for_a_different_attempt_conflicts_without_opening_a_second_incident()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var command = OpenCommand(scenario, [proofId]);

        await using var first = CreateIncidentScope();
        await first.Service.OpenAsync(command, CancellationToken.None);

        await using var second = CreateIncidentScope();
        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            second.Service.OpenAsync(
                command with { ReasonCode = IncidentContract.AddressNotFound },
                CancellationToken.None));

        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_same_key_in_another_tenant_opens_an_independent_incident()
    {
        await using var first = new SyntheticOrderScenario(fixture);
        await first.InitializeAsync(orderStatus: "DELIVERING");
        var firstProof = await InsertProofAsync(first);

        await using var second = new SyntheticOrderScenario(fixture);
        await second.InitializeAsync(orderStatus: "DELIVERING");
        var secondProof = await InsertProofAsync(second);

        const string sharedKey = "inc001-shared-tenant-key-0001";
        await using var firstScope = CreateIncidentScope();
        var firstResult = await firstScope.Service.OpenAsync(
            OpenCommand(first, [firstProof]) with { IdempotencyKey = sharedKey },
            CancellationToken.None);

        await using var secondScope = CreateIncidentScope();
        var secondResult = await secondScope.Service.OpenAsync(
            OpenCommand(second, [secondProof]) with { IdempotencyKey = sharedKey },
            CancellationToken.None);

        Assert.NotEqual(firstResult.Id, secondResult.Id);
        await AssertIncidentCountsAsync(first, incidents: 1, evidence: 1, audits: 1);
        await AssertIncidentCountsAsync(second, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_incident_without_evidence_cannot_be_committed_at_all()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");

        // Bypassing the application entirely still cannot produce an incident without evidence:
        // the requirement is enforced by a deferred constraint at commit time.
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            scenario.ExecuteAdminAsync(
                """
                INSERT INTO incidents.incidents(
                  id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
                  reason_code,next_action,occurred_at,sla_due_at,created_by)
                VALUES (
                  gen_random_uuid(),@order,@org,'SYNTHETIC','LOW','OPEN',true,
                  'RECIPIENT_ABSENT','RESCHEDULED',clock_timestamp(),
                  clock_timestamp()+interval '72 hours',@user);
                """,
                SyntheticOrderScenario.P("order", scenario.OrderId),
                SyntheticOrderScenario.P("org", scenario.OrganizationId),
                SyntheticOrderScenario.P("user", scenario.UserId)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_sla_deadline_that_precedes_the_attempt_is_refused_by_the_database()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            scenario.ExecuteAdminAsync(
                """
                INSERT INTO incidents.incidents(
                  id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
                  reason_code,next_action,occurred_at,sla_due_at,created_by)
                VALUES (
                  gen_random_uuid(),@order,@org,'SYNTHETIC','LOW','OPEN',true,
                  'RECIPIENT_ABSENT','RESCHEDULED',clock_timestamp(),
                  clock_timestamp()-interval '1 hour',@user);
                """,
                SyntheticOrderScenario.P("order", scenario.OrderId),
                SyntheticOrderScenario.P("org", scenario.OrganizationId),
                SyntheticOrderScenario.P("user", scenario.UserId)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Incident_evidence_is_append_only_for_runtime_roles()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        await using var transaction = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]);
        await using var delete = new NpgsqlCommand(
            "DELETE FROM incidents.incident_evidence WHERE incident_id=@incident;",
            transaction.Connection,
            transaction.Transaction);
        delete.Parameters.AddWithValue("incident", result.Id);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_opened_incident_unlocks_the_ORD_002_failed_attempt_transition()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var incidents = CreateIncidentScope();
        var incident = await incidents.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        await using var orders = CreateTransitionScope();
        var transitioned = await orders.Service.TransitionAsync(
            TransitionCommand(
                scenario,
                OrderStatus.FailedAttempt,
                $$"""{"incident_id":"{{incident.Id:D}}"}"""),
            CancellationToken.None);

        Assert.Equal(OrderStatus.FailedAttempt.ToContractValue(), transitioned.Status);
        Assert.Equal(2, transitioned.Version);

        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT e.payload->>'incident_id',
                   e.payload->>'attempt_stage',
                   (e.payload->>'custody_acquired')::boolean,
                   e.public_event_code
            FROM orders.order_events e
            WHERE e.order_id=@order AND e.aggregate_version=2
            """);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(incident.Id.ToString("D"), reader.GetString(0));
        Assert.Equal("DELIVERING", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal("DELIVERY_ATTEMPTED", reader.GetString(3));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_failed_attempt_without_an_incident_is_still_refused()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var orders = CreateTransitionScope();

        var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            orders.Service.TransitionAsync(
                TransitionCommand(scenario, OrderStatus.FailedAttempt, metadata: null),
                CancellationToken.None));

        // Without an incident there is no recorded attempt stage, so ORD-002 refuses at the guard.
        Assert.Equal(OrderTransitionConflictCode.GuardNotSatisfied, conflict.Code);
        Assert.Equal("attempt_stage_recorded", conflict.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_failed_attempt_naming_another_tenants_incident_is_refused()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");
        var foreignProof = await InsertProofAsync(foreign);
        await using var foreignScope = CreateIncidentScope();
        var foreignIncident = await foreignScope.Service.OpenAsync(
            OpenCommand(foreign, [foreignProof]),
            CancellationToken.None);

        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var orders = CreateTransitionScope();

        var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            orders.Service.TransitionAsync(
                TransitionCommand(
                    scenario,
                    OrderStatus.FailedAttempt,
                    $$"""{"incident_id":"{{foreignIncident.Id:D}}"}"""),
                CancellationToken.None));

        Assert.Equal(OrderTransitionConflictCode.GuardNotSatisfied, conflict.Code);
        Assert.Equal("attempt_stage_recorded", conflict.GuardCode);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_failed_attempt_never_reaches_delivered_directly()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var incidents = CreateIncidentScope();
        var incident = await incidents.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        await using var orders = CreateTransitionScope();
        await orders.Service.TransitionAsync(
            TransitionCommand(
                scenario,
                OrderStatus.FailedAttempt,
                $$"""{"incident_id":"{{incident.Id:D}}"}"""),
            CancellationToken.None);

        await using var direct = CreateTransitionScope();
        var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            direct.Service.TransitionAsync(
                TransitionCommand(scenario, OrderStatus.Delivered, metadata: null, expectedVersion: 2),
                CancellationToken.None));

        Assert.Equal(OrderTransitionConflictCode.InvalidState, conflict.Code);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_unresolved_incident_keeps_the_order_open()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var incidents = CreateIncidentScope();
        await incidents.Service.OpenAsync(OpenCommand(scenario, [proofId]), CancellationToken.None);

        await scenario.ExecuteAdminAsync(
            """
            UPDATE orders.orders
            SET status='DELIVERED',version=2,claim_window_ends_at=clock_timestamp()+interval '72 hours'
            WHERE id=@order;
            """,
            SyntheticOrderScenario.P("order", scenario.OrderId));

        await using var orders = CreateTransitionScope();
        var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            orders.Service.TransitionAsync(
                TransitionCommand(scenario, OrderStatus.Closed, metadata: null, expectedVersion: 2),
                CancellationToken.None));

        Assert.Equal(OrderTransitionConflictCode.GuardNotSatisfied, conflict.Code);
        Assert.Equal("no_unresolved_incident", conflict.GuardCode);
    }


    // ----------------------------------------------------------------- AI-06 PII protection

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_accepted_description_is_persisted_only_as_ciphertext_under_a_key_version()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT i.description_ciphertext,i.pii_key_version,
                   (SELECT count(*) FROM platform.audit_logs a
                     WHERE a.entity_id=i.id AND a.payload_redacted::text LIKE '%'||@plaintext||'%'),
                   (SELECT count(*) FROM incidents.incidents x
                     WHERE x.id=i.id AND encode(x.description_ciphertext,'escape') LIKE '%'||@plaintext||'%')
            FROM incidents.incidents i
            WHERE i.id=@incident
            """);
        verify.Parameters.AddWithValue("incident", result.Id);
        verify.Parameters.AddWithValue("plaintext", TestDescription);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var ciphertext = reader.GetFieldValue<byte[]>(0);
        Assert.NotEmpty(ciphertext);
        Assert.Equal(TestKeyVersion, reader.GetString(1));
        // The stored bytes are not the description, and the description is nowhere to be found.
        Assert.NotEqual(Encoding.UTF8.GetBytes(TestDescription), ciphertext);
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_unavailable_description_protector_fails_closed_with_zero_effects()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope(piiProtector: new DisabledIncidentPiiProtector());
        var command = OpenCommand(scenario, [proofId]);

        await Assert.ThrowsAsync<IncidentInfrastructureException>(() =>
            scope.Service.OpenAsync(command, CancellationToken.None));

        // No incident, no evidence, no audit entry and not even an idempotency reservation.
        await AssertNoIncidentArtifactsAsync(scenario);
        await using var reservations = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@org;");
        reservations.Parameters.AddWithValue("org", scenario.OrganizationId);
        Assert.Equal(0L, await reservations.ExecuteScalarAsync());
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_replayed_opening_keeps_the_stored_ciphertext_and_key_version_stable()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var command = OpenCommand(scenario, [proofId]);

        var first = await scope.Service.OpenAsync(command, CancellationToken.None);
        var replay = await scope.Service.OpenAsync(command, CancellationToken.None);

        Assert.Equal(first.Id, replay.Id);
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT count(*),count(DISTINCT description_ciphertext),count(DISTINCT pii_key_version)
            FROM incidents.incidents WHERE order_id=@order
            """);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    // ------------------------------------------------- Database-enforced evidence coherence

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Further_evidence_naming_a_proof_of_the_same_order_and_tenant_is_accepted()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var secondProofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        await InsertEvidenceAsync(scenario, result.Id, scenario.OrderId, secondProofId);

        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 2, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_naming_a_proof_of_another_order_of_the_same_tenant_is_refused_by_the_database()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        var siblingOrderId = await InsertSiblingOrderAsync(scenario);
        var siblingProofId = await InsertProofAsync(scenario, siblingOrderId);

        // The proof belongs to this very tenant, but to a different order.
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEvidenceAsync(scenario, result.Id, scenario.OrderId, siblingProofId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_naming_another_tenants_proof_is_refused_by_the_database()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await using var other = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await other.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var otherProofId = await InsertProofAsync(other);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEvidenceAsync(scenario, result.Id, scenario.OrderId, otherProofId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_whose_order_does_not_match_its_incident_is_refused_by_the_database()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        var siblingOrderId = await InsertSiblingOrderAsync(scenario);
        var siblingProofId = await InsertProofAsync(scenario, siblingOrderId);

        // Coherent with the proof, incoherent with the incident the evidence claims to support.
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEvidenceAsync(scenario, result.Id, siblingOrderId, siblingProofId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_naming_another_tenant_as_owner_is_refused_by_the_database()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await using var other = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await other.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        // A proof this incident does not already cite, so the owner rule is what decides the
        // rejection rather than the UNIQUE (incident_id, proof_id) of an existing pair.
        var secondProofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEvidenceAsync(
                scenario, result.Id, scenario.OrderId, secondProofId, ownerOrgId: other.OrganizationId));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Evidence_with_an_incoherent_operator_organization_is_refused_by_the_database()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await using var other = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await other.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var secondProofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();
        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId]),
            CancellationToken.None);

        // The order, the incident and the proof all carry no operator; claiming one is incoherent.
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEvidenceAsync(
                scenario,
                result.Id,
                scenario.OrderId,
                secondProofId,
                operatorOrgId: other.OrganizationId));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    // ----------------------------------------------------------- RETURNING requires custody

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Rescheduling_an_attempt_that_failed_at_pickup_is_accepted()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "AT_PICKUP");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId], nextAction: IncidentContract.Rescheduled),
            CancellationToken.None);

        Assert.Equal(IncidentContract.Rescheduled, result.NextAction);
        Assert.False(result.CustodyAcquired);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Returning_an_attempt_that_failed_at_pickup_is_refused_before_persistence()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "AT_PICKUP");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        // Custody was never acquired, so ORD-002 and ADR-014 cannot return the parcel.
        var failure = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.OpenAsync(
                OpenCommand(scenario, [proofId], nextAction: IncidentContract.Returning),
                CancellationToken.None));

        Assert.Equal("ORDER_STATE_NOT_ALLOWED", failure.Code);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Returning_an_attempt_that_failed_after_custody_was_acquired_is_accepted()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "IN_TRANSIT");
        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope();

        var result = await scope.Service.OpenAsync(
            OpenCommand(scenario, [proofId], nextAction: IncidentContract.Returning),
            CancellationToken.None);

        Assert.Equal(IncidentContract.Returning, result.NextAction);
        Assert.True(result.CustodyAcquired);
        await AssertIncidentCountsAsync(scenario, incidents: 1, evidence: 1, audits: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_database_refuses_returning_without_custody_even_from_direct_sql()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "AT_PICKUP");

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            scenario.ExecuteAdminAsync(
                """
                INSERT INTO incidents.incidents(
                  id,order_id,owner_org_id,incident_type,severity,status,custody_acquired,
                  reason_code,next_action,occurred_at,sla_due_at,created_by)
                VALUES (
                  gen_random_uuid(),@order,@org,'SYNTHETIC','LOW','OPEN',false,
                  'RECIPIENT_ABSENT','RETURNING',clock_timestamp(),
                  clock_timestamp()+interval '72 hours',@user);
                """,
                SyntheticOrderScenario.P("order", scenario.OrderId),
                SyntheticOrderScenario.P("org", scenario.OrganizationId),
                SyntheticOrderScenario.P("user", scenario.UserId)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        await AssertNoIncidentArtifactsAsync(scenario);
    }

    private static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;

    internal const string TestKeyVersion = "inc001-contract-v1";

    /// <summary>The description every opening in this suite carries, in plaintext.</summary>
    internal const string TestDescription = "El destinatario no se encontraba en el domicilio.";

    private static OpenIncidentCommand OpenCommand(
        SyntheticOrderScenario scenario,
        IReadOnlyList<Guid> evidence,
        DateTimeOffset? occurredAt = null,
        string? nextAction = null) =>
        new(
            scenario.UserId,
            scenario.OrganizationId,
            MfaSatisfied: true,
            $"inc001-pg-{Guid.NewGuid():N}",
            scenario.OrderId,
            "FAILED_DELIVERY_ATTEMPT",
            IncidentContract.Medium,
            TestDescription,
            IncidentContract.RecipientAbsent,
            nextAction ?? IncidentContract.Rescheduled,
            occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            evidence,
            "synthetic-request-id");

    private static TransitionOrderCommand TransitionCommand(
        SyntheticOrderScenario scenario,
        OrderStatus target,
        string? metadata,
        int expectedVersion = 1) =>
        new(
            scenario.UserId,
            scenario.OrganizationId,
            $"inc001-transition-{Guid.NewGuid():N}",
            scenario.OrderId,
            target.ToContractValue(),
            "intento fallido sintetico",
            expectedVersion,
            metadata,
            true,
            "synthetic-request-id");

    /// <summary>
    /// A second order of the same tenant, reusing the scenario's city and locations, so a test
    /// can name a proof that is unquestionably this tenant's yet belongs to another order.
    /// </summary>
    private static async Task<Guid> InsertSiblingOrderAsync(SyntheticOrderScenario scenario)
    {
        var quoteId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await scenario.ExecuteAdminAsync(
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
              'DELIVERING',q.subtotal_cents,q.discount_cents,q.tax_cents,q.total_cents,
              q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,q.package_snapshot,0,1
            FROM pricing.quotes q WHERE q.id=@quote;
            """,
            SyntheticOrderScenario.P("quote", quoteId),
            SyntheticOrderScenario.P("source_quote", scenario.QuoteId),
            SyntheticOrderScenario.P("order", orderId),
            SyntheticOrderScenario.P("public_id", $"ARC002-{orderId:N}"),
            SyntheticOrderScenario.P("input_hash", new byte[32]));
        return orderId;
    }

    /// <summary>
    /// Writes one evidence row with the exact tuple a test wants the database to reject. It runs
    /// as the cluster administrator on purpose: application validation is not what is under test
    /// here, the constraints are.
    /// </summary>
    private static Task InsertEvidenceAsync(
        SyntheticOrderScenario scenario,
        Guid incidentId,
        Guid orderId,
        Guid proofId,
        Guid? ownerOrgId = null,
        Guid? operatorOrgId = null) =>
        scenario.ExecuteAdminAsync(
            """
            INSERT INTO incidents.incident_evidence(
              id,incident_id,order_id,owner_org_id,operator_org_id,proof_id,created_by)
            VALUES (gen_random_uuid(),@incident,@order,@owner,@operator,@proof,@user);
            """,
            SyntheticOrderScenario.P("incident", incidentId),
            SyntheticOrderScenario.P("order", orderId),
            SyntheticOrderScenario.P("owner", ownerOrgId ?? scenario.OrganizationId),
            new NpgsqlParameter("operator", NpgsqlTypes.NpgsqlDbType.Uuid)
            {
                Value = (object?)operatorOrgId ?? DBNull.Value,
            },
            SyntheticOrderScenario.P("proof", proofId),
            SyntheticOrderScenario.P("user", scenario.UserId));

    private static async Task<Guid> InsertProofAsync(
        SyntheticOrderScenario scenario,
        Guid? orderId = null)
    {
        var uploadId = Guid.NewGuid();
        var proofId = Guid.NewGuid();
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
              @proof,@order,@org,@upload,'DELIVERY_PHOTO',@object_key,
              decode(repeat('04',32),'hex'),'image/jpeg',100,clock_timestamp(),@user);
            """,
            SyntheticOrderScenario.P("upload", uploadId),
            SyntheticOrderScenario.P("proof", proofId),
            SyntheticOrderScenario.P("order", orderId ?? scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("quarantine", $"quarantine/{uploadId:N}"),
            SyntheticOrderScenario.P("object_key", $"proofs/{uploadId:N}"));
        return proofId;
    }

    private async Task AssertNoIncidentArtifactsAsync(SyntheticOrderScenario scenario) =>
        await AssertIncidentCountsAsync(scenario, incidents: 0, evidence: 0, audits: 0);

    private async Task AssertIncidentCountsAsync(
        SyntheticOrderScenario scenario,
        long incidents,
        long evidence,
        long audits)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*) FROM incidents.incidents WHERE order_id=@order),
              (SELECT count(*) FROM incidents.incident_evidence WHERE order_id=@order),
              (SELECT count(*) FROM platform.audit_logs
                WHERE org_id=@org AND action='incidents.incident.opened');
            """);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        verify.Parameters.AddWithValue("org", scenario.OrganizationId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(incidents, reader.GetInt64(0));
        Assert.Equal(evidence, reader.GetInt64(1));
        Assert.Equal(audits, reader.GetInt64(2));
    }

    private IncidentScope CreateIncidentScope(
        DateTimeOffset? now = null,
        IIncidentPiiProtector? piiProtector = null)
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
            piiProtector ?? new DeterministicMockIncidentPiiProtector(),
            Options.Create(new IncidentsOptions
            {
                PiiProtector = IncidentPiiProtectorKind.Mock,
                PiiKeyVersion = TestKeyVersion,
            }),
            now is { } utcNow ? new FixedClock(utcNow) : new SystemClock());
        return new IncidentScope(context, service);
    }

    private OrderScope CreateTransitionScope()
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
            new PostgreSqlOrderAssignmentGuardReader(),
            new PostgreSqlOrderProofGuardReader(),
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
        return new OrderScope(context, service);
    }

    private sealed class IncidentScope(
        IncidentsDbContext context,
        IIncidentService service) : IAsyncDisposable
    {
        internal IIncidentService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class OrderScope(
        OrdersDbContext context,
        IOrderTransitionService service) : IAsyncDisposable
    {
        internal IOrderTransitionService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
