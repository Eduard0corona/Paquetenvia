using Orders.Application.Orders;
using Orders.Domain;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// ADR-024/LIF-001: a claim is admissible while now &lt;= claim_window_ends_at and finalized_at is
/// null; the finalizer only takes windows strictly before its clock, so the two never overlap.
/// </summary>
public sealed class ClaimWindowBoundaryTests
{
    private static readonly DateTimeOffset WindowEndsAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMicrosecond = TimeSpan.FromTicks(10);

    [Fact]
    public void Claim_is_admissible_before_and_exactly_at_the_window_end()
    {
        Assert.True(OrderTransitionMatrix.Evaluate(
            OrderStatus.Closed, OrderStatus.ClaimOpen, WindowEndsAt - OneMicrosecond, WindowEndsAt, null).Allowed);
        Assert.True(OrderTransitionMatrix.Evaluate(
            OrderStatus.Closed, OrderStatus.ClaimOpen, WindowEndsAt, WindowEndsAt, null).Allowed);

        var registry = new OrderTransitionGuardRegistry();
        Assert.True(registry.Evaluate(ClaimContext(WindowEndsAt, null)).Satisfied);
    }

    [Fact]
    public void Claim_one_microsecond_after_the_window_is_rejected()
    {
        Assert.Equal(
            OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.ClaimWindowExpired),
            OrderTransitionMatrix.Evaluate(
                OrderStatus.Closed, OrderStatus.ClaimOpen, WindowEndsAt + OneMicrosecond, WindowEndsAt, null));

        var guard = new OrderTransitionGuardRegistry().Evaluate(ClaimContext(WindowEndsAt + OneMicrosecond, null));
        Assert.False(guard.Satisfied);
        Assert.Equal("now_before_or_equal_claim_window_ends_at", guard.Code);
    }

    [Fact]
    public void Finalized_closed_order_rejects_claims_even_inside_the_window()
    {
        var finalizedAt = WindowEndsAt + OneMicrosecond;
        Assert.Equal(
            OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.Finalized),
            OrderTransitionMatrix.Evaluate(
                OrderStatus.Closed, OrderStatus.ClaimOpen, WindowEndsAt - TimeSpan.FromHours(1), WindowEndsAt, finalizedAt));

        var guard = new OrderTransitionGuardRegistry().Evaluate(ClaimContext(WindowEndsAt, finalizedAt));
        Assert.False(guard.Satisfied);
        Assert.Equal("now_before_or_equal_claim_window_ends_at", guard.Code);
    }

    [Fact]
    public void Closed_without_a_claim_window_rejects_claims()
    {
        Assert.Equal(
            OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.ClaimWindowExpired),
            OrderTransitionMatrix.Evaluate(OrderStatus.Closed, OrderStatus.ClaimOpen, WindowEndsAt, null, null));
    }

    [Fact]
    public void Claim_resolved_is_final_immediately_for_every_target()
    {
        Assert.Contains(OrderStatus.ClaimResolved, (IReadOnlySet<OrderStatus>)OrderTransitionMatrix.ImmediateTerminalStates);
        Assert.All(Enum.GetValues<OrderStatus>(), target =>
            Assert.Equal(
                OrderTransitionEvaluation.Rejected(OrderTransitionRuleCode.TerminalState),
                OrderTransitionMatrix.Evaluate(
                    OrderStatus.ClaimResolved, target, WindowEndsAt - TimeSpan.FromDays(1), WindowEndsAt, null)));
    }

    private static OrderTransitionGuardContext ClaimContext(DateTimeOffset occurredAt, DateTimeOffset? finalizedAt) => new()
    {
        Source = OrderStatus.Closed,
        Target = OrderStatus.ClaimOpen,
        Reason = "damaged package",
        OccurredAt = occurredAt,
        ClaimWindowEndsAt = WindowEndsAt,
        FinalizedAt = finalizedAt,
        CodExpectedCents = 0,
        MonetaryIntegrityValid = true,
        Metadata = NormalizedTransitionMetadata.Empty,
    };
}
