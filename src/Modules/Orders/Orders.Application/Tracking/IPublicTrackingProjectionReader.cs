using System.Collections.Immutable;

namespace Orders.Application.Tracking;

public enum PublicTimelineEventCode
{
    OrderCreated,
    PickupScheduled,
    PickedUp,
    InTransit,
    OutForDelivery,
    DeliveryAttempted,
    Rescheduled,
    Delivered,
    Returning,
    Returned,
    Cancelled,
}

public sealed record PublicTrackingTimelineItem(
    PublicTimelineEventCode Code,
    DateTimeOffset OccurredAt);

public sealed record PublicTrackingProjection
{
    public PublicTrackingProjection(
        string publicId,
        PublicOrderStatus publicStatus,
        IReadOnlyDictionary<string, string?>? estimatedWindow,
        IEnumerable<PublicTrackingTimelineItem> timeline)
    {
        PublicId = publicId;
        PublicStatus = publicStatus;
        EstimatedWindow = estimatedWindow?.ToImmutableDictionary(StringComparer.Ordinal);
        Timeline = timeline.ToImmutableArray();
    }

    public string PublicId { get; }
    public PublicOrderStatus PublicStatus { get; }
    public IReadOnlyDictionary<string, string?>? EstimatedWindow { get; }
    public ImmutableArray<PublicTrackingTimelineItem> Timeline { get; }
}

public sealed class PublicTrackingLookupResult
{
    private PublicTrackingLookupResult(PublicTrackingProjection? projection) => Projection = projection;

    public bool IsFound => Projection is not null;
    public PublicTrackingProjection? Projection { get; }

    public static PublicTrackingLookupResult NotFound { get; } = new(null);

    public static PublicTrackingLookupResult Found(PublicTrackingProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return new PublicTrackingLookupResult(projection);
    }
}

public interface IPublicTrackingProjectionReader
{
    ValueTask<PublicTrackingLookupResult> FindAsync(
        string token,
        CancellationToken cancellationToken);
}

public sealed class PublicTrackingInfrastructureException : Exception
{
    public PublicTrackingInfrastructureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public enum PublicTrackingProviderKind
{
    Disabled,
    PostgreSql,
}

public sealed class PublicTrackingOptions
{
    public const string SectionName = "PublicTracking";
    public PublicTrackingProviderKind Provider { get; set; } = PublicTrackingProviderKind.Disabled;
    public int CommandTimeoutSeconds { get; set; } = 5;
}
