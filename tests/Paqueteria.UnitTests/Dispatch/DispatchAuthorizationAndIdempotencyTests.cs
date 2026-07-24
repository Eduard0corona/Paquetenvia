using System.Security.Cryptography;
using Dispatch.Application.Assignments;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class DispatchAuthorizationAndIdempotencyTests
{
    private readonly DispatchAssignmentAuthorizer authorizer = new();

    [Theory]
    [InlineData("PLATFORM_ADMIN", true, true)]
    [InlineData("PLATFORM_ADMIN", false, false)]
    [InlineData("DISPATCHER", false, true)]
    [InlineData("DRIVER", true, false)]
    [InlineData("VIEWER", true, false)]
    [InlineData("FINANCE", true, false)]
    [InlineData("ALLY_ADMIN", true, false)]
    [InlineData("ALLY_OPERATOR", true, false)]
    [InlineData("BUSINESS_ADMIN", true, false)]
    [InlineData("BUSINESS_OPERATOR", true, false)]
    [InlineData("UNKNOWN", true, false)]
    public void Authorization_is_allowlisted(string role, bool mfa, bool expected)
    {
        Assert.Equal(expected, authorizer.IsAuthorized(
            new DispatchAssignmentAuthorizationContext(role, true, true, mfa)));
    }

    [Fact]
    public void Inactive_user_or_membership_is_denied()
    {
        Assert.False(authorizer.IsAuthorized(
            new DispatchAssignmentAuthorizationContext("DISPATCHER", false, true, false)));
        Assert.False(authorizer.IsAuthorized(
            new DispatchAssignmentAuthorizationContext("DISPATCHER", true, false, false)));
    }

    [Fact]
    public void Canonical_hash_is_deterministic_and_excludes_actor_mfa_and_request_id()
    {
        var command = ValidCommand();
        var changedContext = command with
        {
            ActorId = Guid.NewGuid(),
            MfaSatisfied = !command.MfaSatisfied,
            RequestId = "different",
        };

        Assert.True(CryptographicOperations.FixedTimeEquals(
            AssignmentCanonicalizer.ComputeSha256(command),
            AssignmentCanonicalizer.ComputeSha256(changedContext)));
    }

    [Fact]
    public void Tenant_order_driver_and_int64_cost_change_hash()
    {
        var command = ValidCommand();
        var baseline = AssignmentCanonicalizer.ComputeSha256(command);
        var variants = new[]
        {
            command with { OrganizationId = Guid.NewGuid() },
            command with { OrderId = Guid.NewGuid() },
            command with { DriverId = Guid.NewGuid() },
            command with { CostCents = (long)int.MaxValue + 1 },
        };

        Assert.All(variants, variant => Assert.False(CryptographicOperations.FixedTimeEquals(
            baseline,
            AssignmentCanonicalizer.ComputeSha256(variant))));
    }

    [Fact]
    public void Replay_requires_exact_assignment_and_historical_event_evidence()
    {
        var command = ValidCommand();
        var assignmentId = Guid.NewGuid();
        var stored = new AssignmentResult(
            assignmentId,
            command.OrderId,
            command.DriverId,
            "ACCEPTED",
            new MoneyResult("MXN", command.CostCents!.Value));
        var evidence = new AssignmentReplayEvidence(
            1,
            assignmentId,
            command.OrderId,
            command.DriverId,
            "ACTIVE",
            command.CostCents,
            1,
            "RESCHEDULED",
            "ASSIGNED");

        Assert.True(AssignmentReplayPolicy.IsConsistent(
            command,
            stored,
            assignmentId,
            evidence));
        Assert.False(AssignmentReplayPolicy.IsConsistent(
            command,
            stored,
            assignmentId,
            evidence with { MatchingTransitionEventCount = 0 }));
    }

    private static CreateOwnDriverAssignmentCommand ValidCommand() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "DSP-002-key-0001",
        Guid.NewGuid(),
        Guid.NewGuid(),
        "OWN",
        0,
        null,
        true,
        "request");
}
