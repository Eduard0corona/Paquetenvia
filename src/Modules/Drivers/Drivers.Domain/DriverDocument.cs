namespace Drivers.Domain;

public sealed class DriverDocument
{
    private DriverDocument()
    {
    }

    public DriverDocument(
        Guid id,
        Guid driverId,
        Guid organizationId,
        DriverDocumentType documentType,
        string objectKey,
        byte[] sha256,
        DateTimeOffset? expiresAt,
        DriverDocumentStatus status,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || driverId == Guid.Empty || organizationId == Guid.Empty ||
            !Enum.IsDefined(documentType) || string.IsNullOrWhiteSpace(objectKey) || sha256 is null ||
            !Enum.IsDefined(status) || createdAt == default || createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The driver document is invalid.");
        }

        Id = id;
        DriverId = driverId;
        OrganizationId = organizationId;
        DocumentType = documentType;
        ObjectKey = objectKey;
        Sha256 = sha256.ToArray();
        ExpiresAt = expiresAt;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid DriverId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public DriverDocumentType DocumentType { get; private set; }
    public string ObjectKey { get; private set; } = string.Empty;
    public byte[] Sha256 { get; private set; } = [];
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DriverDocumentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
