using Finance.Application;
using Finance.Application.Cod;
using Finance.Application.Financials;

namespace Finance.Infrastructure;

/// <summary>
/// Stand-ins used while Finance:Provider is Disabled. A disabled provider is capability the system does not
/// offer, not an actor lacking it, so every operation reports <see cref="FinanceUnavailableException"/>.
/// </summary>
internal static class DisabledFinance
{
    internal static FinanceUnavailableException Unavailable() => new("The finance provider is disabled.");
}

internal sealed class DisabledCodTransactionService : ICodTransactionService
{
    public Task<CodTransactionResult> RecordAsync(
        RecordCodCollectionCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<CodTransactionResult> ReconcileAsync(
        ReconcileCodCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();
}

internal sealed class DisabledOrderFinancialsService : IOrderFinancialsService
{
    public Task<OrderFinancialsResult> GetOrderFinancialsAsync(
        GetOrderFinancialsQuery query,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<RouteFinancialsResult> GetRouteFinancialsAsync(
        GetRouteFinancialsQuery query,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();
}
