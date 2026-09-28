using System.Data.Common;
using System.Text.Json;
using Incidents.Application.Incidents;
using Incidents.Domain;
using Incidents.Infrastructure;
using Incidents.Infrastructure.Incidents;
using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Infrastructure.Orders;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// INC-001 incident resolution against a real PostgreSQL baseline and the runtime application
/// role: the pending-to-terminal state machine, resolved_at, supervisory authorization, FORCE RLS,
/// idempotent replay, the one-winner race, atomic rollback, and the guarantee that closing an
/// incident never touches its order or its evidence.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
public sealed class IncidentsResolutionPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private static readonly DateTimeOffset OpenedAt = DateTimeOffset.Parse(
        "2026-09-25T15:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset ResolvedAt = OpenedAt.AddMinutes(42).AddTicks(1234567);

    private const string ResolutionReason = "El cliente confirmó una nueva ventana de entrega.";

    private const string Description = IncidentsPostgreSqlContractTests.TestDescription;

    // ------------------------------------------------------------------ transition matrix

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("OPEN", "RESOLVED")]
    [InlineData("OPEN", "REJECTED")]
    [InlineData("INVESTIGATING", "RESOLVED")]
    [InlineData("INVESTIGATING", "REJECTED")]
    public async Task A_pending_incident_closes_into_the_requested_terminal_outcome(
        string pendingStatus,
        string outcome)
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        await SetIncidentStatusAsync(scenario, incident.Id, pendingStatus);
        await using var scope = CreateIncidentScope(ResolvedAt);

        var result = await scope.Service.ResolveAsync(
            ResolveCommand(scenario, incident.Id, outcome),
            CancellationToken.None);

        Assert.Equal(incident.Id, result.Id);
        Assert.Equal(outcome, result.Status);
        // The response is the existing Incident representation with only the status moved.
        Assert.Equal(incident.OrderId, result.OrderId);
        Assert.Equal(incident.Severity, result.Severity);
        Assert.Equal(incident.ReasonCode, result.ReasonCode);
        Assert.Equal(incident.NextAction, result.NextAction);
        Assert.Equal(incident.CustodyAcquired, result.CustodyAcquired);
        Assert.Equal(incident.OccurredAt, result.OccurredAt);
        Assert.Equal(incident.SlaDueAt, result.SlaDueAt);
        Assert.Equal(incident.EvidenceProofIds, result.EvidenceProofIds);

        var state = await ReadIncidentStateAsync(incident.Id);
        Assert.Equal(outcome, state.Status);
        // resolved_at is the normalized server clock at microsecond precision.
        Assert.Equal(UtcMicrosecondPrecision.Normalize(ResolvedAt), state.ResolvedAt);

        var audits = await ReadResolutionAuditsAsync(scenario, incident.Id);
        var audit = Assert.Single(audits);
        Assert.Equal(
            outcome == IncidentContract.Resolved
                ? "incidents.incident.resolved"
                : "incidents.incident.rejected",
            audit.Action);
        Assert.Equal(scenario.UserId, audit.ActorId);
        // The audit row itself carries the incident as its entity and resolved_at as its instant.
        Assert.Equal(UtcMicrosecondPrecision.Normalize(ResolvedAt), audit.OccurredAt);
        AssertRedactedPayload(
            new
            {
                incident_id = incident.Id,
                order_id = incident.OrderId,
                previous_status = pendingStatus,
                outcome,
                resolution_reason = ResolutionReason,
                resolved_at = UtcMicrosecondPrecision.Normalize(ResolvedAt),
            },
            audit.Payload);
        using (var payload = JsonDocument.Parse(audit.Payload))
        {
            var root = payload.RootElement;
            Assert.Equal(pendingStatus, root.GetProperty("previous_status").GetString());
            Assert.Equal(outcome, root.GetProperty("outcome").GetString());
            Assert.Equal(ResolutionReason, root.GetProperty("resolution_reason").GetString());
            // The protected incident description never reaches the resolution evidence.
            Assert.False(root.TryGetProperty("description", out _));
        }

        Assert.DoesNotContain(Description, audit.Payload, StringComparison.Ordinal);

        Assert.Equal(1, await CountResolutionReservationsAsync(scenario, completedWith: 200));
    }

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("RESOLVED", "RESOLVED")]
    [InlineData("RESOLVED", "REJECTED")]
    [InlineData("REJECTED", "RESOLVED")]
    [InlineData("REJECTED", "REJECTED")]
    public async Task A_terminal_incident_never_moves_again(string firstOutcome, string secondOutcome)
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        await using (var first = CreateIncidentScope(ResolvedAt))
        {
            await first.Service.ResolveAsync(
                ResolveCommand(scenario, incident.Id, firstOutcome),
                CancellationToken.None);
        }

        await using var second = CreateIncidentScope(ResolvedAt.AddHours(1));
        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            second.Service.ResolveAsync(
                ResolveCommand(scenario, incident.Id, secondOutcome, reason: "Segundo cierre."),
                CancellationToken.None));

        Assert.Equal("INCIDENT_STATE_CONFLICT", conflict.Code);
        var state = await ReadIncidentStateAsync(incident.Id);
        Assert.Equal(firstOutcome, state.Status);
        // resolved_at is written exactly once.
        Assert.Equal(UtcMicrosecondPrecision.Normalize(ResolvedAt), state.ResolvedAt);
        Assert.Single(await ReadResolutionAuditsAsync(scenario, incident.Id));
        Assert.Equal(1, await CountResolutionReservationsAsync(scenario));
    }

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("OPEN")]
    [InlineData("INVESTIGATING")]
    [InlineData("resolved")]
    [InlineData("")]
    public async Task An_outcome_outside_the_terminal_vocabulary_is_rejected_before_any_effect(string outcome)
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        await using var scope = CreateIncidentScope(ResolvedAt);

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.ResolveAsync(
                ResolveCommand(scenario, incident.Id, outcome),
                CancellationToken.None));

        Assert.Equal("INVALID_REQUEST", conflict.Code);
        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, incident.Id);
    }

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData("line\nbreak")]
    public async Task A_missing_or_padded_reason_is_rejected_before_any_effect(string reason)
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        await using var scope = CreateIncidentScope(ResolvedAt);

        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            scope.Service.ResolveAsync(
                ResolveCommand(scenario, incident.Id, IncidentContract.Resolved, reason: reason),
                CancellationToken.None));

        Assert.Equal("INVALID_REQUEST", conflict.Code);
        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, incident.Id);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_reason_over_the_published_bound_or_a_malformed_key_is_rejected()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        await using var scope = CreateIncidentScope(ResolvedAt);
        var command = ResolveCommand(scenario, incident.Id, IncidentContract.Resolved);

        foreach (var invalid in new[]
                 {
                     command with { Reason = new string('a', IncidentRequestPolicy.MaximumResolutionReasonLength + 1) },
                     command with { IdempotencyKey = "short" },
                     command with { IdempotencyKey = new string('k', 129) },
                 })
        {
            var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
                scope.Service.ResolveAsync(invalid, CancellationToken.None));
            Assert.Equal("INVALID_REQUEST", conflict.Code);
        }

        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, incident.Id);
    }

    // --------------------------------------------------------------------- authorization

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("DISPATCHER", "ACTIVE", "ACTIVE", false, true)]
    [InlineData("PLATFORM_ADMIN", "ACTIVE", "ACTIVE", true, true)]
    [InlineData("PLATFORM_ADMIN", "ACTIVE", "ACTIVE", false, false)]
    [InlineData("DRIVER", "ACTIVE", "ACTIVE", true, false)]
    [InlineData("FINANCE", "ACTIVE", "ACTIVE", true, false)]
    [InlineData("VIEWER", "ACTIVE", "ACTIVE", true, false)]
    [InlineData("DISPATCHER", "SUSPENDED", "ACTIVE", true, false)]
    [InlineData("DISPATCHER", "REVOKED", "ACTIVE", true, false)]
    [InlineData("DISPATCHER", "ACTIVE", "SUSPENDED", true, false)]
    [InlineData("DISPATCHER", "ACTIVE", "DISABLED", true, false)]
    public async Task Only_an_active_dispatcher_or_an_MFA_satisfied_platform_admin_may_close(
        string role,
        string membershipStatus,
        string userStatus,
        bool mfaSatisfied,
        bool allowed)
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        await scenario.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships SET role=@role,status=@membership
            WHERE user_id=@user AND organization_id=@org;
            UPDATE identity.users SET status=@user_status WHERE id=@user;
            """,
            SyntheticOrderScenario.P("role", role),
            SyntheticOrderScenario.P("membership", membershipStatus),
            SyntheticOrderScenario.P("user_status", userStatus),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId));
        await using var scope = CreateIncidentScope(ResolvedAt);
        var command = ResolveCommand(scenario, incident.Id, IncidentContract.Resolved) with
        {
            MfaSatisfied = mfaSatisfied,
        };

        if (allowed)
        {
            var result = await scope.Service.ResolveAsync(command, CancellationToken.None);
            Assert.Equal(IncidentContract.Resolved, result.Status);
            return;
        }

        await Assert.ThrowsAsync<IncidentForbiddenException>(() =>
            scope.Service.ResolveAsync(command, CancellationToken.None));
        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, incident.Id);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task The_driver_who_opened_the_incident_can_never_close_it()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await MakeScenarioUserTheAssignedDriverAsync(scenario);
        var proofId = await InsertProofAsync(scenario);

        // The active assignment is exactly what lets this driver open the incident...
        IncidentResult incident;
        await using (var opening = CreateIncidentScope(OpenedAt))
        {
            incident = await opening.Service.OpenAsync(
                OpenCommand(scenario, proofId) with { MfaSatisfied = false },
                CancellationToken.None);
        }

        // ...and it never lets them resolve or reject it, with or without MFA.
        foreach (var outcome in new[] { IncidentContract.Resolved, IncidentContract.Rejected })
        {
            foreach (var mfa in new[] { false, true })
            {
                await using var scope = CreateIncidentScope(ResolvedAt);
                await Assert.ThrowsAsync<IncidentForbiddenException>(() =>
                    scope.Service.ResolveAsync(
                        ResolveCommand(scenario, incident.Id, outcome) with { MfaSatisfied = mfa },
                        CancellationToken.None));
            }
        }

        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, incident.Id);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_forbidden_actor_learns_nothing_about_replay_evidence()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        var command = ResolveCommand(scenario, incident.Id, IncidentContract.Resolved);
        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            await scope.Service.ResolveAsync(command, CancellationToken.None);
        }

        await scenario.ExecuteAdminAsync(
            "UPDATE organizations.organization_memberships SET role='DRIVER' WHERE user_id=@user;",
            SyntheticOrderScenario.P("user", scenario.UserId));

        // Capability is settled before the stored replay: the exact replay, a mismatching payload
        // and a random incident all answer the same 403.
        await using var denied = CreateIncidentScope(ResolvedAt);
        foreach (var attempt in new[]
                 {
                     command,
                     command with { Reason = "Otro motivo." },
                     command with { IncidentId = Guid.NewGuid() },
                 })
        {
            await Assert.ThrowsAsync<IncidentForbiddenException>(() =>
                denied.Service.ResolveAsync(attempt, CancellationToken.None));
        }
    }

    // ------------------------------------------------------------------- tenant isolation

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_foreign_incident_is_indistinguishable_from_an_absent_one()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        var foreignIncident = await OpenIncidentAsync(foreign);
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        await using var scope = CreateIncidentScope(ResolvedAt);

        // The actor is an active dispatcher of their own tenant and names the foreign incident.
        await Assert.ThrowsAsync<IncidentNotFoundException>(() =>
            scope.Service.ResolveAsync(
                ResolveCommand(scenario, foreignIncident.Id, IncidentContract.Resolved),
                CancellationToken.None));
        await Assert.ThrowsAsync<IncidentNotFoundException>(() =>
            scope.Service.ResolveAsync(
                ResolveCommand(scenario, Guid.NewGuid(), IncidentContract.Resolved),
                CancellationToken.None));

        await AssertStillOpenWithoutResolutionEffectsAsync(foreign, foreignIncident.Id);
        Assert.Equal(0, await CountResolutionReservationsAsync(scenario));
        Assert.Empty(await ReadResolutionAuditsAsync(scenario, foreignIncident.Id));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Force_rls_hides_and_protects_a_foreign_incident_from_the_runtime_role()
    {
        await using var foreign = new SyntheticOrderScenario(fixture);
        var foreignIncident = await OpenIncidentAsync(foreign);
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");

        await using var transaction = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]);
        await using (var read = new NpgsqlCommand(
            "SELECT count(*) FROM incidents.incidents WHERE id=@incident;",
            transaction.Connection,
            transaction.Transaction))
        {
            read.Parameters.AddWithValue("incident", foreignIncident.Id);
            Assert.Equal(0L, await read.ExecuteScalarAsync());
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE incidents.incidents SET status='RESOLVED',resolved_at=clock_timestamp() WHERE id=@incident;",
            transaction.Connection,
            transaction.Transaction))
        {
            update.Parameters.AddWithValue("incident", foreignIncident.Id);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
        await AssertStillOpenWithoutResolutionEffectsAsync(foreign, foreignIncident.Id);
    }

    // -------------------------------------------------------------------------- idempotency

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task An_exact_replay_returns_the_original_response_without_a_second_effect()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        var command = ResolveCommand(scenario, incident.Id, IncidentContract.Rejected);

        IncidentResult first;
        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            first = await scope.Service.ResolveAsync(command, CancellationToken.None);
        }

        // A later clock proves the replay neither updates the row nor rewrites resolved_at.
        await using var later = CreateIncidentScope(ResolvedAt.AddHours(3));
        var replayed = await later.Service.ResolveAsync(command, CancellationToken.None);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replayed));
        var state = await ReadIncidentStateAsync(incident.Id);
        Assert.Equal(IncidentContract.Rejected, state.Status);
        Assert.Equal(UtcMicrosecondPrecision.Normalize(ResolvedAt), state.ResolvedAt);
        Assert.Single(await ReadResolutionAuditsAsync(scenario, incident.Id));
        Assert.Equal(1, await CountResolutionReservationsAsync(scenario, completedWith: 200));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Reusing_a_key_for_a_different_resolution_conflicts_without_a_second_effect()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        var other = await OpenIncidentAsync(scenario, initialize: false);
        var command = ResolveCommand(scenario, incident.Id, IncidentContract.Resolved);
        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            await scope.Service.ResolveAsync(command, CancellationToken.None);
        }

        await using var retry = CreateIncidentScope(ResolvedAt.AddMinutes(1));
        foreach (var mismatch in new[]
                 {
                     command with { Outcome = IncidentContract.Rejected },
                     command with { Reason = "Otro motivo del cierre." },
                     command with { IncidentId = other.Id },
                 })
        {
            var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
                retry.Service.ResolveAsync(mismatch, CancellationToken.None));
            Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        }

        var state = await ReadIncidentStateAsync(incident.Id);
        Assert.Equal(IncidentContract.Resolved, state.Status);
        Assert.Equal(UtcMicrosecondPrecision.Normalize(ResolvedAt), state.ResolvedAt);
        Assert.Single(await ReadResolutionAuditsAsync(scenario, incident.Id));
        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, other.Id, expectedReservations: 1);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_corrupt_stored_resolution_fails_closed_without_exposing_it()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        var command = ResolveCommand(scenario, incident.Id, IncidentContract.Resolved);
        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            await scope.Service.ResolveAsync(command, CancellationToken.None);
        }

        await scenario.ExecuteAdminAsync(
            """
            UPDATE platform.idempotency_keys SET response_body='{"id":"not-a-uuid"}'::jsonb
            WHERE owner_org_id=@org AND scope='INC-001:RESOLVE_INCIDENT';
            """,
            SyntheticOrderScenario.P("org", scenario.OrganizationId));

        await using var replay = CreateIncidentScope(ResolvedAt.AddMinutes(1));
        var conflict = await Assert.ThrowsAsync<IncidentConflictException>(() =>
            replay.Service.ResolveAsync(command, CancellationToken.None));

        // An internal-only code; the endpoint publishes it as the generic CONFLICT.
        Assert.Equal("IDEMPOTENCY_CORRUPT", conflict.Code);
        Assert.Single(await ReadResolutionAuditsAsync(scenario, incident.Id));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Replaying_the_opening_after_resolution_still_returns_the_original_opening()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");
        var proofId = await InsertProofAsync(scenario);
        var opening = OpenCommand(scenario, proofId);
        IncidentResult opened;
        await using (var scope = CreateIncidentScope(OpenedAt))
        {
            opened = await scope.Service.OpenAsync(opening, CancellationToken.None);
        }

        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            await scope.Service.ResolveAsync(
                ResolveCommand(scenario, opened.Id, IncidentContract.Resolved),
                CancellationToken.None);
        }

        await using var retry = CreateIncidentScope(ResolvedAt.AddMinutes(5));
        var replayed = await retry.Service.OpenAsync(opening, CancellationToken.None);

        // The stored opening response is replayed verbatim; the incident itself stays closed.
        Assert.Equal(JsonSerializer.Serialize(opened), JsonSerializer.Serialize(replayed));
        Assert.Equal(IncidentContract.Open, replayed.Status);
        Assert.Equal(IncidentContract.Resolved, (await ReadIncidentStateAsync(opened.Id)).Status);
    }

    // ---------------------------------------------------------------- atomicity and races

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task A_failure_after_the_update_and_the_audit_rolls_everything_back()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        // The audit entry is written for real, inside the transaction, and then the transaction
        // fails: the status, resolved_at, the audit row and the reservation must all disappear.
        await using var scope = CreateIncidentScope(ResolvedAt, failAfterAudit: true);

        await Assert.ThrowsAsync<InjectedResolutionFailure>(() =>
            scope.Service.ResolveAsync(
                ResolveCommand(scenario, incident.Id, IncidentContract.Resolved),
                CancellationToken.None));

        await AssertStillOpenWithoutResolutionEffectsAsync(scenario, incident.Id);

        // The same request then succeeds once, cleanly.
        await using var retry = CreateIncidentScope(ResolvedAt);
        var result = await retry.Service.ResolveAsync(
            ResolveCommand(scenario, incident.Id, IncidentContract.Resolved),
            CancellationToken.None);
        Assert.Equal(IncidentContract.Resolved, result.Status);
        Assert.Single(await ReadResolutionAuditsAsync(scenario, incident.Id));
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task Racing_resolved_and_rejected_requests_produce_exactly_one_winner()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);

        // Hold the incident row so both requests reach the row lock before either can proceed.
        await using var blocker = await fixture.AdminDataSource.OpenConnectionAsync();
        await using var blocking = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand(
            "SELECT 1 FROM incidents.incidents WHERE id=@incident FOR UPDATE;",
            blocker,
            blocking))
        {
            hold.Parameters.AddWithValue("incident", incident.Id);
            await hold.ExecuteScalarAsync();
        }

        await using var resolving = CreateIncidentScope(ResolvedAt);
        await using var rejecting = CreateIncidentScope(ResolvedAt);
        var resolve = Capture(resolving.Service.ResolveAsync(
            ResolveCommand(scenario, incident.Id, IncidentContract.Resolved),
            CancellationToken.None));
        var reject = Capture(rejecting.Service.ResolveAsync(
            ResolveCommand(scenario, incident.Id, IncidentContract.Rejected),
            CancellationToken.None));

        await WaitForRowLockWaitersAsync(incident.Id, expected: 2);
        await blocking.CommitAsync();
        var outcomes = await Task.WhenAll(resolve, reject);

        var winner = Assert.Single(outcomes, outcome => outcome.Result is not null);
        var loser = Assert.Single(outcomes, outcome => outcome.Failure is not null);
        Assert.Equal("INCIDENT_STATE_CONFLICT", Assert.IsType<IncidentConflictException>(loser.Failure).Code);

        var state = await ReadIncidentStateAsync(incident.Id);
        Assert.Equal(winner.Result!.Status, state.Status);
        Assert.Single(await ReadResolutionAuditsAsync(scenario, incident.Id));
        Assert.Equal(1, await CountResolutionReservationsAsync(scenario, completedWith: 200));
    }

    // ------------------------------------------------------------- order and evidence boundary

    [Theory]
    [Trait("Category", "PostgreSqlContract")]
    [InlineData("RESOLVED")]
    [InlineData("REJECTED")]
    public async Task Closing_an_incident_never_touches_its_order_or_its_evidence(string outcome)
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);
        var before = await ReadOrderAndEvidenceAsync(scenario);

        await using var scope = CreateIncidentScope(ResolvedAt);
        await scope.Service.ResolveAsync(
            ResolveCommand(scenario, incident.Id, outcome),
            CancellationToken.None);

        var after = await ReadOrderAndEvidenceAsync(scenario);
        Assert.Equal("DELIVERING", after.Status);
        Assert.Equal(before, after);
    }

    [PostgreSqlContractFact]
    [Trait("Category", "PostgreSqlContract")]
    public async Task ORD_002_counts_exactly_open_and_investigating_incidents_as_unresolved()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        var incident = await OpenIncidentAsync(scenario);

        Assert.True(await ReadHasUnresolvedIncidentAsync(scenario));
        await SetIncidentStatusAsync(scenario, incident.Id, IncidentContract.Investigating);
        Assert.True(await ReadHasUnresolvedIncidentAsync(scenario));

        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            await scope.Service.ResolveAsync(
                ResolveCommand(scenario, incident.Id, IncidentContract.Resolved),
                CancellationToken.None);
        }

        Assert.False(await ReadHasUnresolvedIncidentAsync(scenario));

        var rejected = await OpenIncidentAsync(scenario, initialize: false);
        Assert.True(await ReadHasUnresolvedIncidentAsync(scenario));
        await using (var scope = CreateIncidentScope(ResolvedAt))
        {
            await scope.Service.ResolveAsync(
                ResolveCommand(scenario, rejected.Id, IncidentContract.Rejected),
                CancellationToken.None);
        }

        Assert.False(await ReadHasUnresolvedIncidentAsync(scenario));
    }

    // -------------------------------------------------------------------------------- helpers

    private static ResolveIncidentCommand ResolveCommand(
        SyntheticOrderScenario scenario,
        Guid incidentId,
        string outcome,
        string reason = ResolutionReason) =>
        new(
            scenario.UserId,
            scenario.OrganizationId,
            MfaSatisfied: false,
            $"inc001-resolve-{Guid.NewGuid():N}",
            incidentId,
            outcome,
            reason,
            "synthetic-request-id");

    private static OpenIncidentCommand OpenCommand(SyntheticOrderScenario scenario, Guid proofId) =>
        new(
            scenario.UserId,
            scenario.OrganizationId,
            MfaSatisfied: true,
            $"inc001-open-{Guid.NewGuid():N}",
            scenario.OrderId,
            "FAILED_DELIVERY_ATTEMPT",
            IncidentContract.Medium,
            Description,
            IncidentContract.RecipientAbsent,
            IncidentContract.Rescheduled,
            OpenedAt.AddMinutes(-5),
            [proofId],
            "synthetic-request-id");

    private async Task<IncidentResult> OpenIncidentAsync(
        SyntheticOrderScenario scenario,
        bool initialize = true)
    {
        if (initialize)
        {
            await scenario.InitializeAsync(orderStatus: "DELIVERING");
        }

        var proofId = await InsertProofAsync(scenario);
        await using var scope = CreateIncidentScope(OpenedAt);
        return await scope.Service.OpenAsync(OpenCommand(scenario, proofId), CancellationToken.None);
    }

    private static Task SetIncidentStatusAsync(
        SyntheticOrderScenario scenario,
        Guid incidentId,
        string status) =>
        scenario.ExecuteAdminAsync(
            "UPDATE incidents.incidents SET status=@status WHERE id=@incident;",
            SyntheticOrderScenario.P("status", status),
            SyntheticOrderScenario.P("incident", incidentId));

    /// <summary>
    /// Turns the scenario user into the order's currently assigned driver, the one INC-001 lets
    /// open an incident on it.
    /// </summary>
    private static Task MakeScenarioUserTheAssignedDriverAsync(SyntheticOrderScenario scenario) =>
        scenario.ExecuteAdminAsync(
            """
            UPDATE organizations.organization_memberships SET role='DRIVER'
            WHERE user_id=@user AND organization_id=@org;
            INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE');
            INSERT INTO dispatch.assignments(
              id,order_id,owner_org_id,driver_id,assignment_type,status,cost_cents,accepted_at)
            VALUES (gen_random_uuid(),@order,@org,@driver,'OWN','ACTIVE',0,clock_timestamp());
            """,
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("city", scenario.CityId),
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("driver", Guid.NewGuid()));

    private static async Task<Guid> InsertProofAsync(SyntheticOrderScenario scenario)
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
            SyntheticOrderScenario.P("order", scenario.OrderId),
            SyntheticOrderScenario.P("org", scenario.OrganizationId),
            SyntheticOrderScenario.P("user", scenario.UserId),
            SyntheticOrderScenario.P("quarantine", $"quarantine/{uploadId:N}"),
            SyntheticOrderScenario.P("object_key", $"proofs/{uploadId:N}"));
        return proofId;
    }

    private async Task<(string Status, DateTimeOffset? ResolvedAt)> ReadIncidentStateAsync(Guid incidentId)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            "SELECT status,resolved_at FROM incidents.incidents WHERE id=@incident;");
        verify.Parameters.AddWithValue("incident", incidentId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<IReadOnlyList<(string Action, Guid ActorId, string Payload, DateTimeOffset OccurredAt)>> ReadResolutionAuditsAsync(
        SyntheticOrderScenario scenario,
        Guid incidentId)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT action,actor_id,payload_redacted::text,occurred_at FROM platform.audit_logs
            WHERE entity_id=@incident
              AND action IN ('incidents.incident.resolved','incidents.incident.rejected');
            """);
        verify.Parameters.AddWithValue("incident", incidentId);
        var audits = new List<(string, Guid, string, DateTimeOffset)>();
        await using var reader = await verify.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            audits.Add((
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return audits;
    }

    /// <summary>
    /// The stored payload is exactly what the platform redactor makes of the expected resolution
    /// metadata: it went through the redactor, and it holds nothing beyond those fields.
    /// </summary>
    private static void AssertRedactedPayload(object expected, string stored)
    {
        using var source = JsonDocument.Parse(
            JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var redacted = JsonDocument.Parse(new AuditPayloadRedactor().Redact(source.RootElement).Json);
        using var actual = JsonDocument.Parse(stored);
        Assert.Equal(
            redacted.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal),
            actual.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var property in redacted.RootElement.EnumerateObject())
        {
            var value = actual.RootElement.GetProperty(property.Name);
            Assert.Equal(property.Value.ValueKind, value.ValueKind);
            Assert.Equal(property.Value.ToString(), value.ToString());
        }
    }

    private async Task<long> CountResolutionReservationsAsync(
        SyntheticOrderScenario scenario,
        int? completedWith = null)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT count(*) FROM platform.idempotency_keys
            WHERE owner_org_id=@org AND scope='INC-001:RESOLVE_INCIDENT'
              AND (@status::int IS NULL OR response_status=@status::int);
            """);
        verify.Parameters.AddWithValue("org", scenario.OrganizationId);
        verify.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = (object?)completedWith ?? DBNull.Value,
        });
        return (long)(await verify.ExecuteScalarAsync())!;
    }

    private async Task AssertStillOpenWithoutResolutionEffectsAsync(
        SyntheticOrderScenario scenario,
        Guid incidentId,
        long expectedReservations = 0)
    {
        var state = await ReadIncidentStateAsync(incidentId);
        Assert.Equal(IncidentContract.Open, state.Status);
        Assert.Null(state.ResolvedAt);
        Assert.Empty(await ReadResolutionAuditsAsync(scenario, incidentId));
        Assert.Equal(expectedReservations, await CountResolutionReservationsAsync(scenario));
    }

    private async Task<(string Status, int Version, long Events, string Evidence)> ReadOrderAndEvidenceAsync(
        SyntheticOrderScenario scenario)
    {
        await using var verify = fixture.AdminDataSource.CreateCommand(
            """
            SELECT o.status,o.version,
                   (SELECT count(*) FROM orders.order_events e WHERE e.order_id=o.id),
                   (SELECT coalesce(string_agg(x.id::text||':'||x.proof_id::text||':'||x.created_at::text,',' ORDER BY x.id),'')
                      FROM incidents.incident_evidence x WHERE x.order_id=o.id)
            FROM orders.orders o WHERE o.id=@order;
            """);
        verify.Parameters.AddWithValue("order", scenario.OrderId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3));
    }

    /// <summary>
    /// Reads the order-side pending-incident flag through the ORD-002 reader itself, inside a
    /// tenant transaction of the runtime role, exactly as the CLOSED guard reads it.
    /// </summary>
    private async Task<bool> ReadHasUnresolvedIncidentAsync(SyntheticOrderScenario scenario)
    {
        await using var transaction = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            scenario.UserId,
            [scenario.OrganizationId]);
        var snapshot = await new PostgreSqlOrderIncidentGuardReader().ReadAsync(
            transaction.Connection,
            transaction.Transaction,
            scenario.OrganizationId,
            scenario.OrderId,
            requestedIncidentId: null,
            CancellationToken.None);
        return snapshot.HasUnresolvedIncident;
    }

    private async Task WaitForRowLockWaitersAsync(Guid incidentId, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var probe = fixture.AdminDataSource.CreateCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type='Lock' AND query LIKE '%FOR UPDATE%'
                  AND query LIKE '%FROM incidents.incidents i%';
                """);
            if ((long)(await probe.ExecuteScalarAsync())! >= expected)
            {
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Both resolutions never reached the lock on {incidentId}.");
            await Task.Delay(25);
        }
    }

    private static async Task<(IncidentResult? Result, Exception? Failure)> Capture(Task<IncidentResult> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private IncidentScope CreateIncidentScope(DateTimeOffset now, bool failAfterAudit = false)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<IncidentsDbContext>()
            .UseNpgsql(fixture.AppDataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new IncidentsDbContext(options, state);
        IAppendOnlyAuditWriter auditWriter = new PostgreSqlAppendOnlyAuditWriter(state);
        if (failAfterAudit)
        {
            auditWriter = new FailingAfterAuditWriter(auditWriter);
        }

        var service = new PostgreSqlIncidentService(
            new TenantTransactionContext<IncidentsDbContext>(context, state),
            auditWriter,
            new AuditPayloadRedactor(),
            new DeterministicMockIncidentPiiProtector(IncidentsPostgreSqlContractTests.TestKeyVersion),
            Options.Create(new IncidentsOptions
            {
                PiiProtector = IncidentPiiProtectorKind.Mock,
                PiiKeyVersion = IncidentsPostgreSqlContractTests.TestKeyVersion,
            }),
            new FixedClock(now));
        return new IncidentScope(context, service);
    }

    private sealed class IncidentScope(
        IncidentsDbContext context,
        IIncidentService service) : IAsyncDisposable
    {
        internal IIncidentService Service { get; } = service;
        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    /// <summary>Writes the real audit entry, then fails the transaction that holds it.</summary>
    private sealed class FailingAfterAuditWriter(IAppendOnlyAuditWriter inner) : IAppendOnlyAuditWriter
    {
        public async Task WriteAsync(
            DbConnection connection,
            DbTransaction transaction,
            AuditEntry entry,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(connection, transaction, entry, cancellationToken);
            throw new InjectedResolutionFailure();
        }
    }

    private sealed class InjectedResolutionFailure() : Exception("Injected failure after the resolution audit.");
}
