namespace Drivers.Domain;

/// <summary>
/// One row of <c>drivers.driver_documents</c>. DSP-001 reads the latest row per type; DRV-DOC-001-IN-PERSON-2026-11-02
/// adds the evidence kind and the review columns. A <see cref="DriverDocumentEvidenceKind.File"/> row carries an object
/// key and a 32-byte SHA-256; an <see cref="DriverDocumentEvidenceKind.InPerson"/> row carries neither, only the type,
/// expiry, decision and who reviewed it (AI-06 enforces both shapes with checks). Rows are written with SQL by the
/// Drivers module; the entity maps the table for the module's model only.
/// </summary>
public sealed class DriverDocument
{
    private DriverDocument()
    {
    }

    public Guid Id { get; private set; }
    public Guid DriverId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public DriverDocumentType DocumentType { get; private set; }
    public DriverDocumentEvidenceKind EvidenceKind { get; private set; }

    /// <summary>The stored object of a <see cref="DriverDocumentEvidenceKind.File"/> row; null for an in-person row.</summary>
    public string? ObjectKey { get; private set; }

    /// <summary>The SHA-256 of a <see cref="DriverDocumentEvidenceKind.File"/> row; null for an in-person row.</summary>
    public byte[]? Sha256 { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }
    public DriverDocumentExpiryBasis? ExpiryBasis { get; private set; }
    public DriverDocumentStatus Status { get; private set; }
    public DriverDocumentRejectionCode? RejectionCode { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
