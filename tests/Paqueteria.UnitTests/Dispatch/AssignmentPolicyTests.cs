using Dispatch.Application.Assignments;
using Dispatch.Domain;

namespace Paqueteria.UnitTests.Dispatch;

public sealed class AssignmentPolicyTests
{
    [Fact]
    public void Contract_enums_are_exact()
    {
        Assert.Equal(
            ["OWN", "EXTERNAL", "ALLY_CAPACITY"],
            Enum.GetValues<AssignmentType>().Select(value => value.ToContractValue()));
        Assert.Equal(
            ["OFFERED", "ACCEPTED", "ACTIVE", "COMPLETED", "CANCELLED"],
            Enum.GetValues<AssignmentStatus>().Select(value => value.ToContractValue()));
    }

    [Theory]
    [InlineData("READY_FOR_PICKUP", 0)]
    [InlineData("READY_FOR_PICKUP", 42)]
    [InlineData("RESCHEDULED", 0)]
    public void Own_assignment_accepts_the_two_sources_and_non_negative_cost(
        string source,
        long cost)
    {
        var result = ManualOwnAssignmentPolicy.Evaluate(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssignmentType.Own,
            null,
            cost,
            source);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Own_assignment_rejects_negative_cost_route_and_empty_identifiers()
    {
        Assert.Equal(
            AssignmentRuleCode.InvalidCost,
            ManualOwnAssignmentPolicy.Evaluate(
                Guid.NewGuid(), Guid.NewGuid(), AssignmentType.Own, null, -1, "READY_FOR_PICKUP").Code);
        Assert.Equal(
            AssignmentRuleCode.RouteNotSupported,
            ManualOwnAssignmentPolicy.Evaluate(
                Guid.NewGuid(), Guid.NewGuid(), AssignmentType.Own, Guid.NewGuid(), 0, "READY_FOR_PICKUP").Code);
        Assert.Equal(
            AssignmentRuleCode.InvalidIdentifier,
            ManualOwnAssignmentPolicy.Evaluate(
                Guid.Empty, Guid.NewGuid(), AssignmentType.Own, null, 0, "READY_FOR_PICKUP").Code);
    }

    [Theory]
    [InlineData(AssignmentType.External)]
    [InlineData(AssignmentType.AllyCapacity)]
    public void Only_own_is_supported(AssignmentType type)
    {
        var result = ManualOwnAssignmentPolicy.Evaluate(
            Guid.NewGuid(), Guid.NewGuid(), type, null, 0, "READY_FOR_PICKUP");

        Assert.Equal(AssignmentRuleCode.UnsupportedType, result.Code);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CONFIRMED")]
    [InlineData("ASSIGNED")]
    [InlineData("AT_PICKUP")]
    [InlineData("PICKED_UP")]
    [InlineData("IN_TRANSIT")]
    [InlineData("DELIVERING")]
    [InlineData("FAILED_ATTEMPT")]
    [InlineData("RETURNING")]
    [InlineData("RETURNED")]
    [InlineData("DELIVERED")]
    [InlineData("CLOSED")]
    [InlineData("CLAIM_OPEN")]
    [InlineData("CLAIM_RESOLVED")]
    [InlineData("CANCELLED")]
    [InlineData("UNKNOWN")]
    public void Other_order_states_are_rejected(string source)
    {
        var result = ManualOwnAssignmentPolicy.Evaluate(
            Guid.NewGuid(), Guid.NewGuid(), AssignmentType.Own, null, 0, source);

        Assert.Equal(AssignmentRuleCode.InvalidSourceState, result.Code);
    }

    [Fact]
    public void Owner_and_existing_operator_are_derived_without_changing_owner()
    {
        var owner = Guid.NewGuid();
        var currentOperator = Guid.NewGuid();

        Assert.Null(ManualOwnAssignmentPolicy.DeriveOperatorOrganization(owner, owner, currentOperator));
        Assert.Equal(
            currentOperator,
            ManualOwnAssignmentPolicy.DeriveOperatorOrganization(currentOperator, owner, currentOperator));
        Assert.Throws<InvalidOperationException>(() =>
            ManualOwnAssignmentPolicy.DeriveOperatorOrganization(Guid.NewGuid(), owner, currentOperator));
    }

    [Fact]
    public void Factory_creates_accepted_assignment_with_one_utc_timestamp()
    {
        var occurredAt = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var assignment = ManualOwnAssignmentPolicy.CreateAccepted(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            long.MaxValue,
            occurredAt);

        Assert.Equal(AssignmentType.Own, assignment.AssignmentType);
        Assert.Equal(AssignmentStatus.Accepted, assignment.Status);
        Assert.Equal(occurredAt, assignment.AcceptedAt);
        Assert.Equal(occurredAt, assignment.CreatedAt);
        Assert.Equal(long.MaxValue, assignment.CostCents);
    }

    [Fact]
    public void Assignment_result_uses_MXN_int64_money()
    {
        var result = new AssignmentResult(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ACCEPTED",
            new MoneyResult("MXN", (long)int.MaxValue + 1));

        Assert.Equal("MXN", result.Cost.Currency);
        Assert.Equal((long)int.MaxValue + 1, result.Cost.AmountCents);
    }
}
