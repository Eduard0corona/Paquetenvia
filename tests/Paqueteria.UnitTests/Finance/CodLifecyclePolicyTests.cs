using Finance.Application;
using Finance.Application.Cod;
using Finance.Domain;
using Orders.Application.Orders;
using Orders.Domain;

namespace Paqueteria.UnitTests.Finance;

/// <summary>
/// FIN-001 COD lifecycle fixtures. The last two facts pin the finance policy to the order transition
/// guards it exists to satisfy, so the two cannot drift apart.
/// </summary>
public sealed class CodLifecyclePolicyTests
{
    [Theory]
    [InlineData("PICKED_UP", true)]
    [InlineData("IN_TRANSIT", true)]
    [InlineData("DELIVERING", true)]
    [InlineData("FAILED_ATTEMPT", true)]
    [InlineData("RESCHEDULED", true)]
    [InlineData("ASSIGNED", false)]
    [InlineData("AT_PICKUP", false)]
    [InlineData("DELIVERED", false)]
    [InlineData("CLOSED", false)]
    [InlineData("CANCELLED", false)]
    public void Fin001_collections_are_recorded_only_before_delivery(string orderStatus, bool expected) =>
        Assert.Equal(
            expected,
            CodLifecyclePolicy.CanRecord(orderStatus, new(5_000), new(5_000), false));

    [Fact]
    public void Fin001_recording_requires_an_expectation_and_an_exact_amount()
    {
        Assert.False(CodLifecyclePolicy.CanRecord("DELIVERING", MoneyCents.Zero, new(5_000), false));
        Assert.False(CodLifecyclePolicy.CanRecord("DELIVERING", new(5_000), new(4_999), false));
        Assert.False(CodLifecyclePolicy.CanRecord("DELIVERING", new(5_000), new(5_001), false));
        Assert.False(CodLifecyclePolicy.CanRecord("DELIVERING", new(5_000), new(5_000), true));
        Assert.True(CodLifecyclePolicy.CanRecord("DELIVERING", new(5_000), new(5_000), false));
    }

    [Theory]
    [InlineData("DELIVERED", CodStatus.Recorded, true)]
    [InlineData("DELIVERING", CodStatus.Recorded, true)]
    [InlineData("CLAIM_OPEN", CodStatus.Recorded, true)]
    [InlineData("CLOSED", CodStatus.Recorded, false)]
    [InlineData("CANCELLED", CodStatus.Recorded, false)]
    [InlineData("DELIVERED", CodStatus.Reconciled, false)]
    [InlineData("DELIVERED", CodStatus.Expected, false)]
    [InlineData("DELIVERED", CodStatus.Reversed, false)]
    public void Fin001_reconciliation_runs_once_and_before_close(
        string orderStatus,
        CodStatus current,
        bool expected) =>
        Assert.Equal(expected, CodLifecyclePolicy.CanReconcile(orderStatus, current));

    [Fact]
    public void Fin001_delivery_requires_recorded_or_reconciled_cash_for_the_exact_amount()
    {
        Assert.True(CodLifecyclePolicy.SatisfiesDeliveryRequirement(MoneyCents.Zero, null, null));
        Assert.False(CodLifecyclePolicy.SatisfiesDeliveryRequirement(new(5_000), null, null));
        Assert.False(CodLifecyclePolicy.SatisfiesDeliveryRequirement(
            new(5_000), CodStatus.Expected, new(5_000)));
        Assert.True(CodLifecyclePolicy.SatisfiesDeliveryRequirement(
            new(5_000), CodStatus.Recorded, new(5_000)));
        Assert.True(CodLifecyclePolicy.SatisfiesDeliveryRequirement(
            new(5_000), CodStatus.Reconciled, new(5_000)));
        Assert.False(CodLifecyclePolicy.SatisfiesDeliveryRequirement(
            new(5_000), CodStatus.Recorded, new(4_000)));
    }

    [Fact]
    public void Fin001_close_requires_reconciled_cash_or_no_record_at_all()
    {
        Assert.True(CodLifecyclePolicy.SatisfiesCloseRequirement(MoneyCents.Zero, null, null));
        Assert.False(CodLifecyclePolicy.SatisfiesCloseRequirement(
            MoneyCents.Zero, CodStatus.Recorded, new(1)));
        Assert.False(CodLifecyclePolicy.SatisfiesCloseRequirement(
            new(5_000), CodStatus.Recorded, new(5_000)));
        Assert.True(CodLifecyclePolicy.SatisfiesCloseRequirement(
            new(5_000), CodStatus.Reconciled, new(5_000)));
        Assert.False(CodLifecyclePolicy.SatisfiesCloseRequirement(
            new(5_000), CodStatus.Reconciled, new(4_000)));
    }

    [Theory]
    [InlineData(0L, null, null, true)]
    [InlineData(5_000L, null, null, false)]
    [InlineData(5_000L, "RECORDED", 5_000L, true)]
    [InlineData(5_000L, "RECONCILED", 5_000L, true)]
    [InlineData(5_000L, "RECORDED", 4_000L, false)]
    [InlineData(5_000L, "EXPECTED", 5_000L, false)]
    public void Fin001_delivery_policy_agrees_with_the_order_transition_guard(
        long expectedCents,
        string? codStatus,
        long? amountCents,
        bool satisfied)
    {
        Assert.Equal(satisfied, EvaluateGuard(
            OrderStatus.Delivering, OrderStatus.Delivered, expectedCents, codStatus, amountCents));
        Assert.Equal(satisfied, CodLifecyclePolicy.SatisfiesDeliveryRequirement(
            new(expectedCents),
            ParseStatus(codStatus),
            amountCents is { } amount ? new MoneyCents(amount) : null));
    }

    [Theory]
    [InlineData(0L, null, null, true)]
    [InlineData(5_000L, null, null, false)]
    [InlineData(5_000L, "RECORDED", 5_000L, false)]
    [InlineData(5_000L, "RECONCILED", 5_000L, true)]
    [InlineData(5_000L, "RECONCILED", 4_000L, false)]
    public void Fin001_close_policy_agrees_with_the_order_transition_guard(
        long expectedCents,
        string? codStatus,
        long? amountCents,
        bool satisfied)
    {
        Assert.Equal(satisfied, EvaluateGuard(
            OrderStatus.Delivered, OrderStatus.Closed, expectedCents, codStatus, amountCents));
        Assert.Equal(satisfied, CodLifecyclePolicy.SatisfiesCloseRequirement(
            new(expectedCents),
            ParseStatus(codStatus),
            amountCents is { } amount ? new MoneyCents(amount) : null));
    }

    [Theory]
    [InlineData("DISPATCHER", false, true, true, true)]
    [InlineData("PLATFORM_ADMIN", true, true, true, true)]
    [InlineData("PLATFORM_ADMIN", false, false, false, false)]
    [InlineData("DRIVER", false, true, false, false)]
    [InlineData("VIEWER", true, false, false, false)]
    public void Fin001_capabilities_are_fail_closed_and_segregate_duties(
        string role,
        bool mfa,
        bool canRecord,
        bool canReconcile,
        bool canRead)
    {
        var context = new FinanceAuthorizationContext(role, true, true, mfa, true);

        Assert.Equal(canRecord, FinanceAuthorizationPolicy.CanRecordCod(context));
        Assert.Equal(canReconcile, FinanceAuthorizationPolicy.CanReconcileCod(context));
        Assert.Equal(canRead, FinanceAuthorizationPolicy.CanReadFinancials(context));
    }

    [Fact]
    public void Fin001_a_driver_may_only_record_against_their_own_active_assignment()
    {
        Assert.True(FinanceAuthorizationPolicy.CanRecordCod(new("DRIVER", true, true, false, true)));
        Assert.False(FinanceAuthorizationPolicy.CanRecordCod(new("DRIVER", true, true, false, false)));
        Assert.False(FinanceAuthorizationPolicy.CanRecordCod(new("DRIVER", false, true, false, true)));
        Assert.False(FinanceAuthorizationPolicy.CanRecordCod(new("DRIVER", true, false, false, true)));
    }

    [Fact]
    public void Fin001_record_and_reconcile_hashes_are_tenant_and_payload_bound()
    {
        var actor = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var order = Guid.NewGuid();
        var command = new RecordCodCollectionCommand(
            actor, organization, "fin001-contract-key", order, 5_000, "cash-01", false, null);

        Assert.Equal(CodCanonicalizer.Record(command), CodCanonicalizer.Record(command with { }));
        Assert.NotEqual(
            CodCanonicalizer.Record(command),
            CodCanonicalizer.Record(command with { AmountCents = 5_001 }));
        Assert.NotEqual(
            CodCanonicalizer.Record(command),
            CodCanonicalizer.Record(command with { Reference = "cash-02" }));
        Assert.NotEqual(
            CodCanonicalizer.Record(command),
            CodCanonicalizer.Record(command with { OrganizationId = Guid.NewGuid() }));

        var reconcile = new ReconcileCodCommand(actor, organization, "fin001-contract-key", order, true, null);
        Assert.Equal(CodCanonicalizer.Reconcile(reconcile), CodCanonicalizer.Reconcile(reconcile with { }));
        Assert.NotEqual(
            CodCanonicalizer.Reconcile(reconcile),
            CodCanonicalizer.Reconcile(reconcile with { CodTransactionId = Guid.NewGuid() }));
    }

    [Fact]
    public void Fin001_record_requests_require_an_idempotency_grade_reference_and_positive_amount()
    {
        var valid = new RecordCodCollectionCommand(
            Guid.NewGuid(), Guid.NewGuid(), "fin001-contract-key", Guid.NewGuid(), 5_000, "cash-01", false, null);

        Assert.True(CodInputPolicy.IsValid(valid));
        Assert.False(CodInputPolicy.IsValid(valid with { AmountCents = 0 }));
        Assert.False(CodInputPolicy.IsValid(valid with { AmountCents = -1 }));
        Assert.False(CodInputPolicy.IsValid(valid with { Reference = "  " }));
        Assert.False(CodInputPolicy.IsValid(valid with { Reference = " padded" }));
        Assert.False(CodInputPolicy.IsValid(valid with
        {
            Reference = new string('x', CodInputPolicy.MaximumReferenceLength + 1),
        }));
        Assert.False(CodInputPolicy.IsValid(valid with { OrderId = Guid.Empty }));
    }

    private static CodStatus? ParseStatus(string? value) =>
        FinanceContractValues.TryParseCodStatus(value, out var status) ? status : null;

    private static bool EvaluateGuard(
        OrderStatus source,
        OrderStatus target,
        long codExpectedCents,
        string? codStatus,
        long? amountCents) =>
        new OrderTransitionGuardRegistry().Evaluate(new()
        {
            Source = source,
            Target = target,
            Reason = "fin-001 fixture",
            OccurredAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            ClaimWindowEndsAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            FinalizedAt = null,
            CodExpectedCents = codExpectedCents,
            MonetaryIntegrityValid = true,
            Metadata = NormalizedTransitionMetadata.Empty,
            Proofs = new(true, true),
            Cod = new(codStatus is not null, codStatus, amountCents),
        }).Satisfied;
}
