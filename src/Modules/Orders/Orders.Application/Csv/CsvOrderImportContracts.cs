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
    public const string ColumnFile = "file";

    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumDataRows = 500;
    public const int MaximumVersionLength = 64;

    public static readonly ImmutableArray<string> Header =
    [
        ColumnQuoteId,
        ColumnPayerType,
        ColumnTermsVersion,
        ColumnPrivacyVersion,
        ColumnAcceptedAt,
        ColumnAcceptanceChannel,
    ];

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

public sealed record CsvOrderImportRowPreview(
    int RowNumber,
    string? QuoteId,
    string? PayerType,
    bool Valid,
    IReadOnlyList<CsvOrderImportRowError> Errors)
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
    string AcceptanceChannel);

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
