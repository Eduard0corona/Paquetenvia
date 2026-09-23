using Orders.Application.Orders;

namespace Orders.Application.Csv;

/// <summary>
/// Commits a prevalidated CSV-001 batch by replaying each row through the authoritative ORD-001
/// create path. No order rule is re-implemented here: quote ownership, single use, expiry, pricing
/// and legal acceptance are all decided by <see cref="IOrderService"/> inside its own transaction.
/// </summary>
public sealed class CsvOrderImportCommitService(IOrderService orders) : ICsvOrderImportCommitService
{
    public async Task<CsvOrderImportCommitResult> CommitAsync(
        CsvOrderImportCommitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var outcomes = new List<CsvOrderImportRowOutcome>(command.Rows.Count);
        foreach (var row in command.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await CommitRowAsync(command, row, cancellationToken));
        }

        return new CsvOrderImportCommitResult(command.ContentDigest, outcomes);
    }

    private async Task<CsvOrderImportRowOutcome> CommitRowAsync(
        CsvOrderImportCommitCommand command,
        CsvOrderImportOrderRow row,
        CancellationToken cancellationToken)
    {
        var quoteId = row.QuoteId.ToString("D");
        try
        {
            var result = await orders.CreateAsync(
                new CreateOrderCommand(
                    command.ActorId,
                    command.OrganizationId,
                    CsvOrderImportIdempotency.DeriveRowKey(
                        command.OrganizationId,
                        command.IdempotencyKey,
                        command.ContentDigest,
                        row.RowNumber),
                    row.QuoteId,
                    row.PayerType,
                    new OrderAcceptanceInput(
                        row.TermsVersion,
                        row.PrivacyVersion,
                        row.AcceptedAt,
                        row.AcceptanceChannel),
                    command.RequestId),
                cancellationToken);
            return new CsvOrderImportRowOutcome(
                row.RowNumber,
                quoteId,
                CsvOrderImportRowStatuses.Created,
                result.Id,
                result.PublicId,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderConflictException exception)
        {
            return new CsvOrderImportRowOutcome(
                row.RowNumber,
                quoteId,
                CsvOrderImportRowStatuses.Failed,
                null,
                null,
                ToFailureCode(exception.Code));
        }
    }

    private static string ToFailureCode(OrderConflictCode code) => code switch
    {
        OrderConflictCode.QuoteUnavailable => CsvOrderImportRowFailureCodes.QuoteUnavailable,
        OrderConflictCode.IdempotencyConflict => CsvOrderImportRowFailureCodes.IdempotencyConflict,
        _ => CsvOrderImportRowFailureCodes.InvalidRequest,
    };
}
