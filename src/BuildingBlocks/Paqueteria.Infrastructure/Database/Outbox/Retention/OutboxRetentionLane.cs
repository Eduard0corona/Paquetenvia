namespace Paqueteria.Infrastructure.Database.Outbox.Retention;

public enum OutboxRetentionLane
{
    Business,
    Location,
}

/// <summary>
/// The AI-06 retention contract of one outbox lane, mirrored so that unsafe configuration is
/// rejected at startup instead of at the first purge call. The SQL functions stay authoritative:
/// they re-check the minimum windows and clamp the batch size on every call.
/// </summary>
public sealed record OutboxRetentionLaneContract(
    OutboxRetentionLane Lane,
    string Name,
    string PurgeFunction,
    TimeSpan MinimumProcessedRetention,
    TimeSpan MinimumDeadRetention,
    int MaximumBatchSize)
{
    public static readonly OutboxRetentionLaneContract Business = new(
        OutboxRetentionLane.Business,
        "business",
        "security.purge_outbox",
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(7),
        10_000);

    public static readonly OutboxRetentionLaneContract Location = new(
        OutboxRetentionLane.Location,
        "location",
        "security.purge_location_outbox",
        TimeSpan.FromHours(1),
        TimeSpan.FromDays(1),
        50_000);

    public static IReadOnlyList<OutboxRetentionLaneContract> All { get; } = [Business, Location];

    public static OutboxRetentionLaneContract For(OutboxRetentionLane lane) => lane switch
    {
        OutboxRetentionLane.Business => Business,
        OutboxRetentionLane.Location => Location,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown outbox retention lane."),
    };
}
