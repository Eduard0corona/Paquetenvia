using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Financials;

namespace Finance.Infrastructure;

internal sealed class DisabledCodTransactionService : ICodTransactionService
{
    public Task<CodTransactionResult> RecordAsync(
        RecordCodCollectionCommand command,
        CancellationToken cancellationToken) => throw new FinanceForbiddenException();

    public Task<CodTransactionResult> ReconcileAsync(
        ReconcileCodCommand command,
        CancellationToken cancellationToken) => throw new FinanceForbiddenException();
}

internal sealed class DisabledOrderFinancialsService : IOrderFinancialsService
{
    public Task<OrderFinancialsResult> GetOrderFinancialsAsync(
        GetOrderFinancialsQuery query,
        CancellationToken cancellationToken) => throw new FinanceForbiddenException();

    public Task<RouteFinancialsResult> GetRouteFinancialsAsync(
        GetRouteFinancialsQuery query,
        CancellationToken cancellationToken) => throw new FinanceForbiddenException();
}
