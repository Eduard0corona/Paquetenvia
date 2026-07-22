using System.Data.Common;

namespace Paqueteria.Application.Auditing;

public sealed record AuditEntry
{
    public AuditEntry(
        Guid auditId,
        Guid organizationId,
        Guid? actorId,
        string action,
        string entityType,
        Guid entityId,
        string? requestId,
        RedactedAuditPayload payload,
        DateTimeOffset occurredAt)
    {
        if (auditId == Guid.Empty || organizationId == Guid.Empty || entityId == Guid.Empty)
        {
            throw new ArgumentException("Audit, organization and entity identifiers must be non-empty.");
        }

        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("An audit actor identifier must be non-empty when present.", nameof(actorId));
        }

        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(entityType))
        {
            throw new ArgumentException("Audit action and entity type are required.");
        }

        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Audit timestamps must be expressed in UTC.", nameof(occurredAt));
        }

        AuditId = auditId;
        OrganizationId = organizationId;
        ActorId = actorId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        RequestId = requestId;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        OccurredAt = occurredAt;
    }

    public Guid AuditId { get; }

    public Guid OrganizationId { get; }

    public Guid? ActorId { get; }

    public string Action { get; }

    public string EntityType { get; }

    public Guid EntityId { get; }

    public string? RequestId { get; }

    public RedactedAuditPayload Payload { get; }

    public DateTimeOffset OccurredAt { get; }
}

public interface IAppendOnlyAuditWriter
{
    Task WriteAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditEntry entry,
        CancellationToken cancellationToken);
}
