namespace Notifications.Domain;

public enum NotificationChannel
{
    InApp,
}

public enum NotificationStatus
{
    Pending,
    Sent,
    Failed,
}

public static class NotificationContractValues
{
    public static string ToContractValue(this NotificationChannel channel) => channel switch
    {
        NotificationChannel.InApp => "IN_APP",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown channel."),
    };

    public static string ToContractValue(this NotificationStatus status) => status switch
    {
        NotificationStatus.Pending => "PENDING",
        NotificationStatus.Sent => "SENT",
        NotificationStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status."),
    };
}

public sealed class Notification
{
    private Notification()
    {
    }

    public Notification(
        Guid id,
        Guid ownerOrganizationId,
        Guid recipientUserId,
        string templateKey,
        int templateVersion,
        string variablesSnapshot,
        Guid sourceEventId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || recipientUserId == Guid.Empty ||
            sourceEventId == Guid.Empty)
        {
            throw new ArgumentException("Notification identifiers must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(templateKey))
        {
            throw new ArgumentException("A template key is required.", nameof(templateKey));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(variablesSnapshot);
        Id = id;
        OwnerOrganizationId = ownerOrganizationId;
        RecipientUserId = recipientUserId;
        Channel = NotificationChannel.InApp;
        Status = NotificationStatus.Pending;
        Attempts = 0;
        Version = 1;
        TemplateKey = templateKey;
        TemplateVersion = templateVersion;
        VariablesSnapshot = variablesSnapshot;
        SourceEventId = sourceEventId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public NotificationStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int Version { get; private set; }
    public string TemplateKey { get; private set; } = string.Empty;
    public int TemplateVersion { get; private set; }
    public string VariablesSnapshot { get; private set; } = "{}";
    public Guid SourceEventId { get; private set; }
    public string? LastProviderAttemptCode { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void RecordProviderOutcome(
        NotificationStatus status,
        string providerCode,
        DateTimeOffset attemptedAt,
        int expectedVersion)
    {
        if (expectedVersion != Version || Status is NotificationStatus.Sent or NotificationStatus.Failed)
        {
            throw new InvalidOperationException("Notification version or terminal state changed.");
        }

        if (status is not (NotificationStatus.Pending or NotificationStatus.Sent or NotificationStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerCode);
        Status = status;
        Attempts++;
        Version++;
        LastProviderAttemptCode = providerCode;
        LastAttemptAt = attemptedAt;
        UpdatedAt = attemptedAt;
    }

    public void FinalizeMaximumAttempts(DateTimeOffset finalizedAt, int expectedVersion)
    {
        if (expectedVersion != Version || Status != NotificationStatus.Pending)
        {
            throw new InvalidOperationException("Notification version or status changed.");
        }

        Status = NotificationStatus.Failed;
        Version++;
        LastProviderAttemptCode = "MAX_ATTEMPTS_EXHAUSTED";
        LastAttemptAt = finalizedAt;
        UpdatedAt = finalizedAt;
    }
}
