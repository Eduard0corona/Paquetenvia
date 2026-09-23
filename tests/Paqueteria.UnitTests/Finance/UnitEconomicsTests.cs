using Finance.Domain;

namespace Paqueteria.UnitTests.Finance;

/// <summary>FIN-001 financial fixtures for the unit economics calculator.</summary>
public sealed class UnitEconomicsTests
{
    private static readonly Guid OrderA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OrderC = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Fin001_own_modality_margin_is_revenue_minus_assignment_cost()
    {
        var economics = OrderUnitEconomics.Calculate(
            OrderA,
            new(12_000),
            [new(DeliveryModality.Own, new(4_500), 1)],
            CodPosition.NotExpected);

        Assert.Equal(12_000, economics.Revenue.AmountCents);
        Assert.Equal(4_500, economics.Cost.AmountCents);
        Assert.Equal(7_500, economics.Margin.AmountCents);
        Assert.Equal(6_250, economics.MarginBasisPoints);
        Assert.Equal(4_500, Bucket(economics, DeliveryModality.Own).Cost.AmountCents);
        Assert.Equal(0, Bucket(economics, DeliveryModality.External).Cost.AmountCents);
        Assert.Equal(0, Bucket(economics, DeliveryModality.AllyCapacity).Cost.AmountCents);
    }

    [Fact]
    public void Fin001_external_modality_cost_is_the_accepted_offer_commission()
    {
        var economics = OrderUnitEconomics.Calculate(
            OrderA,
            new(20_000),
            [new(DeliveryModality.External, new(8_000), 1)],
            CodPosition.NotExpected);

        Assert.Equal(8_000, economics.Cost.AmountCents);
        Assert.Equal(12_000, economics.Margin.AmountCents);
        Assert.Equal(8_000, Bucket(economics, DeliveryModality.External).Cost.AmountCents);
        Assert.Equal(1, Bucket(economics, DeliveryModality.External).AssignmentCount);
        Assert.Equal(0, Bucket(economics, DeliveryModality.Own).AssignmentCount);
    }

    [Fact]
    public void Fin001_mixed_modalities_report_every_bucket_and_one_total()
    {
        var economics = OrderUnitEconomics.Calculate(
            OrderA,
            new(30_000),
            [
                new(DeliveryModality.Own, new(5_000), 1),
                new(DeliveryModality.External, new(7_000), 2),
                new(DeliveryModality.AllyCapacity, new(3_000), 1),
            ],
            CodPosition.NotExpected);

        Assert.Equal(15_000, economics.Cost.AmountCents);
        Assert.Equal(15_000, economics.Margin.AmountCents);
        Assert.Equal(5_000, economics.MarginBasisPoints);
        Assert.Equal(
            ["OWN", "EXTERNAL", "ALLY_CAPACITY"],
            economics.CostByModality.Select(bucket => bucket.Modality.ToContractValue()));
    }

    [Fact]
    public void Fin001_margin_is_negative_when_cost_exceeds_revenue()
    {
        var economics = OrderUnitEconomics.Calculate(
            OrderA,
            new(4_000),
            [new(DeliveryModality.External, new(9_000), 1)],
            CodPosition.NotExpected);

        Assert.Equal(-5_000, economics.Margin.AmountCents);
        Assert.True(economics.Margin.IsNegative);
        Assert.Equal(-12_500, economics.MarginBasisPoints);
    }

    [Fact]
    public void Fin001_zero_revenue_has_no_defined_margin_ratio()
    {
        var economics = OrderUnitEconomics.Calculate(
            OrderA,
            MoneyCents.Zero,
            [new(DeliveryModality.Own, new(1_200), 1)],
            CodPosition.NotExpected);

        Assert.Equal(-1_200, economics.Margin.AmountCents);
        Assert.Null(economics.MarginBasisPoints);
    }

    [Fact]
    public void Fin001_a_modality_may_contribute_at_most_one_bucket()
    {
        Assert.Throws<ArgumentException>(() => OrderUnitEconomics.Calculate(
            OrderA,
            new(10_000),
            [new(DeliveryModality.Own, new(1_000), 1), new(DeliveryModality.Own, new(2_000), 1)],
            CodPosition.NotExpected));
    }

    [Fact]
    public void Fin001_route_totals_reconcile_against_their_orders()
    {
        var route = RouteUnitEconomics.Aggregate(
        [
            OrderUnitEconomics.Calculate(
                OrderA, new(12_000), [new(DeliveryModality.Own, new(4_500), 1)], CodPosition.NotExpected),
            OrderUnitEconomics.Calculate(
                OrderB, new(20_000), [new(DeliveryModality.External, new(8_000), 1)],
                new(new(5_000), CodStatus.Recorded, new(5_000))),
            OrderUnitEconomics.Calculate(
                OrderC, new(9_000), [new(DeliveryModality.Own, new(3_000), 1)],
                new(new(2_500), CodStatus.Reconciled, new(2_500))),
        ]);

        Assert.Equal(3, route.OrderCount);
        Assert.Equal(41_000, route.Revenue.AmountCents);
        Assert.Equal(15_500, route.Cost.AmountCents);
        Assert.Equal(25_500, route.Margin.AmountCents);
        Assert.Equal(6_219, route.MarginBasisPoints);
        Assert.Equal(7_500, BucketOf(route, DeliveryModality.Own).Cost.AmountCents);
        Assert.Equal(2, BucketOf(route, DeliveryModality.Own).AssignmentCount);
        Assert.Equal(8_000, BucketOf(route, DeliveryModality.External).Cost.AmountCents);
        Assert.Equal(0, BucketOf(route, DeliveryModality.AllyCapacity).AssignmentCount);
        Assert.Equal(
            route.Revenue.AmountCents,
            route.Orders.Sum(order => order.Revenue.AmountCents));
        Assert.Equal(
            route.Cost.AmountCents,
            route.Orders.Sum(order => order.Cost.AmountCents));
    }

    [Fact]
    public void Fin001_route_cod_totals_separate_reconciled_cash_from_outstanding_cash()
    {
        var route = RouteUnitEconomics.Aggregate(
        [
            OrderUnitEconomics.Calculate(OrderA, new(10_000), [], CodPosition.NotExpected),
            OrderUnitEconomics.Calculate(
                OrderB, new(10_000), [], new(new(5_000), CodStatus.Recorded, new(5_000))),
            OrderUnitEconomics.Calculate(
                OrderC, new(10_000), [], new(new(2_500), CodStatus.Reconciled, new(2_500))),
        ]);

        Assert.Equal(7_500, route.CodExpected.AmountCents);
        Assert.Equal(5_000, route.CodPendingReconciliation.AmountCents);
        Assert.Equal(1, route.CodPendingReconciliationCount);
    }

    [Fact]
    public void Fin001_an_empty_route_totals_to_zero_without_a_margin_ratio()
    {
        var route = RouteUnitEconomics.Aggregate([]);

        Assert.Equal(0, route.OrderCount);
        Assert.Equal(0, route.Revenue.AmountCents);
        Assert.Equal(0, route.Cost.AmountCents);
        Assert.Null(route.MarginBasisPoints);
        Assert.Equal(3, route.CostByModality.Count);
    }

    [Fact]
    public void Fin001_an_order_may_appear_at_most_once_in_a_route_total()
    {
        var order = OrderUnitEconomics.Calculate(OrderA, new(10_000), [], CodPosition.NotExpected);

        Assert.Throws<ArgumentException>(() => RouteUnitEconomics.Aggregate([order, order]));
    }

    private static ModalityCost Bucket(OrderUnitEconomics economics, DeliveryModality modality) =>
        economics.CostByModality.Single(bucket => bucket.Modality == modality);

    private static ModalityCost BucketOf(RouteUnitEconomics economics, DeliveryModality modality) =>
        economics.CostByModality.Single(bucket => bucket.Modality == modality);
}
