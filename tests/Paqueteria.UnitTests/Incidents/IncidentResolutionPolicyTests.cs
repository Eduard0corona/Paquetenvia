using Incidents.Application.Incidents;
using Incidents.Domain;

namespace Paqueteria.UnitTests.Incidents;

/// <summary>
/// The INC-001 resolution rules in isolation: the closed outcome vocabulary, the pending-to-terminal
/// state machine, the reason policy and the supervisory authorization matrix.
/// </summary>
public sealed class IncidentResolutionPolicyTests
{
    private static ResolveIncidentCommand ValidCommand(
        string? outcome = null,
        string? reason = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MfaSatisfied: false,
            "idempotency-key-0001",
            Guid.NewGuid(),
            outcome ?? IncidentContract.Resolved,
            reason ?? "El cliente confirmó una nueva ventana de entrega.",
            RequestId: null);

    [Fact]
    public void The_incident_vocabulary_keeps_exactly_four_statuses_and_two_outcomes()
    {
        Assert.Equal(
            ["Open", "Investigating", "Resolved", "Rejected"],
            Enum.GetNames<IncidentStatus>());
        Assert.Equal(["Resolved", "Rejected"], Enum.GetNames<IncidentResolutionOutcome>());

        // Every outcome is one of the existing terminal statuses, never a new status.
        foreach (var outcome in Enum.GetValues<IncidentResolutionOutcome>())
        {
            var status = IncidentResolutionPolicy.TerminalStatus(outcome);
            Assert.True(IncidentResolutionPolicy.IsTerminal(status));
            Assert.Equal(outcome.ToContractValue(), status.ToContractValue());
        }
    }

    [Theory]
    [InlineData("OPEN", IncidentStatus.Open)]
    [InlineData("INVESTIGATING", IncidentStatus.Investigating)]
    [InlineData("RESOLVED", IncidentStatus.Resolved)]
    [InlineData("REJECTED", IncidentStatus.Rejected)]
    public void Every_published_status_round_trips(string value, IncidentStatus expected)
    {
        Assert.True(IncidentContract.TryParseStatus(value, out var status));
        Assert.Equal(expected, status);
        Assert.Equal(value, status.ToContractValue());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("CLOSED")]
    [InlineData("")]
    [InlineData(null)]
    public void A_status_outside_the_vocabulary_does_not_parse(string? value)
    {
        Assert.False(IncidentContract.TryParseStatus(value, out _));
    }

    [Theory]
    [InlineData("RESOLVED", IncidentResolutionOutcome.Resolved)]
    [InlineData("REJECTED", IncidentResolutionOutcome.Rejected)]
    public void Only_the_terminal_statuses_parse_as_an_outcome(
        string value,
        IncidentResolutionOutcome expected)
    {
        Assert.True(IncidentContract.TryParseResolutionOutcome(value, out var outcome));
        Assert.Equal(expected, outcome);
        Assert.Equal(value, outcome.ToContractValue());
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("INVESTIGATING")]
    [InlineData("resolved")]
    [InlineData(" RESOLVED")]
    [InlineData("RESOLVED ")]
    [InlineData("CLOSED")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_an_outcome(string? value)
    {
        Assert.False(IncidentContract.TryParseResolutionOutcome(value, out _));
    }

    [Fact]
    public void Pending_and_terminal_partition_the_vocabulary_exactly_as_ORD_002_reads_it()
    {
        // ORD-002 counts OPEN and INVESTIGATING as an unresolved incident; nothing else is pending.
        Assert.Equal(
            [IncidentStatus.Open, IncidentStatus.Investigating],
            Enum.GetValues<IncidentStatus>().Where(IncidentResolutionPolicy.IsPending));
        Assert.Equal(
            [IncidentStatus.Resolved, IncidentStatus.Rejected],
            Enum.GetValues<IncidentStatus>().Where(IncidentResolutionPolicy.IsTerminal));
    }

    [Theory]
    [InlineData(IncidentStatus.Open, IncidentResolutionOutcome.Resolved, true)]
    [InlineData(IncidentStatus.Open, IncidentResolutionOutcome.Rejected, true)]
    [InlineData(IncidentStatus.Investigating, IncidentResolutionOutcome.Resolved, true)]
    [InlineData(IncidentStatus.Investigating, IncidentResolutionOutcome.Rejected, true)]
    [InlineData(IncidentStatus.Resolved, IncidentResolutionOutcome.Resolved, false)]
    [InlineData(IncidentStatus.Resolved, IncidentResolutionOutcome.Rejected, false)]
    [InlineData(IncidentStatus.Rejected, IncidentResolutionOutcome.Resolved, false)]
    [InlineData(IncidentStatus.Rejected, IncidentResolutionOutcome.Rejected, false)]
    public void Only_a_pending_incident_can_close_and_a_terminal_one_never_moves_again(
        IncidentStatus current,
        IncidentResolutionOutcome outcome,
        bool allowed)
    {
        Assert.Equal(allowed, IncidentResolutionPolicy.CanResolve(current, outcome));
    }

    [Fact]
    public void An_undefined_outcome_can_never_close_an_incident()
    {
        Assert.False(IncidentResolutionPolicy.CanResolve(IncidentStatus.Open, (IncidentResolutionOutcome)99));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IncidentResolutionPolicy.TerminalStatus((IncidentResolutionOutcome)99));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    public void Only_a_dispatcher_or_an_MFA_satisfied_platform_admin_may_resolve(
        bool isActiveDispatcher,
        bool isActivePlatformAdmin,
        bool mfaSatisfied,
        bool allowed)
    {
        Assert.Equal(
            allowed,
            IncidentResolutionAuthorizationPolicy.MayResolve(
                isActiveDispatcher,
                isActivePlatformAdmin,
                mfaSatisfied));
    }

    [Theory]
    [InlineData("Reprogramado con el cliente.")]
    [InlineData("x")]
    [InlineData("Dirección confirmada: la entrega se reintenta mañana.")]
    public void A_bounded_plain_text_reason_is_accepted(string reason)
    {
        Assert.True(IncidentRequestPolicy.IsValidResolutionReason(reason));
        Assert.True(IncidentRequestPolicy.IsValidResolveCommandShape(ValidCommand(reason: reason)));
    }

    [Fact]
    public void The_reason_is_bounded_at_the_published_maximum()
    {
        Assert.True(IncidentRequestPolicy.IsValidResolutionReason(
            new string('a', IncidentRequestPolicy.MaximumResolutionReasonLength)));
        Assert.False(IncidentRequestPolicy.IsValidResolutionReason(
            new string('a', IncidentRequestPolicy.MaximumResolutionReasonLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData(" non-breaking lead")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tinside")]
    [InlineData("nul\u0000inside")]
    [InlineData("c1\u0085inside")]
    public void A_missing_padded_or_control_bearing_reason_is_rejected_not_trimmed(string? reason)
    {
        Assert.False(IncidentRequestPolicy.IsValidResolutionReason(reason));
    }

    [Fact]
    public void An_absent_actor_tenant_incident_or_non_terminal_outcome_is_rejected()
    {
        var command = ValidCommand();
        Assert.True(IncidentRequestPolicy.IsValidResolveCommandShape(command));
        Assert.True(IncidentRequestPolicy.IsValidResolveCommandShape(
            command with { Outcome = IncidentContract.Rejected }));
        Assert.False(IncidentRequestPolicy.IsValidResolveCommandShape(command with { ActorId = Guid.Empty }));
        Assert.False(IncidentRequestPolicy.IsValidResolveCommandShape(command with { OrganizationId = Guid.Empty }));
        Assert.False(IncidentRequestPolicy.IsValidResolveCommandShape(command with { IncidentId = Guid.Empty }));
        Assert.False(IncidentRequestPolicy.IsValidResolveCommandShape(
            command with { Outcome = IncidentContract.Open }));
        Assert.False(IncidentRequestPolicy.IsValidResolveCommandShape(
            command with { Outcome = IncidentContract.Investigating }));
        Assert.False(IncidentRequestPolicy.IsValidResolveCommandShape(command with { Reason = " " }));
    }
}
