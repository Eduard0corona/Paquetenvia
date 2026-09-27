using Finance.Application;
using Finance.Application.Settlements;
using Finance.Domain;
using Finance.Domain.Settlements;
using Orders.Domain;

namespace Paqueteria.UnitTests.Finance;

/// <summary>
/// SET-001 Slice 2 application policy: the lifecycle the service decides before the ledger guard,
/// who may operate settlements, which outcomes are payable, what blocks approval and how an
/// adjustment is validated.
/// </summary>
public sealed class SettlementPolicyTests
{
    [Theory]
    [InlineData(SettlementStatus.Draft, false, false, false, true, false)]
    [InlineData(SettlementStatus.Calculated, true, true, false, true, false)]
    [InlineData(SettlementStatus.Approved, false, false, true, true, false)]
    [InlineData(SettlementStatus.Paid, false, false, false, false, true)]
    [InlineData(SettlementStatus.Void, false, false, false, false, true)]
    public void The_lifecycle_admits_exactly_the_published_steps(
        SettlementStatus status,
        bool adjust,
        bool approve,
        bool pay,
        bool voidable,
        bool terminal)
    {
        Assert.Equal(adjust, SettlementLifecyclePolicy.CanAdjust(status));
        Assert.Equal(approve, SettlementLifecyclePolicy.CanApprove(status));
        Assert.Equal(pay, SettlementLifecyclePolicy.CanMarkPaid(status));
        Assert.Equal(voidable, SettlementLifecyclePolicy.CanVoid(status));
        Assert.Equal(terminal, SettlementLifecyclePolicy.IsTerminal(status));
    }

    [Fact]
    public void Contract_values_round_trip_and_reserved_vocabulary_is_refused()
    {
        Assert.Equal(
            ["DRAFT", "CALCULATED", "APPROVED", "PAID", "VOID"],
            SettlementContractValues.AllStatuses.Select(status => status.ToContractValue()));
        Assert.All(SettlementContractValues.AllStatuses, status =>
        {
            Assert.True(SettlementContractValues.TryParseStatus(status.ToContractValue(), out var parsed));
            Assert.Equal(status, parsed);
        });
        Assert.Equal(
            ["DELIVERY", "RETURN", "ADJUSTMENT"],
            SettlementContractValues.AllLineTypes.Select(lineType => lineType.ToContractValue()));
        Assert.All(SettlementContractValues.AllLineTypes, lineType =>
        {
            Assert.True(SettlementContractValues.TryParseLineType(lineType.ToContractValue(), out var parsed));
            Assert.Equal(lineType, parsed);
        });
        Assert.Equal(["DRIVER"], SettlementContractValues.AllPayeeTypes.Select(payee => payee.ToContractValue()));

        foreach (var reserved in new[] { "ROUTE_BASE", "BONUS", "WAITING", "COD", "delivery", "", null })
        {
            Assert.False(SettlementContractValues.TryParseLineType(reserved, out _));
        }

        foreach (var reserved in new[] { "ALLY", "BUSINESS", "driver", "", null })
        {
            Assert.False(SettlementContractValues.TryParsePayeeType(reserved, out _));
        }

        Assert.False(SettlementContractValues.TryParseStatus("CLOSED", out _));
    }

    [Theory]
    [InlineData("FINANCE", false, true)]
    [InlineData("FINANCE", true, true)]
    [InlineData("PLATFORM_ADMIN", true, true)]
    [InlineData("PLATFORM_ADMIN", false, false)]
    [InlineData("DISPATCHER", true, false)]
    [InlineData("DRIVER", true, false)]
    [InlineData("VIEWER", true, false)]
    [InlineData("ALLY_ADMIN", true, false)]
    [InlineData("BUSINESS_ADMIN", true, false)]
    [InlineData(null, true, false)]
    public void Only_active_FINANCE_and_MFA_satisfied_PLATFORM_ADMIN_operate_settlements(
        string? role,
        bool mfa,
        bool expected) =>
        Assert.Equal(
            expected,
            SettlementAuthorizationPolicy.CanOperate(new FinanceAuthorizationContext(role, true, role is not null, mfa, false)));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void An_inactive_user_or_membership_never_operates_settlements(bool userActive, bool membershipActive)
    {
        foreach (var role in new[] { "FINANCE", "PLATFORM_ADMIN" })
        {
            Assert.False(SettlementAuthorizationPolicy.CanOperate(
                new FinanceAuthorizationContext(role, userActive, membershipActive, true, false)));
        }
    }

    [Theory]
    [InlineData("DELIVERED", "DELIVERED", SettlementLineType.Delivery)]
    [InlineData("DELIVERED", "CLOSED", SettlementLineType.Delivery)]
    [InlineData("DELIVERED", "CLAIM_OPEN", SettlementLineType.Delivery)]
    [InlineData("DELIVERED", "CLAIM_RESOLVED", SettlementLineType.Delivery)]
    [InlineData("RETURNED", "RETURNED", SettlementLineType.Return)]
    [InlineData("DELIVERED", "DELIVERING", null)]
    [InlineData("DELIVERED", "RETURNED", null)]
    [InlineData("RETURNED", "RETURNING", null)]
    [InlineData("RETURNED", "DELIVERED", null)]
    [InlineData("OUT_FOR_DELIVERY", "DELIVERED", null)]
    [InlineData("CANCELLED", "CANCELLED", null)]
    [InlineData(null, "DELIVERED", null)]
    [InlineData("DELIVERED", null, null)]
    public void Only_a_proven_delivery_or_return_is_payable(
        string? outcome,
        string? orderStatus,
        SettlementLineType? expected) =>
        Assert.Equal(expected, SettlementSourcePolicy.Classify(outcome, orderStatus));

    /// <summary>
    /// The payable statuses are pinned to the ORD-002 transition matrix: the delivered family is exactly
    /// DELIVERED and what can only follow it, and RETURNED is terminal, so a proven outcome stays proven.
    /// </summary>
    [Fact]
    public void Payable_order_statuses_are_exactly_those_the_order_state_machine_cannot_leave_behind()
    {
        var reachable = new HashSet<OrderStatus> { OrderStatus.Delivered };
        var frontier = new Queue<OrderStatus>(reachable);
        while (frontier.TryDequeue(out var current))
        {
            if (!OrderTransitionMatrix.AllowedTransitions.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets.Where(reachable.Add))
            {
                frontier.Enqueue(target);
            }
        }

        Assert.Equal(
            reachable.Select(status => status.ToContractValue()).Order(StringComparer.Ordinal),
            SettlementSourcePolicy.DeliveredOrderStatuses.Order(StringComparer.Ordinal));
        Assert.Contains(OrderStatus.Returned, (IReadOnlySet<OrderStatus>)OrderTransitionMatrix.ImmediateTerminalStates);
        Assert.False(OrderTransitionMatrix.AllowedTransitions.ContainsKey(OrderStatus.Returned));
        Assert.Equal(["RETURNED"], SettlementSourcePolicy.ReturnedOrderStatuses);
        Assert.Equal("DELIVERED", OrderPublicEventCodePolicy.Map(OrderStatus.Delivered));
        Assert.Equal("RETURNED", OrderPublicEventCodePolicy.Map(OrderStatus.Returned));
        Assert.Equal(["DELIVERED", "RETURNED"], SettlementSourcePolicy.OutcomeEventCodes);
    }

    [Fact]
    public void Sources_are_named_by_their_assignment_or_audit_entry()
    {
        var assignment = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var audit = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        Assert.Equal(
            "dispatch.assignments/0f8fad5b-d9cb-469f-a165-70867728950e",
            SettlementSourcePolicy.AssignmentSource(assignment));
        Assert.Equal(
            "platform.audit_logs/7c9e6679-7425-40de-944b-e07fc1f90ae7",
            SettlementSourcePolicy.AdjustmentSource(audit));
        Assert.Equal(["OWN", "EXTERNAL"], SettlementSourcePolicy.DriverPayableAssignmentTypes);
        Assert.Equal(["OPEN", "INVESTIGATING"], SettlementSourcePolicy.PendingIncidentStatuses);
    }

    [Fact]
    public void Nothing_pending_approves()
    {
        Assert.Equal(SettlementApprovalBlocker.None, SettlementApprovalPolicy.Evaluate([]));
        Assert.Equal(
            SettlementApprovalBlocker.None,
            SettlementApprovalPolicy.Evaluate(
            [
                Delivery("CLOSED", 5_000, CodStatus.Reconciled, 5_000),
                Delivery("DELIVERED"),
                Delivery("CLAIM_RESOLVED"),
                Return(5_000, null),
            ]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(CodStatus.Recorded, 5_000L)]
    [InlineData(CodStatus.Disputed, 5_000L)]
    [InlineData(CodStatus.Reversed, 5_000L)]
    [InlineData(CodStatus.Reconciled, 4_999L)]
    [InlineData(CodStatus.Reconciled, 5_001L)]
    public void A_delivery_whose_cash_is_not_reconciled_is_cash_pending(CodStatus? status, long? amount) =>
        Assert.Equal(
            SettlementApprovalBlocker.CashPending,
            SettlementApprovalPolicy.Evaluate([Delivery("DELIVERED", 5_000, status, amount)]));

    [Theory]
    [InlineData(null, null)]
    [InlineData(CodStatus.Recorded, 5_000L)]
    public void A_return_is_never_blocked_by_its_order_COD_expectation(CodStatus? status, long? amount)
    {
        var returned = Return(5_000, status, amount);
        Assert.False(SettlementApprovalPolicy.IsCashPending(returned));
        Assert.Equal(SettlementApprovalBlocker.None, SettlementApprovalPolicy.Evaluate([returned]));
    }

    [Fact]
    public void Pending_incidents_and_open_claims_block_in_a_stable_order()
    {
        var incident = Delivery("DELIVERED") with { HasPendingIncident = true };
        var claim = Delivery("CLAIM_OPEN");
        var cash = Delivery("DELIVERED", 5_000, CodStatus.Recorded, 5_000);
        var returnIncident = Return(0, null) with { HasPendingIncident = true };

        Assert.Equal(SettlementApprovalBlocker.IncidentPending, SettlementApprovalPolicy.Evaluate([incident]));
        Assert.Equal(SettlementApprovalBlocker.IncidentPending, SettlementApprovalPolicy.Evaluate([returnIncident]));
        Assert.Equal(SettlementApprovalBlocker.ClaimPending, SettlementApprovalPolicy.Evaluate([claim]));

        // Cash, then incident, then claim, whatever the line order.
        Assert.Equal(SettlementApprovalBlocker.CashPending, SettlementApprovalPolicy.Evaluate([claim, incident, cash]));
        Assert.Equal(SettlementApprovalBlocker.CashPending, SettlementApprovalPolicy.Evaluate([cash, claim, incident]));
        Assert.Equal(SettlementApprovalBlocker.IncidentPending, SettlementApprovalPolicy.Evaluate([claim, incident]));
        Assert.Equal(SettlementApprovalBlocker.IncidentPending, SettlementApprovalPolicy.Evaluate([incident, claim]));
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void An_adjustment_is_any_non_zero_signed_amount_with_a_bounded_reason(long amount)
    {
        Assert.True(SettlementInputPolicy.IsValid(Adjustment(amount, "Bono por puntualidad")));
        Assert.False(SettlementInputPolicy.IsValid(Adjustment(amount, null)));
    }

    [Fact]
    public void A_zero_adjustment_records_nothing_and_is_refused() =>
        Assert.False(SettlementInputPolicy.IsValid(Adjustment(0, "Sin efecto")));

    [Theory]
    [InlineData("")]
    [InlineData(" Descuento")]
    [InlineData("Descuento ")]
    [InlineData("Descuento\npor daño")]
    [InlineData("Descuento\tpor daño")]
    [InlineData("Descuento\u0085por daño")]
    public void A_reason_with_surrounding_whitespace_or_control_characters_is_refused(string reason)
    {
        Assert.False(SettlementReasonPolicy.IsValid(reason));
        Assert.False(SettlementInputPolicy.IsValid(Adjustment(100, reason)));
        Assert.False(SettlementInputPolicy.IsValid(Void(reason)));
    }

    [Fact]
    public void A_reason_is_bounded_to_500_characters()
    {
        Assert.True(SettlementReasonPolicy.IsValid(new string('a', SettlementReasonPolicy.MaximumLength)));
        Assert.False(SettlementReasonPolicy.IsValid(new string('a', SettlementReasonPolicy.MaximumLength + 1)));
        Assert.True(SettlementReasonPolicy.IsValid("Descuento por daño en paquete"));
    }

    [Fact]
    public void Adjustments_apply_with_exact_checked_bigint_arithmetic()
    {
        Assert.Equal(15_250L, SettlementLedger.TryApply(14_500, 750));
        Assert.Equal(-500L, SettlementLedger.TryApply(0, -500));
        Assert.Equal(long.MaxValue, SettlementLedger.TryApply(long.MaxValue - 1, 1));
        Assert.Null(SettlementLedger.TryApply(long.MaxValue, 1));
        Assert.Null(SettlementLedger.TryApply(long.MinValue, -1));
    }

    [Fact]
    public void The_ledger_reconciles_only_on_the_exact_line_sum()
    {
        Assert.True(SettlementLedger.Reconciles(0, []));
        Assert.True(SettlementLedger.Reconciles(14_500, [12_000, 3_000, 0, -500]));
        Assert.False(SettlementLedger.Reconciles(14_499, [12_000, 3_000, 0, -500]));

        // A sum that would wrap around a bigint never reconciles with the wrapped value.
        Assert.False(SettlementLedger.Reconciles(long.MinValue, [long.MaxValue, 1]));
        Assert.Null(SettlementLedger.TryTotal([long.MaxValue, 1]));
        Assert.Equal(long.MaxValue, SettlementLedger.TryTotal([long.MaxValue - 1, 1]));
    }

    [Fact]
    public void Transitions_approvals_and_payments_need_only_their_settlement_and_key()
    {
        var actor = Guid.NewGuid();
        var organization = Guid.NewGuid();
        Assert.True(SettlementInputPolicy.IsValid(new SettlementTransitionCommand(
            actor, organization, "settlement-transition-key", Guid.NewGuid(), false, null)));
        Assert.False(SettlementInputPolicy.IsValid(new SettlementTransitionCommand(
            actor, organization, "short", Guid.NewGuid(), false, null)));
        Assert.False(SettlementInputPolicy.IsValid(new SettlementTransitionCommand(
            actor, organization, "settlement-transition-key", Guid.Empty, false, null)));
        Assert.False(SettlementInputPolicy.IsValid(new CreateSettlementCommand(
            actor, organization, "settlement-create-key-0001", Guid.NewGuid(),
            new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 14), false, null)));
        Assert.False(SettlementInputPolicy.IsValid(new CreateSettlementCommand(
            actor, organization, "settlement-create-key-0001", Guid.Empty,
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 20), false, null)));
    }

    [Fact]
    public void Request_hashes_bind_every_outcome_changing_field()
    {
        var organization = Guid.NewGuid();
        var driver = Guid.NewGuid();
        var create = new CreateSettlementCommand(
            Guid.NewGuid(), organization, "settlement-create-key-0001", driver,
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 20), false, "request-a");

        // The actor, the key and the request id do not change the outcome, so they do not change the hash.
        Assert.Equal(
            SettlementCanonicalizer.Create(create),
            SettlementCanonicalizer.Create(create with { ActorId = Guid.NewGuid(), RequestId = "request-b" }));
        Assert.NotEqual(
            SettlementCanonicalizer.Create(create),
            SettlementCanonicalizer.Create(create with { PeriodTo = new DateOnly(2026, 9, 21) }));
        Assert.NotEqual(
            SettlementCanonicalizer.Create(create),
            SettlementCanonicalizer.Create(create with { OrganizationId = Guid.NewGuid() }));

        var adjustment = Adjustment(750, "Bono por puntualidad");
        Assert.NotEqual(
            SettlementCanonicalizer.Adjust(adjustment),
            SettlementCanonicalizer.Adjust(adjustment with { AmountCents = -750 }));
        Assert.NotEqual(
            SettlementCanonicalizer.Adjust(adjustment),
            SettlementCanonicalizer.Adjust(adjustment with { Reason = "Bono por distancia" }));
        Assert.NotEqual(SettlementCanonicalizer.Void(Void("Motivo A")), SettlementCanonicalizer.Void(Void("Motivo B")));
    }

    private static SettlementSourceState Delivery(
        string orderStatus,
        long codExpected = 0,
        CodStatus? codStatus = null,
        long? codAmount = null) =>
        new(
            SettlementLineType.Delivery,
            orderStatus,
            new MoneyCents(codExpected),
            codStatus,
            codAmount is { } amount ? new MoneyCents(amount) : null,
            false);

    private static SettlementSourceState Return(long codExpected, CodStatus? codStatus, long? codAmount = null) =>
        new(
            SettlementLineType.Return,
            "RETURNED",
            new MoneyCents(codExpected),
            codStatus,
            codAmount is { } amount ? new MoneyCents(amount) : null,
            false);

    private static readonly Guid SettlementId = Guid.Parse("5b1c0e7a-4e0f-4b4a-9a53-2f3f7a1c9d10");
    private static readonly Guid ActorId = Guid.Parse("a3bb189e-8bf9-4888-9912-ace4e6543002");
    private static readonly Guid OrganizationId = Guid.Parse("c56a4180-65aa-42ec-a945-5fd21dec0538");

    private static AddSettlementAdjustmentCommand Adjustment(long amount, string? reason) => new(
        ActorId, OrganizationId, "settlement-adjust-key-0001", SettlementId, amount, reason, false, null);

    private static VoidSettlementCommand Void(string? reason) => new(
        ActorId, OrganizationId, "settlement-void-key-00001", SettlementId, reason, false, null);
}
