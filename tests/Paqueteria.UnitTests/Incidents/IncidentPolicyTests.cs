using Incidents.Application.Incidents;
using Incidents.Domain;

namespace Paqueteria.UnitTests.Incidents;

public sealed class IncidentPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);

    private static OpenIncidentCommand ValidCommand(
        string? description = null,
        string? severity = null,
        IReadOnlyList<Guid>? evidence = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MfaSatisfied: false,
            "idempotency-key-0001",
            Guid.NewGuid(),
            "FAILED_DELIVERY_ATTEMPT",
            severity ?? IncidentContract.Medium,
            description ?? "El destinatario no se encontraba en el domicilio.",
            IncidentContract.RecipientAbsent,
            IncidentContract.Rescheduled,
            Now.AddMinutes(-10),
            evidence ?? [Guid.NewGuid()],
            RequestId: null);

    [Fact]
    public void Incident_vocabulary_remains_exactly_v06()
    {
        Assert.Equal(
            ["Open", "Investigating", "Resolved", "Rejected"],
            Enum.GetNames<IncidentStatus>());
        Assert.Equal(
            ["Low", "Medium", "High", "Critical"],
            Enum.GetNames<IncidentSeverity>());
    }

    [Fact]
    public void The_only_explicit_next_actions_are_the_ones_ORD_002_accepts_after_a_failed_attempt()
    {
        Assert.Equal(["Rescheduled", "Returning"], Enum.GetNames<IncidentNextAction>());
        Assert.True(IncidentContract.TryParseNextAction("RESCHEDULED", out var rescheduled));
        Assert.Equal(IncidentNextAction.Rescheduled, rescheduled);
        Assert.True(IncidentContract.TryParseNextAction("RETURNING", out var returning));
        Assert.Equal(IncidentNextAction.Returning, returning);
    }

    [Theory]
    [InlineData("DELIVERING")]
    [InlineData("DELIVERED")]
    [InlineData("FAILED_ATTEMPT")]
    [InlineData("CLOSED")]
    [InlineData("")]
    [InlineData(null)]
    public void A_next_action_outside_the_contract_is_rejected(string? value)
    {
        Assert.False(IncidentContract.TryParseNextAction(value, out _));
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand() with { NextAction = value! }));
    }

    [Theory]
    [InlineData("RECIPIENT_ABSENT")]
    [InlineData("ADDRESS_NOT_FOUND")]
    [InlineData("RECIPIENT_REFUSED")]
    [InlineData("ACCESS_RESTRICTED")]
    [InlineData("PAYMENT_UNAVAILABLE")]
    [InlineData("PACKAGE_DAMAGED")]
    [InlineData("SECURITY_RISK")]
    public void Every_contract_reason_code_round_trips(string value)
    {
        Assert.True(IncidentContract.TryParseReasonCode(value, out var reasonCode));
        Assert.Equal(value, reasonCode.ToContractValue());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("recipient_absent")]
    [InlineData("SOMETHING_ELSE")]
    public void A_missing_or_unknown_reason_is_rejected(string? value)
    {
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand() with { ReasonCode = value! }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_description_is_rejected(string? value)
    {
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand() with { Description = value! }));
    }

    [Fact]
    public void A_description_beyond_the_contract_length_is_rejected()
    {
        var overlong = new string('a', IncidentRequestPolicy.MaximumDescriptionLength + 1);
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand(description: overlong)));
        Assert.True(IncidentRequestPolicy.IsValidCommandShape(
            ValidCommand(description: overlong[..IncidentRequestPolicy.MaximumDescriptionLength])));
    }

    [Fact]
    public void Evidence_is_mandatory()
    {
        Assert.False(IncidentRequestPolicy.IsValidEvidence(null));
        Assert.False(IncidentRequestPolicy.IsValidEvidence([]));
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand(evidence: [])));
        Assert.True(IncidentRequestPolicy.IsValidEvidence([Guid.NewGuid()]));
    }

    [Fact]
    public void Empty_duplicated_or_excessive_evidence_is_rejected()
    {
        var duplicate = Guid.NewGuid();
        Assert.False(IncidentRequestPolicy.IsValidEvidence([Guid.Empty]));
        Assert.False(IncidentRequestPolicy.IsValidEvidence([duplicate, duplicate]));
        Assert.False(IncidentRequestPolicy.IsValidEvidence(
            Enumerable.Range(0, IncidentEvidencePolicy.MaximumEvidenceCount + 1)
                .Select(_ => Guid.NewGuid())
                .ToArray()));
        Assert.True(IncidentRequestPolicy.IsValidEvidence(
            Enumerable.Range(0, IncidentEvidencePolicy.MaximumEvidenceCount)
                .Select(_ => Guid.NewGuid())
                .ToArray()));
    }

    [Theory]
    [InlineData("CRITICAL", 2)]
    [InlineData("HIGH", 8)]
    [InlineData("MEDIUM", 24)]
    [InlineData("LOW", 72)]
    public void The_sla_timestamp_is_derived_from_severity(string severity, int hours)
    {
        Assert.True(IncidentContract.TryParseSeverity(severity, out var parsed));
        Assert.Equal(TimeSpan.FromHours(hours), IncidentSlaPolicy.ResolutionWindow(parsed));
        Assert.Equal(Now.AddHours(hours), IncidentSlaPolicy.DueAt(Now, parsed));
    }

    [Fact]
    public void The_sla_timestamp_is_deterministic_for_the_same_attempt()
    {
        Assert.True(IncidentContract.TryParseSeverity(IncidentContract.High, out var severity));
        Assert.Equal(
            IncidentSlaPolicy.DueAt(Now, severity),
            IncidentSlaPolicy.DueAt(Now, severity));
    }

    [Fact]
    public void The_sla_timestamp_is_always_after_the_attempt()
    {
        foreach (var severity in Enum.GetValues<IncidentSeverity>())
        {
            Assert.True(IncidentSlaPolicy.DueAt(Now, severity) > Now);
        }
    }

    [Theory]
    [InlineData("AT_PICKUP", true)]
    [InlineData("IN_TRANSIT", true)]
    [InlineData("DELIVERING", true)]
    [InlineData("DRAFT", false)]
    [InlineData("CONFIRMED", false)]
    [InlineData("ASSIGNED", false)]
    [InlineData("PICKED_UP", false)]
    [InlineData("FAILED_ATTEMPT", false)]
    [InlineData("DELIVERED", false)]
    [InlineData("CLOSED", false)]
    [InlineData("CANCELLED", false)]
    [InlineData(null, false)]
    public void Incidents_open_only_from_the_states_that_can_reach_a_failed_attempt(
        string? orderStatus,
        bool allowed)
    {
        Assert.Equal(allowed, IncidentOrderStatePolicy.IsAllowedOpeningState(orderStatus));
    }

    [Fact]
    public void An_attempt_reported_in_the_future_or_long_past_is_rejected()
    {
        Assert.True(IncidentRequestPolicy.IsValidOccurrence(Now, Now));
        Assert.True(IncidentRequestPolicy.IsValidOccurrence(Now.AddMinutes(-30), Now));
        Assert.False(IncidentRequestPolicy.IsValidOccurrence(Now.AddHours(1), Now));
        Assert.False(IncidentRequestPolicy.IsValidOccurrence(default, Now));
    }

    [Fact]
    public void The_retrospective_window_is_the_approved_seventy_two_hours()
    {
        Assert.Equal(TimeSpan.FromHours(72), IncidentRequestPolicy.MaximumOccurrenceAge);
        Assert.True(IncidentRequestPolicy.IsValidOccurrence(Now.AddHours(-71), Now));
        Assert.True(IncidentRequestPolicy.IsValidOccurrence(Now.AddHours(-72), Now));
        Assert.False(IncidentRequestPolicy.IsValidOccurrence(Now.AddHours(-73), Now));
    }

    [Fact]
    public void A_small_clock_skew_between_device_and_server_is_tolerated()
    {
        Assert.True(IncidentRequestPolicy.IsValidOccurrence(Now.AddMinutes(1), Now));
        Assert.False(IncidentRequestPolicy.IsValidOccurrence(Now.AddMinutes(6), Now));
    }

    [Fact]
    public void A_complete_opening_request_is_accepted()
    {
        Assert.True(IncidentRequestPolicy.IsValidCommandShape(ValidCommand()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lowercase")]
    [InlineData("WITH SPACE")]
    [InlineData("WITH-DASH")]
    public void An_incident_type_outside_the_contract_shape_is_rejected(string? value)
    {
        Assert.False(IncidentRequestPolicy.IsValidIncidentType(value));
    }

    [Fact]
    public void An_unknown_severity_is_rejected()
    {
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand(severity: "URGENT")));
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(ValidCommand() with { Severity = null! }));
    }


    // ------------------------------------------------------- RETURNING requires custody

    [Theory]
    [InlineData("AT_PICKUP", false, true)]
    [InlineData("AT_PICKUP", true, true)]
    [InlineData("IN_TRANSIT", true, true)]
    [InlineData("DELIVERING", true, true)]
    [InlineData("PICKED_UP", true, false)]
    [InlineData("DRAFT", false, false)]
    [InlineData(null, false, false)]
    public void Rescheduling_is_available_from_every_state_an_incident_may_open_from(
        string? orderStatus,
        bool custodyAcquired,
        bool allowed)
    {
        Assert.Equal(
            allowed,
            IncidentOrderStatePolicy.IsAllowedNextAction(
                orderStatus, custodyAcquired, IncidentNextAction.Rescheduled));
    }

    [Theory]
    [InlineData("AT_PICKUP", false, false)]
    [InlineData("AT_PICKUP", true, true)]
    [InlineData("IN_TRANSIT", true, true)]
    [InlineData("IN_TRANSIT", false, false)]
    [InlineData("DELIVERING", true, true)]
    [InlineData("DELIVERING", false, false)]
    [InlineData("PICKED_UP", true, false)]
    [InlineData("DRAFT", true, false)]
    [InlineData(null, true, false)]
    public void Returning_requires_custody_from_the_picked_up_history(
        string? orderStatus,
        bool custodyAcquired,
        bool allowed)
    {
        Assert.Equal(
            allowed,
            IncidentOrderStatePolicy.IsAllowedNextAction(
                orderStatus, custodyAcquired, IncidentNextAction.Returning));
    }

    [Fact]
    public void An_attempt_that_failed_at_a_first_pickup_can_be_rescheduled_but_never_returned()
    {
        // ORD-002 and ADR-014 only return what the operator already holds. A pickup photo is not
        // custody: without a PICKED_UP status change the parcel never left the pickup point.
        Assert.True(IncidentOrderStatePolicy.IsAllowedNextAction(
            IncidentOrderStatePolicy.AtPickup, false, IncidentNextAction.Rescheduled));
        Assert.False(IncidentOrderStatePolicy.IsAllowedNextAction(
            IncidentOrderStatePolicy.AtPickup, false, IncidentNextAction.Returning));
    }

    // ---------------------------------------------------- Operational MVP-1 parameters

    [Fact]
    public void The_approved_operational_defaults_are_the_MVP1_parameters()
    {
        var policy = IncidentOperationalPolicy.Mvp1;

        Assert.True(policy.IsValid);
        Assert.Equal(TimeSpan.FromHours(2), policy.CriticalResolutionWindow);
        Assert.Equal(TimeSpan.FromHours(8), policy.HighResolutionWindow);
        Assert.Equal(TimeSpan.FromHours(24), policy.MediumResolutionWindow);
        Assert.Equal(TimeSpan.FromHours(72), policy.LowResolutionWindow);
        Assert.Equal(TimeSpan.FromHours(72), policy.MaximumOccurrenceAge);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.MaximumOccurrenceSkew);
        Assert.Equal(1, IncidentEvidencePolicy.MinimumEvidenceCount);
        Assert.Equal(10, policy.MaximumEvidenceCount);
    }

    [Fact]
    public void The_published_static_policies_read_the_same_operational_parameters()
    {
        foreach (var severity in Enum.GetValues<IncidentSeverity>())
        {
            Assert.Equal(
                IncidentOperationalPolicy.Mvp1.ResolutionWindow(severity),
                IncidentSlaPolicy.ResolutionWindow(severity));
        }

        Assert.True(IncidentEvidencePolicy.IsAllowedCount(IncidentEvidencePolicy.MinimumEvidenceCount));
        Assert.True(IncidentEvidencePolicy.IsAllowedCount(IncidentEvidencePolicy.MaximumEvidenceCount));
        Assert.False(IncidentEvidencePolicy.IsAllowedCount(0));
        Assert.False(IncidentEvidencePolicy.IsAllowedCount(IncidentEvidencePolicy.MaximumEvidenceCount + 1));
    }

    [Fact]
    public void An_operational_policy_may_tighten_the_bounds_it_owns()
    {
        var tightened = IncidentOperationalPolicy.Mvp1 with
        {
            MaximumOccurrenceAge = TimeSpan.FromHours(24),
            MaximumEvidenceCount = 3,
        };

        Assert.True(tightened.IsValid);
        Assert.False(tightened.IsValidOccurrence(Now.AddHours(-25), Now));
        Assert.True(tightened.IsAllowedEvidenceCount(3));
        Assert.False(tightened.IsAllowedEvidenceCount(4));
        // The semantic floor stays where AI-08 put it.
        Assert.False(tightened.IsAllowedEvidenceCount(0));
    }

    public static TheoryData<IncidentOperationalPolicy> InvalidOperationalPolicies() =>
    [
        // A zero or negative window is not a deadline.
        IncidentOperationalPolicy.Mvp1 with { CriticalResolutionWindow = TimeSpan.Zero },
        IncidentOperationalPolicy.Mvp1 with { MediumResolutionWindow = TimeSpan.FromHours(-1) },
        // A severer incident may never be given a laxer deadline.
        IncidentOperationalPolicy.Mvp1 with { CriticalResolutionWindow = TimeSpan.FromHours(9) },
        IncidentOperationalPolicy.Mvp1 with { LowResolutionWindow = TimeSpan.FromHours(1) },
        // The retrospective window and the skew are bounded.
        IncidentOperationalPolicy.Mvp1 with { MaximumOccurrenceAge = TimeSpan.Zero },
        IncidentOperationalPolicy.Mvp1 with { MaximumOccurrenceAge = TimeSpan.FromDays(400) },
        IncidentOperationalPolicy.Mvp1 with { MaximumOccurrenceSkew = TimeSpan.FromMinutes(-1) },
        IncidentOperationalPolicy.Mvp1 with { MaximumOccurrenceSkew = TimeSpan.FromHours(2) },
        // Evidence may be tightened, never removed and never widened past the published bound.
        IncidentOperationalPolicy.Mvp1 with { MaximumEvidenceCount = 0 },
        IncidentOperationalPolicy.Mvp1 with { MaximumEvidenceCount = 11 },
    ];

    [Theory]
    [MemberData(nameof(InvalidOperationalPolicies))]
    public void An_operational_policy_outside_the_bounded_surface_is_rejected(
        IncidentOperationalPolicy policy)
    {
        Assert.False(policy.IsValid);
    }

    [Fact]
    public void An_absent_actor_tenant_or_order_is_rejected()
    {
        var command = ValidCommand();
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(command with { ActorId = Guid.Empty }));
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(command with { OrganizationId = Guid.Empty }));
        Assert.False(IncidentRequestPolicy.IsValidCommandShape(command with { OrderId = Guid.Empty }));
    }
}
