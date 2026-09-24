namespace Finance.Domain;

/// <summary>Cost incurred through one delivery modality, with the number of cost-bearing assignments behind it.</summary>
public sealed record ModalityCost(DeliveryModality Modality, MoneyCents Cost, int AssignmentCount)
{
    public static ModalityCost None(DeliveryModality modality) => new(modality, MoneyCents.Zero, 0);
}

/// <summary>Cash-on-delivery position of a single order, as known to finance.</summary>
public sealed record CodPosition(MoneyCents Expected, CodStatus? Status, MoneyCents? Amount)
{
    public static CodPosition NotExpected { get; } = new(MoneyCents.Zero, null, null);

    public bool IsExpected => CodLifecyclePolicy.IsExpected(Expected);

    public bool IsRecorded => Status is CodStatus.Recorded or CodStatus.Reconciled;

    public bool IsReconciled => Status == CodStatus.Reconciled;

    /// <summary>
    /// Whether collected cash is awaiting reconciliation. Only a RECORDED collection is: an expectation with
    /// no collection yet is uncollected cash, reported through <see cref="Expected"/> and never here.
    /// </summary>
    public bool IsPendingReconciliation => Status == CodStatus.Recorded;

    /// <summary>Cash collected but not yet reconciled: the amount actually recorded, never the expectation.</summary>
    public MoneyCents PendingReconciliation =>
        IsPendingReconciliation && Amount is { } recorded ? recorded : MoneyCents.Zero;

    public bool SatisfiesDeliveryRequirement =>
        CodLifecyclePolicy.SatisfiesDeliveryRequirement(Expected, Status, Amount);

    public bool SatisfiesCloseRequirement =>
        CodLifecyclePolicy.SatisfiesCloseRequirement(Expected, Status, Amount);
}

/// <summary>Unit economics of one order: revenue, cost by modality, and the resulting margin.</summary>
public sealed record OrderUnitEconomics(
    Guid OrderId,
    MoneyCents Revenue,
    MoneyCents Cost,
    MoneyCents Margin,
    long? MarginBasisPoints,
    IReadOnlyList<ModalityCost> CostByModality,
    CodPosition Cod)
{
    /// <summary>
    /// The FIN-001 unit economics calculator. Revenue is the order's own priced total, cost is the sum of
    /// the cost-bearing assignments grouped by modality, and margin is their difference; all in checked
    /// integer cents. Modalities absent from <paramref name="costs"/> are reported as zero so the
    /// breakdown has a stable shape.
    /// </summary>
    public static OrderUnitEconomics Calculate(
        Guid orderId,
        MoneyCents revenue,
        IReadOnlyList<ModalityCost> costs,
        CodPosition cod)
    {
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(cod);
        if (costs.Select(cost => cost.Modality).Distinct().Count() != costs.Count)
        {
            throw new ArgumentException("A modality may contribute at most one cost bucket.", nameof(costs));
        }

        var byModality = FinanceContractValues.AllModalities
            .Select(modality =>
                costs.FirstOrDefault(cost => cost.Modality == modality) ?? ModalityCost.None(modality))
            .ToArray();
        var total = byModality.Aggregate(MoneyCents.Zero, (sum, cost) => sum + cost.Cost);
        var margin = revenue - total;
        return new(orderId, revenue, total, margin, margin.BasisPointsOf(revenue), byModality, cod);
    }
}

/// <summary>Financial totals of one route, aggregated from the unit economics of the orders it serves.</summary>
public sealed record RouteUnitEconomics(
    MoneyCents Revenue,
    MoneyCents Cost,
    MoneyCents Margin,
    long? MarginBasisPoints,
    IReadOnlyList<ModalityCost> CostByModality,
    MoneyCents CodExpected,
    MoneyCents CodPendingReconciliation,
    int OrderCount,
    int CodPendingReconciliationCount,
    IReadOnlyList<OrderUnitEconomics> Orders)
{
    /// <summary>
    /// Aggregates per-order unit economics into route-level totals. Every total is derived from the same
    /// per-order values the order endpoint returns, so route figures always reconcile against their orders.
    /// </summary>
    public static RouteUnitEconomics Aggregate(IReadOnlyList<OrderUnitEconomics> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (orders.Select(order => order.OrderId).Distinct().Count() != orders.Count)
        {
            throw new ArgumentException("An order may appear at most once in a route total.", nameof(orders));
        }

        var revenue = orders.Aggregate(MoneyCents.Zero, (sum, order) => sum + order.Revenue);
        var cost = orders.Aggregate(MoneyCents.Zero, (sum, order) => sum + order.Cost);
        var margin = revenue - cost;
        var byModality = FinanceContractValues.AllModalities
            .Select(modality =>
            {
                var buckets = orders
                    .SelectMany(order => order.CostByModality)
                    .Where(bucket => bucket.Modality == modality)
                    .ToArray();
                return new ModalityCost(
                    modality,
                    buckets.Aggregate(MoneyCents.Zero, (sum, bucket) => sum + bucket.Cost),
                    buckets.Sum(bucket => bucket.AssignmentCount));
            })
            .ToArray();
        var codExpected = orders.Aggregate(MoneyCents.Zero, (sum, order) => sum + order.Cod.Expected);
        var codPending = orders.Aggregate(
            MoneyCents.Zero,
            (sum, order) => sum + order.Cod.PendingReconciliation);
        return new(
            revenue,
            cost,
            margin,
            margin.BasisPointsOf(revenue),
            byModality,
            codExpected,
            codPending,
            orders.Count,
            orders.Count(order => order.Cod.IsPendingReconciliation),
            orders);
    }
}
