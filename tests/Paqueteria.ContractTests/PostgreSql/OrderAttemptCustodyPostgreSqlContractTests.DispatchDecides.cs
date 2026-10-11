using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure.Incidents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 against a real PostgreSQL baseline, through the real services: the
/// assigned driver reports a failed attempt with a reason and evidence only, the order reaches FAILED_ATTEMPT with the
/// decision pending, the driver cannot take it, and dispatch chooses each successor under every other guard; a report
/// made by dispatch keeps today's rule.
/// </summary>
public sealed partial class OrderAttemptCustodyPostgreSqlContractTests
{
    private static readonly OrderStatus[] FailedAttemptExits =
        [OrderStatus.Delivering, OrderStatus.Rescheduled, OrderStatus.Returning];

    // ------------------------------------------- 6. a driver report leaves the decision to dispatch

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_driver_report_records_a_graded_protected_report_and_leaves_the_decision_to_dispatch()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var photo = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var occurredAt = UtcMicrosecondPrecision.Normalize(DateTimeOffset.UtcNow.AddMinutes(-2));

        var incident = await OpenDriverReportAsync(world, [photo], IncidentContract.SecurityRisk, occurredAt);

        Assert.Null(incident.NextAction);
        Assert.Equal(IncidentContract.High, incident.Severity);
        Assert.Equal(IncidentContract.SecurityRisk, incident.ReasonCode);
        Assert.True(incident.CustodyAcquired);
        // "Según el motivo": a security risk is HIGH, attended within eight hours of the attempt.
        Assert.Equal(occurredAt.AddHours(8), incident.SlaDueAt);
        Assert.Equal(new[] { photo }, incident.EvidenceProofIds);

        const string expectedDescription = "Intento fallido reportado por el repartidor. Motivo: Riesgo de seguridad.";
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT i.next_action IS NULL,i.severity,i.sla_due_at,i.description_ciphertext,i.pii_key_version,
                   i.created_by,
                   (SELECT count(*) FROM incidents.incident_evidence e WHERE e.incident_id=i.id AND e.proof_id=@proof),
                   (SELECT a.payload_redacted::text FROM platform.audit_logs a
                     WHERE a.entity_id=i.id AND a.action='incidents.incident.opened')
            FROM incidents.incidents i
            WHERE i.id=@incident
            """);
        verify.Parameters.AddWithValue("incident", incident.Id);
        verify.Parameters.AddWithValue("proof", photo);
        await using (var reader = await verify.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.Equal(IncidentContract.High, reader.GetString(1));
            Assert.Equal(occurredAt.AddHours(8), reader.GetFieldValue<DateTimeOffset>(2));
            // The fixed description built from the reason, protected exactly like a dispatcher's description.
            var protector = new DeterministicMockIncidentPiiProtector(IncidentsPostgreSqlContractTests.TestKeyVersion);
            Assert.Equal(
                protector.Protect(expectedDescription, IncidentsPostgreSqlContractTests.TestKeyVersion),
                reader.GetFieldValue<byte[]>(3));
            Assert.Equal(IncidentsPostgreSqlContractTests.TestKeyVersion, reader.GetString(4));
            Assert.Equal(world.DriverUserId, reader.GetGuid(5));
            Assert.Equal(1L, reader.GetInt64(6));
            var audit = reader.GetString(7);
            Assert.Contains("\"next_action\": null", audit, StringComparison.Ordinal);
            Assert.DoesNotContain("Riesgo de seguridad", audit, StringComparison.Ordinal);
            Assert.DoesNotContain("description", audit, StringComparison.Ordinal);
        }

        // The driver reaches FAILED_ATTEMPT with the incident, exactly as before.
        var failed = await DriverTransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        Assert.Equal("FAILED_ATTEMPT", failed.Status);

        // ...and can no longer take the next step: dispatch decides, whatever the target.
        foreach (var target in FailedAttemptExits)
        {
            var refused = await DriverRefusedAsync(world, target);
            Assert.Equal("failed_attempt_dispatch_decides", refused.GuardCode);
            Assert.Equal(OrderTransitionRejectionCodes.DispatchDecisionRequired, refused.RejectionCode);
        }

        Assert.Equal("FAILED_ATTEMPT", await ScalarAsync<string>(
            "SELECT status FROM orders.orders WHERE id=@order",
            SyntheticOrderScenario.P("order", world.Scenario.OrderId)));
        Assert.Equal(FailedAttemptExits, await AllowedTargetsAsync(world, world.Scenario.UserId));
        Assert.Empty(await AllowedTargetsAsync(world, world.DriverUserId));
    }

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("RESCHEDULED")]
    [InlineData("DELIVERING")]
    [InlineData("RETURNING")]
    public async Task Dispatch_chooses_each_successor_of_a_driver_report(string successor)
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var photo = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenDriverReportAsync(world, [photo], IncidentContract.RecipientAbsent);
        await DriverTransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));
        Assert.True(OrderContractValues.TryParseOrderStatus(successor, out var target));

        // "Reprogramar", "Reintentar hoy" or "Regresar": the dispatcher of the scenario decides.
        var chosen = await TransitionAsync(world, target);

        Assert.Equal(successor, chosen.Status);
        if (target == OrderStatus.Delivering)
        {
            // The retry is a new attempt: the photo that became incident evidence never completes it.
            var delivered = await RefusedAsync(world, OrderStatus.Delivered);
            Assert.Equal("delivery_proof_complete", delivered.GuardCode);
        }
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_failed_pickup_reported_by_the_driver_can_only_be_rescheduled()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.CanonicalPathTo("AT_PICKUP"));
        var photo = await InsertProofAsync(world, "PICKUP_PHOTO");
        var incident = await OpenDriverReportAsync(world, [photo], IncidentContract.AddressNotFound);
        Assert.Null(incident.NextAction);
        Assert.False(incident.CustodyAcquired);
        Assert.Equal(IncidentContract.Medium, incident.Severity);
        await DriverTransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        // Nothing was collected: dispatch is offered and allowed only the reschedule.
        Assert.Equal([OrderStatus.Rescheduled], await AllowedTargetsAsync(world, world.Scenario.UserId));
        Assert.Equal("custody_acquired_true", (await RefusedAsync(world, OrderStatus.Returning)).GuardCode);
        Assert.Equal("retry_custody_acquired_true", (await RefusedAsync(world, OrderStatus.Delivering)).GuardCode);
        Assert.Equal("RESCHEDULED", (await TransitionAsync(world, OrderStatus.Rescheduled)).Status);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Who_reports_decides_the_shape_of_the_report()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var photo = await InsertProofAsync(world, "DELIVERY_PHOTO");

        // A driver never sends a severity, a description or a next action...
        var driverWithDispatchShape = DriverReportCommand(world, [photo], IncidentContract.RecipientAbsent) with
        {
            Severity = IncidentContract.Medium,
            Description = IncidentsPostgreSqlContractTests.TestDescription,
            NextAction = IncidentContract.Rescheduled,
        };
        // ...and dispatch always does: "Cuando el reporte lo hace despacho, sigue como hoy".
        var dispatchWithDriverShape = DriverReportCommand(world, [photo], IncidentContract.RecipientAbsent) with
        {
            ActorId = world.Scenario.UserId,
            MfaSatisfied = true,
        };

        foreach (var command in new[] { driverWithDispatchShape, dispatchWithDriverShape })
        {
            await using var scope = CreateIncidentScope();
            var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
                scope.Service.OpenAsync(command, CancellationToken.None));
            Assert.Equal("INVALID_REQUEST", conflict.Code);
        }

        Assert.Equal(0L, await ScalarAsync<long>(
            """
            SELECT (SELECT count(*) FROM incidents.incidents WHERE order_id=@order)
                 + (SELECT count(*) FROM platform.idempotency_keys
                     WHERE owner_org_id=@org AND scope='INC-001:OPEN_INCIDENT'
                       AND idempotency_key LIKE 'inc002-driver-%')
            """,
            SyntheticOrderScenario.P("order", world.Scenario.OrderId),
            SyntheticOrderScenario.P("org", world.Scenario.OrganizationId)));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_driver_report_replays_its_original_response()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var photo = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var command = DriverReportCommand(world, [photo], IncidentContract.PackageDamaged);

        IncidentResult first;
        await using (var scope = CreateIncidentScope())
        {
            first = await scope.Service.OpenAsync(command, CancellationToken.None);
        }

        await using (var scope = CreateIncidentScope())
        {
            var replay = await scope.Service.OpenAsync(command, CancellationToken.None);
            Assert.Equal(first, replay with { EvidenceProofIds = first.EvidenceProofIds });
            Assert.Equal(first.EvidenceProofIds, replay.EvidenceProofIds);
            Assert.Null(replay.NextAction);
            Assert.Equal(IncidentContract.High, replay.Severity);
        }

        Assert.Equal(1L, await ScalarAsync<long>(
            "SELECT count(*) FROM incidents.incidents WHERE order_id=@order",
            SyntheticOrderScenario.P("order", world.Scenario.OrderId)));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_dispatch_report_keeps_binding_the_successor_drivers_included()
    {
        await using var world = await AttemptWorld.CreateAsync(fixture, OrderStatusHistory.ToDelivering);
        var photo = await InsertProofAsync(world, "DELIVERY_PHOTO");
        var incident = await OpenIncidentAsync(world, [photo], IncidentContract.Rescheduled);
        Assert.Equal(IncidentContract.Rescheduled, incident.NextAction);
        await TransitionAsync(world, OrderStatus.FailedAttempt, IncidentMetadata(incident));

        Assert.Equal(
            [OrderStatus.Delivering, OrderStatus.Rescheduled],
            await AllowedTargetsAsync(world, world.Scenario.UserId));
        Assert.Equal("failed_attempt_next_action_respected", (await DriverRefusedAsync(world, OrderStatus.Returning)).GuardCode);
        Assert.Equal("RESCHEDULED", (await DriverTransitionAsync(world, OrderStatus.Rescheduled)).Status);
    }

    // ------------------------------------------------------------------- helpers

    private async Task<IncidentResult> OpenDriverReportAsync(
        AttemptWorld world,
        IReadOnlyList<Guid> evidence,
        string reasonCode,
        DateTimeOffset? occurredAt = null)
    {
        await using var scope = CreateIncidentScope();
        return await scope.Service.OpenAsync(
            DriverReportCommand(world, evidence, reasonCode, occurredAt),
            CancellationToken.None);
    }

    /// <summary>What the driver PWA sends: type, reason, occurrence and evidence; nothing else.</summary>
    private static OpenIncidentCommand DriverReportCommand(
        AttemptWorld world,
        IReadOnlyList<Guid> evidence,
        string reasonCode,
        DateTimeOffset? occurredAt = null) =>
        new(
            world.DriverUserId,
            world.Scenario.OrganizationId,
            MfaSatisfied: false,
            $"inc002-driver-{Guid.NewGuid():N}",
            world.Scenario.OrderId,
            "FAILED_DELIVERY_ATTEMPT",
            Severity: null,
            Description: null,
            reasonCode,
            NextAction: null,
            occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            evidence,
            "synthetic-request-id");

    private async Task<OrderResult> DriverTransitionAsync(
        AttemptWorld world,
        OrderStatus target,
        string? metadata = null)
    {
        await using var scope = CreateTransitionScope();
        var result = await scope.Service.TransitionAsync(
            DriverTransitionCommand(world, target, metadata),
            CancellationToken.None);
        world.Version = result.Version;
        return result;
    }

    private async Task<OrderTransitionConflictException> DriverRefusedAsync(AttemptWorld world, OrderStatus target)
    {
        await using var scope = CreateTransitionScope();
        var conflict = await Assert.ThrowsAsync<OrderTransitionConflictException>(() =>
            scope.Service.TransitionAsync(
                DriverTransitionCommand(world, target, null),
                CancellationToken.None));
        Assert.Equal(OrderTransitionConflictCode.GuardNotSatisfied, conflict.Code);
        return conflict;
    }

    private static TransitionOrderCommand DriverTransitionCommand(
        AttemptWorld world,
        OrderStatus target,
        string? metadata) =>
        new(
            world.DriverUserId,
            world.Scenario.OrganizationId,
            $"ord002-driver-{Guid.NewGuid():N}",
            world.Scenario.OrderId,
            target.ToContractValue(),
            "synthetic driver reason",
            world.Version,
            metadata,
            false,
            "synthetic-request-id");

    /// <summary>getOrder's allowed_transitions for <paramref name="actorId"/>, as the order detail reads them.</summary>
    private async Task<OrderStatus[]> AllowedTargetsAsync(AttemptWorld world, Guid actorId)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        await using var context = new OrdersDbContext(options, state);
        var service = new QuoteSnapshotToOrderCoordinator(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new CryptographicOrderPublicIdGenerator(),
            new NoOpOrderCreationFailureInjector(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            Options.Create(new OrdersOptions
            {
                Provider = OrdersProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
                IdempotencyLifetimeMinutes = 60,
                PublicIdCollisionRetryCount = 2,
            }),
            new SystemClock());
        var detail = await service.GetAsync(
            actorId,
            world.Scenario.OrganizationId,
            world.Scenario.OrderId,
            false,
            CancellationToken.None);
        Assert.Equal(world.Version, detail.Order.Version);
        return [.. detail.AllowedTransitions.Select(item => item.Target)];
    }
}
