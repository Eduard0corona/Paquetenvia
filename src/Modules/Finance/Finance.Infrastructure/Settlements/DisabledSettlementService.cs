using Finance.Application.Settlements;

namespace Finance.Infrastructure.Settlements;

/// <summary>
/// Stand-in used while Finance:Provider is Disabled: settlements are capability the system does not offer,
/// so every operation reports <see cref="Finance.Application.FinanceUnavailableException"/>.
/// </summary>
internal sealed class DisabledSettlementService : ISettlementService
{
    public Task<SettlementResult> CreateAsync(
        CreateSettlementCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<SettlementResult> GetAsync(
        GetSettlementQuery query,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<SettlementResult> AddAdjustmentAsync(
        AddSettlementAdjustmentCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<SettlementResult> ApproveAsync(
        SettlementTransitionCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<SettlementResult> MarkPaidAsync(
        SettlementTransitionCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<SettlementResult> VoidAsync(
        VoidSettlementCommand command,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();

    public Task<SettlementCsvDocument> ExportCsvAsync(
        GetSettlementQuery query,
        CancellationToken cancellationToken) => throw DisabledFinance.Unavailable();
}
