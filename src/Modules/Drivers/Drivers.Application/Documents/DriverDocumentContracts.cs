using System.Collections.Immutable;
using Paqueteria.Application.Paging;

namespace Drivers.Application.Documents;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md): the published vocabulary of
/// in-person driver documents. A dispatcher of the driver's organization sees the original, records its type and the
/// printed expiry (or the review date of a permanent Sinaloa license) and approves (VALID) or rejects it (REJECTED, with
/// a fixed reason). No photo, file, document number, object key or hash exists.
/// </summary>
public static class DriverDocumentVocabulary
{
    /// <summary>
    /// Y11 "Solo los exigidos": the only types a dispatcher records, in the order screens show them. The driver's vehicle
    /// narrows them further to its required list (Drivers:Eligibility); BACKGROUND_CHECK and OTHER are never recorded.
    /// </summary>
    public static readonly ImmutableArray<string> RecordableTypes =
        ["IDENTITY", "DRIVER_LICENSE", "VEHICLE_CARD", "INSURANCE"];

    /// <summary>Y7 "Lista fija": Ilegible, vencido, no es el pedido, no es del repartidor, incompleto, no aceptado.</summary>
    public static readonly ImmutableArray<string> RejectionCodes =
        ["ILLEGIBLE", "EXPIRED", "WRONG_DOCUMENT", "HOLDER_MISMATCH", "INCOMPLETE", "NOT_ACCEPTED"];

    public const string PrintedExpiry = "PRINTED_EXPIRY";
    public const string PermanentLicenseReview = "PERMANENT_LICENSE_REVIEW";

    public static readonly ImmutableArray<string> ExpiryBases = [PrintedExpiry, PermanentLicenseReview];

    public const string Valid = "VALID";
    public const string Rejected = "REJECTED";
    public const string Revoked = "REVOKED";

    /// <summary>The decisions a dispatcher records in person.</summary>
    public static readonly ImmutableArray<string> Decisions = [Valid, Rejected];

    /// <summary>Every AI-06 status a listed document can carry (a FILE row of another path may be PENDING or EXPIRED).</summary>
    public static readonly ImmutableArray<string> Statuses = ["PENDING", "VALID", "REJECTED", "EXPIRED", "REVOKED"];

    /// <summary>The only type a permanent Sinaloa license review applies to (Y5).</summary>
    public const string DriverLicense = "DRIVER_LICENSE";
}

/// <summary>
/// The calendar rules of an in-person document, in the pilot's local calendar (America/Mazatlan, Culiacán time). The
/// date math is here; the conversion of a local date to an instant happens in PostgreSQL (<c>AT TIME ZONE</c>), so the
/// containers need no time zone data.
/// </summary>
public static class DriverDocumentCalendar
{
    public const string TimeZone = "America/Mazatlan";

    /// <summary>Y5 "Cada año (Recommended)": the review date of a permanent Sinaloa license is the review day plus 12 months.</summary>
    public const int PermanentLicenseReviewMonths = 12;

    /// <summary>A printed date further ahead than this is a typing mistake, not a document (INE lasts ten years).</summary>
    public const int MaximumYearsAhead = 10;

    /// <summary>
    /// The last local day the document is valid. Y12 "Sí, todo ese día": a printed date is valid for the whole day, so
    /// the document expires at 00:00 of the next local day; the review date of a permanent license is treated the same
    /// way. February 29 plus 12 months is February 28.
    /// </summary>
    public static DateOnly ValidThrough(DateOnly today, string expiryBasis, DateOnly? printedDate) => expiryBasis switch
    {
        DriverDocumentVocabulary.PrintedExpiry => printedDate ?? throw new ArgumentNullException(nameof(printedDate)),
        DriverDocumentVocabulary.PermanentLicenseReview => today.AddMonths(PermanentLicenseReviewMonths),
        _ => throw new ArgumentOutOfRangeException(nameof(expiryBasis)),
    };

    /// <summary>
    /// An approved document must still be valid today and no further ahead than <see cref="MaximumYearsAhead"/> years:
    /// a printed date before today is a document that already expired (dispatch rejects it as EXPIRED instead).
    /// </summary>
    public static bool IsAcceptable(DateOnly today, DateOnly validThrough) =>
        validThrough >= today && validThrough <= today.AddYears(MaximumYearsAhead);
}

/// <param name="ExpiryBasis">Present only with a VALID decision.</param>
/// <param name="PrintedDate">The printed expiry, present only with <see cref="DriverDocumentVocabulary.PrintedExpiry"/>.</param>
/// <param name="RejectionCode">Present only with a REJECTED decision.</param>
public sealed record RecordDriverDocumentCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid DriverId,
    string DocumentType,
    string Decision,
    string? ExpiryBasis,
    DateOnly? PrintedDate,
    string? RejectionCode,
    string IdempotencyKey,
    string? RequestId);

public sealed record RevokeDriverDocumentCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid DriverId,
    Guid DocumentId,
    string? RequestId);

public sealed record ListDriverDocumentsQuery(Guid ActorId, Guid OrganizationId, Guid? AfterDriverId);

/// <summary>
/// One document as the API shows it: type, status, the rejection reason, the expiry instant and its last valid local
/// day with where it came from, when it was recorded and when it was revoked. Never a file, key, hash, number or the
/// reviewer's identity.
/// </summary>
public sealed record DriverDocumentResult(
    Guid Id,
    string DocumentType,
    string Status,
    string? RejectionCode,
    DateTimeOffset? ExpiresAt,
    DateOnly? ValidThrough,
    string? ExpiryBasis,
    DateTimeOffset RecordedAt,
    DateTimeOffset? RevokedAt);

/// <summary>One required type of the driver's vehicle and its latest document, null when none was ever recorded.</summary>
public sealed record DriverDocumentSlotResult(string DocumentType, DriverDocumentResult? Document);

public sealed record DriverDocumentSetResult(Guid DriverId, IReadOnlyList<DriverDocumentSlotResult> Documents)
{
    public IReadOnlyList<DriverDocumentSlotResult> Documents { get; } = Documents.ToImmutableArray();
}

/// <param name="DocumentsEnabled">Drivers:Documents:Enabled; false in the pilot until GATE-007 (Y2), then nothing is read.</param>
public sealed record DriverDocumentPageResult(
    bool DocumentsEnabled,
    IReadOnlyList<DriverDocumentSetResult> Items,
    Guid? NextAfterDriverId)
{
    public IReadOnlyList<DriverDocumentSetResult> Items { get; } = Items.ToImmutableArray();
}

public sealed record MyDriverDocumentsResult(bool DocumentsEnabled, IReadOnlyList<DriverDocumentSlotResult> Documents)
{
    public IReadOnlyList<DriverDocumentSlotResult> Documents { get; } = Documents.ToImmutableArray();
}

/// <summary>The roster page size, the one of listOrganizationDrivers, so both lists page the same drivers.</summary>
public static class DriverDocumentPaging
{
    public const int PageSize = 100;
}

/// <summary>The listDriverDocuments cursor: the last listed driver id under its own version byte (never another list's).</summary>
public static class DriverDocumentCursorCodec
{
    public const byte Version = 4;

    public static string Encode(Guid afterDriverId) => IdKeysetCursorCodec.Encode(Version, afterDriverId);

    public static bool TryDecode(string? value, out Guid afterDriverId) =>
        IdKeysetCursorCodec.TryDecode(value, Version, out afterDriverId);
}

public interface IDriverDocumentService
{
    /// <summary>listDriverDocuments: one page of the organization's OWN drivers that are not INACTIVE.</summary>
    Task<DriverDocumentPageResult> ListAsync(ListDriverDocumentsQuery query, CancellationToken cancellationToken);

    /// <summary>listMyDriverDocuments: the actor's own driver profile in the selected organization.</summary>
    Task<MyDriverDocumentsResult> ListMineAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken);

    /// <summary>recordDriverDocument: one in-person record, its audit row and its idempotency answer in one transaction.</summary>
    Task<DriverDocumentResult> RecordAsync(RecordDriverDocumentCommand command, CancellationToken cancellationToken);

    /// <summary>revokeDriverDocument: the current VALID document of its type becomes REVOKED, audited in one transaction.</summary>
    Task<DriverDocumentResult> RevokeAsync(RevokeDriverDocumentCommand command, CancellationToken cancellationToken);
}

public static class DriverDocumentConflictCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";

    /// <summary>Y11: the type is not required for the driver's vehicle (or is not recordable at all).</summary>
    public const string DocumentTypeNotRequired = "DOCUMENT_TYPE_NOT_REQUIRED";

    /// <summary>An approval whose last valid day is before today in Culiacán, or more than ten years ahead.</summary>
    public const string ExpiryDateOutOfRange = "EXPIRY_DATE_OUT_OF_RANGE";

    /// <summary>Only the current VALID document of its type can be revoked.</summary>
    public const string DocumentNotRevocable = "DOCUMENT_NOT_REVOCABLE";

    public static readonly ImmutableArray<string> Record =
        [InvalidRequest, IdempotencyConflict, DocumentTypeNotRequired, ExpiryDateOutOfRange];

    public static readonly ImmutableArray<string> Revoke = [InvalidRequest, DocumentNotRevocable];
}

/// <summary>A DRIVER member without a driver profile in the selected organization (403).</summary>
public sealed class DriverDocumentForbiddenException()
    : Exception("The actor has no driver profile in the selected organization.");

/// <summary>An unknown, foreign, retired or non-OWN driver, or a document that is not that driver's (uniform 404).</summary>
public sealed class DriverDocumentNotFoundException() : Exception("The driver document was not found.");

public sealed class DriverDocumentConflictException(string code) : Exception("The driver document request conflicts.")
{
    public string Code { get; } = code;
}

/// <summary>Documents are switched off, the store failed or a stored value is outside the vocabulary (503).</summary>
public sealed class DriverDocumentUnavailableException(Exception? innerException = null)
    : Exception("Driver documents cannot be processed now.", innerException);
