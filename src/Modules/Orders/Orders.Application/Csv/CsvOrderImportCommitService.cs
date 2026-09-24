using Orders.Application.Orders;

namespace Orders.Application.Csv;

/// <summary>
/// Commits a prevalidated CSV-001 batch by replaying each row through the authoritative ORD-001
/// create path. No order rule is re-implemented here: quote ownership, single use, expiry, pricing
/// and legal acceptance are all decided by <see cref="IOrderService"/> inside its own transaction.
/// </summary>
/// <remarks>
/// Idempotency is two-layered. The batch reservation binds the caller's <c>Idempotency-Key</c> to
/// one canonical batch, so replaying that key returns the first response and reusing it for another
/// file is a conflict rather than a second batch. Underneath, every row still carries its own
/// ORD-001 key, which is what makes running the batch a second time — after a crash, or in a race
/// the reservation did not win — produce the same orders instead of new ones.
/// </remarks>
public sealed class CsvOrderImportCommitService(
    IOrderService orders,
    ICsvOrderImportBatchIdempotencyStore batches) : ICsvOrderImportCommitService
{
    public async Task<CsvOrderImportCommitResult> CommitAsync(
        CsvOrderImportCommitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identity = new CsvOrderImportBatchIdentity(
            command.ActorId,
            command.OrganizationId,
            command.IdempotencyKey,
            command.ContentDigest,
            command.Rows.Count);
        if (await batches.ReserveOrReplayAsync(identity, cancellationToken) is { } replay)
        {
            return replay;
        }

        var outcomes = new List<CsvOrderImportRowOutcome>(command.Rows.Count);
        foreach (var row in command.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await CommitRowAsync(command, row, cancellationToken));
        }

        return await batches.CompleteAsync(
            identity,
            new CsvOrderImportCommitResult(command.ContentDigest, outcomes),
            cancellationToken);
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
