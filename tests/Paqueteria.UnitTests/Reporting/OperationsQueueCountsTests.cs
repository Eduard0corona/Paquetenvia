using System.Text.Json;
using Reporting.Application.Operations;
using Reporting.Endpoints;

namespace Paqueteria.UnitTests.Reporting;

/// <summary>UI-PHASE2-QUEUE-COUNTS-2026-10-05: building, validating and serializing the work-queue counts.</summary>
public sealed class OperationsQueueCountsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_status_is_present_in_AI04_order_with_zeros()
    {
        var counts = OperationsQueueCounts.From(At, [new OperationsQueueStatusRow("CLOSED", 4, 0, 0)]);

        Assert.Equal(OperationsDashboardVocabulary.Statuses, counts.ByStatus.Select(entry => entry.Key));
        Assert.Equal(17, counts.ByStatus.Length);
        Assert.Equal(4, counts.ByStatus.Single(entry => entry.Key == "CLOSED").Value);
        Assert.Equal(16, counts.ByStatus.Count(entry => entry.Value == 0));
        Assert.Equal(4, counts.Total);
        Assert.Equal(0, counts.Unassigned + counts.NeedsAttention + counts.PriceReview + counts.DeliveredNotClosed + counts.EnRoute);
    }

    [Fact]
    public void No_rows_is_seventeen_zeros()
    {
        var counts = OperationsQueueCounts.From(At, []);
        Assert.All(counts.ByStatus, entry => Assert.Equal(0, entry.Value));
        Assert.Equal(0, counts.Total);
    }

    [Fact]
    public void Status_queues_follow_the_published_groups_and_sql_queues_are_summed()
    {
        var counts = OperationsQueueCounts.From(At,
        [
            new OperationsQueueStatusRow("READY_FOR_PICKUP", 5, 2, 1),
            new OperationsQueueStatusRow("RESCHEDULED", 2, 1, 0),
            new OperationsQueueStatusRow("FAILED_ATTEMPT", 3, 0, 0),
            new OperationsQueueStatusRow("RETURNING", 1, 0, 1),
            new OperationsQueueStatusRow("CLAIM_OPEN", 1, 0, 0),
            new OperationsQueueStatusRow("IN_TRANSIT", 7, 0, 0),
            new OperationsQueueStatusRow("DELIVERING", 4, 0, 2),
            new OperationsQueueStatusRow("DELIVERED", 6, 0, 0),
            new OperationsQueueStatusRow("CLOSED", 9, 0, 0),
        ]);

        Assert.Equal(38, counts.Total);
        Assert.Equal(3, counts.Unassigned);
        Assert.Equal(7, counts.NeedsAttention);
        Assert.Equal(4, counts.PriceReview);
        Assert.Equal(6, counts.DeliveredNotClosed);
        Assert.Equal(11, counts.EnRoute);
        Assert.Equal(["FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"], OperationsQueuePolicy.NeedsAttentionStatuses.ToArray());
        Assert.Equal(["IN_TRANSIT", "DELIVERING"], OperationsQueuePolicy.EnRouteStatuses.ToArray());
        Assert.Equal(["DELIVERED"], OperationsQueuePolicy.DeliveredNotClosedStatuses.ToArray());
        Assert.All(
            OperationsQueuePolicy.UnassignedStatuses,
            status => Assert.True(OperationsDashboardProjectionPolicy.IsUnassignedAlert(status, hasActiveAssignment: false)));
        Assert.All(
            OperationsDashboardVocabulary.Statuses.Except(OperationsQueuePolicy.UnassignedStatuses),
            status => Assert.False(OperationsDashboardProjectionPolicy.IsUnassignedAlert(status, hasActiveAssignment: false)));
    }

    public static TheoryData<OperationsQueueStatusRow[]> InconsistentRows => new()
    {
        { [new OperationsQueueStatusRow("UNKNOWN", 1, 0, 0)] },
        { [new OperationsQueueStatusRow("draft", 1, 0, 0)] },
        { [new OperationsQueueStatusRow("DRAFT", -1, 0, 0)] },
        { [new OperationsQueueStatusRow("DRAFT", 1, 0, 2)] },
        { [new OperationsQueueStatusRow("DRAFT", 1, 0, -1)] },
        { [new OperationsQueueStatusRow("READY_FOR_PICKUP", 1, 2, 0)] },
        { [new OperationsQueueStatusRow("READY_FOR_PICKUP", 1, -1, 0)] },
        { [new OperationsQueueStatusRow("ASSIGNED", 1, 1, 0)] },
        { [new OperationsQueueStatusRow("DRAFT", 1, 0, 0), new OperationsQueueStatusRow("DRAFT", 1, 0, 0)] },
    };

    [Theory]
    [MemberData(nameof(InconsistentRows))]
    public void An_inconsistent_projection_fails_closed(OperationsQueueStatusRow[] rows)
    {
        Assert.Throws<OperationsDashboardContractException>(() => OperationsQueueCounts.From(At, rows));
    }

    [Fact]
    public void A_non_UTC_timestamp_fails_closed()
    {
        Assert.Throws<OperationsDashboardContractException>(() =>
            OperationsQueueCounts.From(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(-6)), []));
    }

    [Fact]
    public void Response_serializes_counts_only_in_the_published_shape()
    {
        var counts = OperationsQueueCounts.From(At,
        [
            new OperationsQueueStatusRow("RESCHEDULED", 2, 1, 1),
            new OperationsQueueStatusRow("DELIVERED", 1, 0, 0),
        ]);

        var json = JsonSerializer.Serialize(OperationsQueueCountsEndpoints.ToResponse(counts));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(["generated_at", "total", "by_status", "queues"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("2026-10-05T18:00:00+00:00", root.GetProperty("generated_at").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt64());
        Assert.Equal(
            OperationsDashboardVocabulary.Statuses,
            root.GetProperty("by_status").EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, root.GetProperty("by_status").GetProperty("RESCHEDULED").GetInt64());
        var queues = root.GetProperty("queues");
        Assert.Equal(1, queues.GetProperty("unassigned").GetInt64());
        Assert.Equal(2, queues.GetProperty("needs_attention").GetInt64());
        Assert.Equal(1, queues.GetProperty("price_review").GetInt64());
        Assert.Equal(1, queues.GetProperty("delivered_not_closed").GetInt64());
        Assert.Equal(0, queues.GetProperty("en_route").GetInt64());
        Assert.DoesNotMatch("[0-9a-f]{8}-[0-9a-f]{4}", json);
    }
}
