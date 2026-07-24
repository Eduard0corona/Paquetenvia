namespace Realtime.Application.Events;

public sealed record RealtimeEnvelope<TPayload>
    where TPayload : notnull
{
    public RealtimeEnvelope(
        Guid eventId,
        string eventType,
        DateTimeOffset occurredAt,
        Guid aggregateId,
        long aggregateVersion,
        Guid? correlationId,
        TPayload payload)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty event id is required.", nameof(eventId));
        }

        if (!RealtimeEventTypes.IsKnown(eventType))
        {
            throw new ArgumentException("A known versioned event type is required.", nameof(eventType));
        }

        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The event timestamp must be UTC.", nameof(occurredAt));
        }

        if (aggregateId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty aggregate id is required.", nameof(aggregateId));
        }

        if (aggregateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateVersion),
                aggregateVersion,
                "The aggregate version cannot be negative.");
        }

        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("The correlation id cannot be empty.", nameof(correlationId));
        }

        EventId = eventId;
        EventType = eventType;
        OccurredAt = occurredAt;
        AggregateId = aggregateId;
        AggregateVersion = aggregateVersion;
        CorrelationId = correlationId;
        Payload = payload;
    }

    public Guid EventId { get; }
    public string EventType { get; }
    public DateTimeOffset OccurredAt { get; }
    public Guid AggregateId { get; }
    public long AggregateVersion { get; }
    public Guid? CorrelationId { get; }
    public TPayload Payload { get; }
}

public sealed record PublicRealtimeEnvelope<TPayload>
    where TPayload : notnull
{
    public PublicRealtimeEnvelope(
        Guid eventId,
        string eventType,
        DateTimeOffset occurredAt,
        string aggregateId,
        long aggregateVersion,
        Guid? correlationId,
        TPayload payload)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty event id is required.", nameof(eventId));
        }

        if (!RealtimeEventTypes.IsKnown(eventType))
        {
            throw new ArgumentException("A known versioned event type is required.", nameof(eventType));
        }

        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The event timestamp must be UTC.", nameof(occurredAt));
        }

        if (!RealtimePublicOrderId.IsValid(aggregateId))
        {
            throw new ArgumentException("A valid public order id is required.", nameof(aggregateId));
        }

        if (aggregateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateVersion),
                aggregateVersion,
                "The aggregate version cannot be negative.");
        }

        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("The correlation id cannot be empty.", nameof(correlationId));
        }

        EventId = eventId;
        EventType = eventType;
        OccurredAt = occurredAt;
        AggregateId = aggregateId;
        AggregateVersion = aggregateVersion;
        CorrelationId = correlationId;
        Payload = payload;
    }

    public Guid EventId { get; }
    public string EventType { get; }
    public DateTimeOffset OccurredAt { get; }
    public string AggregateId { get; }
    public long AggregateVersion { get; }
    public Guid? CorrelationId { get; }
    public TPayload Payload { get; }
}

public static class RealtimePublicOrderId
{
    public const string Prefix = "ORD_";
    public const int Length = 26;

    public static bool IsValid(string? value) =>
        value is { Length: Length } &&
        value.StartsWith(Prefix, StringComparison.Ordinal) &&
        value.AsSpan(Prefix.Length).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;
}
