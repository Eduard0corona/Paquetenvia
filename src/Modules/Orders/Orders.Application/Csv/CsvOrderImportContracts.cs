using System.Collections.Immutable;

namespace Orders.Application.Csv;

/// <summary>
/// Canonical CSV-001 import contract. The header, its column order, the file limits and the
/// error codes below are the public contract; see docs/development/csv-001-order-csv-import.md.
/// </summary>
public static class CsvOrderImportContract
{
    public const string ColumnQuoteId = "quote_id";
    public const string ColumnPayerType = "payer_type";
    public const string ColumnTermsVersion = "terms_version";
    public const string ColumnPrivacyVersion = "privacy_version";
    public const string ColumnAcceptedAt = "accepted_at";
    public const string ColumnAcceptanceChannel = "acceptance_channel";

    /// <summary>
    /// D6-COD-EXPECTED: optional seventh column with the dispatcher-declared COD in MXN integer cents. It is named
    /// after the <c>cod_expected_cents</c> field of <c>POST /orders</c>, as every other column is named after its
    /// ORD-001 field.
    /// </summary>
    public const string ColumnCodExpectedCents = "cod_expected_cents";
    public const string ColumnFile = "file";

    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumDataRows = 500;
    public const int MaximumVersionLength = Orders.OrderAcceptanceInputPolicy.MaximumVersionLength;

    /// <summary>The six-column header without COD; every row of such a file has no COD expectation.</summary>
    public static readonly ImmutableArray<string> Header =
    [
        ColumnQuoteId,
        ColumnPayerType,
        ColumnTermsVersion,
        ColumnPrivacyVersion,
        ColumnAcceptedAt,
        ColumnAcceptanceChannel,
    ];

    /// <summary>
    /// The seven-column header with the optional COD column last (D6-COD-EXPECTED). Appending it keeps every
    /// existing six-column file valid and unchanged in meaning.
    /// </summary>
    public static readonly ImmutableArray<string> HeaderWithCod = [.. Header, ColumnCodExpectedCents];

    public static string HeaderLine => string.Join(',', Header);
}

/// <summary>File-level rejections: the file cannot be interpreted as a CSV-001 document at all.</summary>
public static class CsvOrderImportFileErrorCodes
{
    public const string EncodingInvalid = "ENCODING_INVALID";
    public const string MalformedQuoting = "MALFORMED_QUOTING";
    public const string HeaderInvalid = "HEADER_INVALID";
    public const string FileEmpty = "FILE_EMPTY";
    public const string RowLimitExceeded = "ROW_LIMIT_EXCEEDED";
}

/// <summary>Row-level prevalidation rejections, reported per row and per column.</summary>
public static class CsvOrderImportRowErrorCodes
{
    public const string ColumnCountInvalid = "COLUMN_COUNT_INVALID";
    public const string QuoteIdInvalid = "QUOTE_ID_INVALID";
    public const string QuoteIdDuplicated = "QUOTE_ID_DUPLICATED";
    public const string PayerTypeInvalid = "PAYER_TYPE_INVALID";
    public const string TermsVersionInvalid = "TERMS_VERSION_INVALID";
    public const string PrivacyVersionInvalid = "PRIVACY_VERSION_INVALID";
    public const string AcceptedAtInvalid = "ACCEPTED_AT_INVALID";
    public const string AcceptanceChannelInvalid = "ACCEPTANCE_CHANNEL_INVALID";
    public const string CodExpectedCentsInvalid = "COD_EXPECTED_CENTS_INVALID";
}

public static class CsvOrderImportRowStatuses
{
    public const string Created = "CREATED";
    public const string Failed = "FAILED";
}

/// <summary>Domain-time failures surfaced by ORD-001 while committing an individual row.</summary>
public static class CsvOrderImportRowFailureCodes
{
    public const string QuoteUnavailable = "QUOTE_UNAVAILABLE";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string InvalidRequest = "INVALID_REQUEST";
}

public sealed record CsvOrderImportRowError(string Column, string Code);

/// <summary>
/// One previewed row. <see cref="CodExpectedCents"/> echoes the COD the row declares so the dispatcher can check it
/// before confirming: zero for a row without COD, null (omitted from the response) when the row did not prevalidate.
/// </summary>
public sealed record CsvOrderImportRowPreview(
    int RowNumber,
    string? QuoteId,
    string? PayerType,
    bool Valid,
    IReadOnlyList<CsvOrderImportRowError> Errors,
    long? CodExpectedCents = null)
{
    public IReadOnlyList<CsvOrderImportRowError> Errors { get; } = Errors.ToImmutableArray();
}

/// <summary>A prevalidated row, shaped so it maps one-to-one onto an ORD-001 create command.</summary>
public sealed record CsvOrderImportOrderRow(
    int RowNumber,
    Guid QuoteId,
    string PayerType,
    string TermsVersion,
    string PrivacyVersion,
    DateTimeOffset AcceptedAt,
    string AcceptanceChannel,
    long CodExpectedCents = 0);

public sealed record CsvOrderImportPrevalidation(
    string ContentDigest,
    IReadOnlyList<string> FileErrors,
    IReadOnlyList<CsvOrderImportRowPreview> Rows,
    IReadOnlyList<CsvOrderImportOrderRow> ValidRows)
{
    public IReadOnlyList<string> FileErrors { get; } = FileErrors.ToImmutableArray();

    public IReadOnlyList<CsvOrderImportRowPreview> Rows { get; } = Rows.ToImmutableArray();

    public IReadOnlyList<CsvOrderImportOrderRow> ValidRows { get; } = ValidRows.ToImmutableArray();

    public int TotalRows => Rows.Count;

    public int ValidRowCount => ValidRows.Count;

    public int InvalidRowCount => checked(Rows.Count - ValidRows.Count);

    /// <summary>A batch is committable only when the whole file prevalidates; partial files are rejected.</summary>
    public bool IsCommittable =>
        FileErrors.Count == 0 && Rows.Count > 0 && ValidRows.Count == Rows.Count;
}

public sealed record CsvOrderImportCommitCommand(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    string ContentDigest,
    IReadOnlyList<CsvOrderImportOrderRow> Rows,
    string? RequestId)
{
    public IReadOnlyList<CsvOrderImportOrderRow> Rows { get; } = Rows.ToImmutableArray();
}

public sealed record CsvOrderImportRowOutcome(
    int RowNumber,
    string QuoteId,
    string Status,
    Guid? OrderId,
    string? PublicId,
    string? ErrorCode);

public sealed record CsvOrderImportCommitResult(
    string ContentDigest,
    IReadOnlyList<CsvOrderImportRowOutcome> Rows)
{
    public IReadOnlyList<CsvOrderImportRowOutcome> Rows { get; } = Rows.ToImmutableArray();

    public int TotalRows => Rows.Count;

    public int CreatedRows => Rows.Count(row => row.Status == CsvOrderImportRowStatuses.Created);

    public int FailedRows => checked(TotalRows - CreatedRows);
}

/// <summary>
/// Commits an already prevalidated CSV-001 batch. Preview never reaches this interface, which is
/// how "no order exists before confirmation" is guaranteed structurally rather than by convention.
/// </summary>
public interface ICsvOrderImportCommitService
{
    Task<CsvOrderImportCommitResult> CommitAsync(
        CsvOrderImportCommitCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Identifies one CSV-001 commit: the tenant, the batch key its caller supplied and the exact file
/// that key is bound to. The actor is carried for the tenant database context only; it is not part
/// of the identity, so two members of the same organization replaying one batch see one result.
/// </summary>
public sealed record CsvOrderImportBatchIdentity(
    Guid ActorId,
    Guid OrganizationId,
    string IdempotencyKey,
    string ContentDigest,
    int RowCount);

/// <summary>
/// Raised when a CSV commit <c>Idempotency-Key</c> is replayed for a different batch. The key is a
/// promise about which file is being confirmed; honouring it for other content would let one key
/// create two batches.
/// </summary>
public sealed class CsvOrderImportBatchConflictException()
    : Exception("The CSV batch idempotency key is already bound to different content.");

/// <summary>
/// Batch-level idempotency for CSV-001 commits: one record per tenant and <c>Idempotency-Key</c>,
/// bound to the canonical batch digest, kept under the shared <c>platform.idempotency_keys</c>
/// conventions. It sits above the per-row ORD-001 keys, which stay the last line of defence.
/// </summary>
public interface ICsvOrderImportBatchIdempotencyStore
{
    /// <summary>
    /// Reserves <paramref name="identity"/>, or returns the response already committed under its
    /// key. Returns null when this call owns the batch and must run it.
    /// </summary>
    /// <exception cref="CsvOrderImportBatchConflictException">
    /// The key is already bound to a different batch.
    /// </exception>
    Task<CsvOrderImportCommitResult?> ReserveOrReplayAsync(
        CsvOrderImportBatchIdentity identity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="result"/> under the reservation and returns the response that is
    /// stored afterwards, which is the one a concurrent commit of the same batch wrote first when
    /// it won the race. Both callers therefore answer with the same batch.
    /// </summary>
    Task<CsvOrderImportCommitResult> CompleteAsync(
        CsvOrderImportBatchIdentity identity,
        CsvOrderImportCommitResult result,
        CancellationToken cancellationToken);
}
