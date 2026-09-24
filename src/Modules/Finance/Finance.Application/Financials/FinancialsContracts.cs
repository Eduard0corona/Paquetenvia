using Finance.Domain;

namespace Finance.Application.Financials;

public sealed record GetOrderFinancialsQuery(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    bool MfaSatisfied);

public sealed record GetRouteFinancialsQuery(
    Guid ActorId,
    Guid OrganizationId,
    Guid RouteId,
    bool MfaSatisfied);

public sealed record ModalityCostResult(string Modality, long CostCents, int AssignmentCount)
{
    public static ModalityCostResult From(ModalityCost cost) => new(
        cost.Modality.ToContractValue(),
        cost.Cost.AmountCents,
        cost.AssignmentCount);
}

public sealed record CodPositionResult(
    long ExpectedCents,
    string? Status,
    long? AmountCents,
    bool Recorded,
    bool Reconciled,
    bool SatisfiesDeliveryRequirement,
    bool SatisfiesCloseRequirement)
{
    public static CodPositionResult From(CodPosition cod) => new(
        cod.Expected.AmountCents,
        cod.Status?.ToContractValue(),
        cod.Amount?.AmountCents,
        cod.IsRecorded,
        cod.IsReconciled,
        cod.SatisfiesDeliveryRequirement,
        cod.SatisfiesCloseRequirement);
}

public sealed record OrderFinancialsResult(
    Guid OrderId,
    string OrderStatus,
    string Currency,
    long RevenueCents,
    long CostCents,
    long MarginCents,
    long? MarginBasisPoints,
    IReadOnlyList<ModalityCostResult> CostByModality,
    CodPositionResult Cod)
{
    public IReadOnlyList<ModalityCostResult> CostByModality { get; } = CostByModality.ToArray();

    public static OrderFinancialsResult From(string orderStatus, OrderUnitEconomics economics) => new(
        economics.OrderId,
        orderStatus,
        MoneyCents.Currency,
        economics.Revenue.AmountCents,
        economics.Cost.AmountCents,
        economics.Margin.AmountCents,
        economics.MarginBasisPoints,
        economics.CostByModality.Select(ModalityCostResult.From).ToArray(),
        CodPositionResult.From(economics.Cod));
}

public sealed record RouteOrderFinancialsResult(
    Guid OrderId,
    long RevenueCents,
    long CostCents,
    long MarginCents,
    long? MarginBasisPoints,
    IReadOnlyList<ModalityCostResult> CostByModality,
    CodPositionResult Cod)
{
    public IReadOnlyList<ModalityCostResult> CostByModality { get; } = CostByModality.ToArray();

    public static RouteOrderFinancialsResult From(OrderUnitEconomics economics) => new(
        economics.OrderId,
        economics.Revenue.AmountCents,
        economics.Cost.AmountCents,
        economics.Margin.AmountCents,
        economics.MarginBasisPoints,
        economics.CostByModality.Select(ModalityCostResult.From).ToArray(),
        CodPositionResult.From(economics.Cod));
}

public sealed record RouteFinancialsResult(
    Guid RouteId,
    string RouteStatus,
    string Currency,
    int OrderCount,
    long RevenueCentsTotal,
    long CostCentsTotal,
    long MarginCentsTotal,
    long? MarginBasisPoints,
    IReadOnlyList<ModalityCostResult> CostByModality,
    long CodExpectedCentsTotal,
    long CodPendingReconciliationCentsTotal,
    int CodPendingReconciliationCount,
    IReadOnlyList<RouteOrderFinancialsResult> Orders)
{
    public IReadOnlyList<ModalityCostResult> CostByModality { get; } = CostByModality.ToArray();

    public IReadOnlyList<RouteOrderFinancialsResult> Orders { get; } = Orders.ToArray();

    public static RouteFinancialsResult From(
        Guid routeId,
        string routeStatus,
        RouteUnitEconomics economics) => new(
        routeId,
        routeStatus,
        MoneyCents.Currency,
        economics.OrderCount,
        economics.Revenue.AmountCents,
        economics.Cost.AmountCents,
        economics.Margin.AmountCents,
        economics.MarginBasisPoints,
        economics.CostByModality.Select(ModalityCostResult.From).ToArray(),
        economics.CodExpected.AmountCents,
        economics.CodPendingReconciliation.AmountCents,
        economics.CodPendingReconciliationCount,
        economics.Orders.Select(RouteOrderFinancialsResult.From).ToArray());
}

public interface IOrderFinancialsService
{
    Task<OrderFinancialsResult> GetOrderFinancialsAsync(
        GetOrderFinancialsQuery query,
        CancellationToken cancellationToken);

    Task<RouteFinancialsResult> GetRouteFinancialsAsync(
        GetRouteFinancialsQuery query,
        CancellationToken cancellationToken);
}

public static class FinancialsInputPolicy
{
    public static bool IsValid(GetOrderFinancialsQuery value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty && value.OrderId != Guid.Empty;

    public static bool IsValid(GetRouteFinancialsQuery value) =>
        value.ActorId != Guid.Empty && value.OrganizationId != Guid.Empty && value.RouteId != Guid.Empty;
}
