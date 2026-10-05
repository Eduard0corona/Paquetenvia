using System.Collections.Immutable;

namespace Reporting.Application.Operations;

/// <summary>
/// UI-PHASE2-QUEUE-COUNTS-2026-10-05: the operations work-queue counts (AI-05 getOperationsQueueCounts). Integer counts
/// only, over every order the selected organization may read as owner or operator under RLS; no identifier, name,
/// amount or other order data.
/// </summary>
public sealed record OperationsQueueCountsRequest(
    Guid ActorId,
    Guid OrganizationId,
    bool MfaSatisfied);

/// <summary>
/// One snapshot of the counts. <see cref="ByStatus"/> carries all 17 AI-04 statuses in AI-04 order, zeros included.
/// </summary>
public sealed class OperationsQueueCounts
{
    private OperationsQueueCounts(
        DateTimeOffset generatedAt,
        ImmutableArray<KeyValuePair<string, long>> byStatus,
        long unassigned,
        long priceReview)
    {
        GeneratedAt = generatedAt;
        ByStatus = byStatus;
        Unassigned = unassigned;
        PriceReview = priceReview;
        Total = byStatus.Sum(entry => entry.Value);
        NeedsAttention = Sum(OperationsQueuePolicy.NeedsAttentionStatuses);
        DeliveredNotClosed = Sum(OperationsQueuePolicy.DeliveredNotClosedStatuses);
        EnRoute = Sum(OperationsQueuePolicy.EnRouteStatuses);
    }

    public DateTimeOffset GeneratedAt { get; }

    /// <summary>Every AI-04 status, in AI-04 order, with its count (zero when no order has it).</summary>
    public ImmutableArray<KeyValuePair<string, long>> ByStatus { get; }

    public long Total { get; }

    public long Unassigned { get; }

    public long NeedsAttention { get; }

    public long PriceReview { get; }

    public long DeliveredNotClosed { get; }

    public long EnRoute { get; }

    /// <summary>
    /// Builds the snapshot from the per-status rows of the aggregate query. A status outside AI-04, a repeated
    /// status, a negative count or a queue count larger than the statuses it is drawn from is an inconsistent
    /// projection and fails closed.
    /// </summary>
    public static OperationsQueueCounts From(
        DateTimeOffset generatedAt,
        IEnumerable<OperationsQueueStatusRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (generatedAt.Offset != TimeSpan.Zero)
        {
            throw new OperationsDashboardContractException("Queue counts timestamp is invalid.");
        }

        var counts = new Dictionary<string, OperationsQueueStatusRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!OperationsDashboardVocabulary.IsStatus(row.Status) ||
                row.Total < 0 ||
                row.Unassigned < 0 ||
                row.PriceReview < 0 ||
                row.Unassigned > row.Total ||
                row.PriceReview > row.Total ||
                (row.Unassigned > 0 && !OperationsQueuePolicy.UnassignedStatuses.Contains(row.Status)) ||
                !counts.TryAdd(row.Status, row))
            {
                throw new OperationsDashboardContractException("Queue counts projection is invalid.");
            }
        }

        var byStatus = OperationsDashboardVocabulary.Statuses
            .Select(status => new KeyValuePair<string, long>(
                status,
                counts.TryGetValue(status, out var row) ? row.Total : 0L))
            .ToImmutableArray();
        return new OperationsQueueCounts(
            generatedAt,
            byStatus,
            counts.Values.Sum(row => row.Unassigned),
            counts.Values.Sum(row => row.PriceReview));
    }

    private long Sum(ImmutableArray<string> statuses) =>
        ByStatus.Where(entry => statuses.Contains(entry.Key, StringComparer.Ordinal)).Sum(entry => entry.Value);
}

/// <summary>One row of the aggregate query: a status present in the visible orders with its counts.</summary>
public sealed record OperationsQueueStatusRow(
    string Status,
    long Total,
    long Unassigned,
    long PriceReview);

/// <summary>
/// The queue definitions. Each one restates a rule the dashboard already applies; none adds a business rule:
/// unassigned is the dashboard's unassigned alert, price review its cost warning, and the status queues are the
/// UI-STATUS-GROUPS-2026-10-05 groups "Requiere atención" and "En ruta" plus DELIVERED.
/// </summary>
public static class OperationsQueuePolicy
{
    /// <summary>
    /// Statuses that raise the dashboard's unassigned alert when the order has no ACCEPTED or ACTIVE assignment
    /// (<see cref="OperationsDashboardProjectionPolicy.IsUnassignedAlert"/>).
    /// </summary>
    public static readonly ImmutableArray<string> UnassignedStatuses = ["READY_FOR_PICKUP", "RESCHEDULED"];

    public static readonly ImmutableArray<string> NeedsAttentionStatuses =
        ["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"];

    public static readonly ImmutableArray<string> DeliveredNotClosedStatuses = ["DELIVERED"];

    public static readonly ImmutableArray<string> EnRouteStatuses = ["IN_TRANSIT", "DELIVERING"];
}

public interface IOperationsQueueCountsReader
{
    Task<OperationsQueueCounts> ReadAsync(
        OperationsQueueCountsRequest request,
        CancellationToken cancellationToken);
}
