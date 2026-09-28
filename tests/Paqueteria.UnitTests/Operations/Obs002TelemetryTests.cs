using Microsoft.Extensions.Logging;
using Notifications.Application.Dispatching;
using Notifications.Infrastructure.Dispatching;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OBS-002: the structured events the pilot alerts query carry counts and fixed names only, and
/// are emitted exactly when the alert rules expect them.
/// </summary>
public sealed class Obs002TelemetryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lane_summary_reports_counts_and_claim_age_once_per_window_then_resets()
    {
        var time = new SteppedTimeProvider(Start);
        var logger = new CapturingLogger<OutboxLaneMonitor>();
        var monitor = new OutboxLaneMonitor(logger, time);

        monitor.ReportIfDue(OutboxLanes.Notifications);
        monitor.Claimed(OutboxLanes.Notifications, 3, Start - TimeSpan.FromSeconds(90));
        monitor.Claimed(OutboxLanes.Notifications, 0, null);
        monitor.Settled(OutboxLanes.Notifications, OutboxSettlement.Processed);
        monitor.Settled(OutboxLanes.Notifications, OutboxSettlement.Retry);
        monitor.Settled(OutboxLanes.Notifications, OutboxSettlement.Dead);
        monitor.LoopFailed(OutboxLanes.Notifications);
        time.Now = Start + TimeSpan.FromSeconds(59);
        monitor.ReportIfDue(OutboxLanes.Notifications);
        Assert.Empty(logger.Entries);

        time.Now = Start + OutboxLaneMonitor.ReportInterval;
        monitor.ReportIfDue(OutboxLanes.Notifications);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(TelemetryEvents.OutboxLaneSummary, entry.EventId);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("notifications", entry.Values["Lane"]);
        Assert.Equal(3L, entry.Values["Claimed"]);
        Assert.Equal(1L, entry.Values["Processed"]);
        Assert.Equal(1L, entry.Values["Retry"]);
        Assert.Equal(1L, entry.Values["Dead"]);
        Assert.Equal(1L, entry.Values["LoopFailures"]);
        Assert.Equal(90_000L, entry.Values["MaxClaimAgeMs"]);
        Assert.Equal(60L, entry.Values["WindowSeconds"]);
        AssertAllowlisted(entry);

        time.Now += OutboxLaneMonitor.ReportInterval;
        monitor.ReportIfDue(OutboxLanes.Notifications);
        var idle = logger.Entries.Last();
        Assert.Equal(LogLevel.Information, idle.Level);
        Assert.Equal(0L, idle.Values["Claimed"]);
        Assert.Equal(0L, idle.Values["Dead"]);
        Assert.Equal(0L, idle.Values["MaxClaimAgeMs"]);
    }

    [Fact]
    public void Lane_summary_rejects_lanes_outside_the_allowlist()
    {
        var monitor = new OutboxLaneMonitor(new CapturingLogger<OutboxLaneMonitor>(), TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Claimed("tenant-a", 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.ReportIfDue("order:123"));
    }

    [Fact]
    public void Http_summary_counts_status_classes_and_skips_empty_windows()
    {
        var counter = new HttpStatusCounter();
        var logger = new CapturingLogger<HttpStatusReporter>();
        var reporter = new HttpStatusReporter(counter, TimeProvider.System, logger);

        reporter.Report(60);
        Assert.Empty(logger.Entries);

        foreach (var status in new[] { 101, 200, 204, 302, 401, 404, 500, 503 })
        {
            counter.Record(status);
        }

        reporter.Report(60);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(TelemetryEvents.HttpStatusSummary, entry.EventId);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(8L, entry.Values["Total"]);
        Assert.Equal(3L, entry.Values["Status2xx"]);
        Assert.Equal(1L, entry.Values["Status3xx"]);
        Assert.Equal(2L, entry.Values["Status4xx"]);
        Assert.Equal(2L, entry.Values["Status5xx"]);
        AssertAllowlisted(entry);

        reporter.Report(60);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public async Task Scheduler_logs_one_outcome_per_cycle_without_the_exception()
    {
        var logger = new CapturingLogger<PeriodicJobScheduler>();
        var scheduler = new PeriodicJobScheduler(TimeProvider.System, logger);
        using var cancellation = new CancellationTokenSource();
        var job = new ScriptedJob(run =>
        {
            if (run == 1)
            {
                throw new InvalidOperationException("synthetic failure with a secret-looking value");
            }

            cancellation.Cancel();
            return Task.CompletedTask;
        });

        await scheduler.RunAsync(job, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        var entries = logger.Entries.ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.Equal(TelemetryEvents.ScheduledJobCycle, entry.EventId));
        Assert.Equal(("failure", LogLevel.Error), (entries[0].Values["Outcome"], entries[0].Level));
        Assert.Equal(("success", LogLevel.Information), (entries[1].Values["Outcome"], entries[1].Level));
        Assert.All(entries, entry => Assert.Equal("synthetic.obs-002", entry.Values["Job"]));
        Assert.All(entries, entry => Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal));
        Assert.All(entries, AssertAllowlisted);
    }

    [Theory]
    [InlineData("DEAD", NotificationErrorCodes.UnknownTopic, OutboxSettlement.Dead)]
    [InlineData("MAX_ATTEMPTS", NotificationErrorCodes.MaxAttemptsExhausted, OutboxSettlement.Dead)]
    [InlineData(NotificationDeliveryOutcome.Permanent, NotificationErrorCodes.ProviderPermanent, OutboxSettlement.Dead)]
    [InlineData(NotificationDeliveryOutcome.Success, NotificationErrorCodes.ProviderAccepted, OutboxSettlement.Processed)]
    [InlineData(NotificationDeliveryOutcome.Transient, NotificationErrorCodes.ProviderTransient, OutboxSettlement.Retry)]
    [InlineData(NotificationDeliveryOutcome.Ambiguous, NotificationErrorCodes.ProviderAmbiguous, OutboxSettlement.Retry)]
    [InlineData("SOURCE", NotificationErrorCodes.SourceExpanded, OutboxSettlement.Processed)]
    [InlineData("SOURCE", NotificationErrorCodes.NoEligibleRecipient, OutboxSettlement.Processed)]
    [InlineData("SOURCE", NotificationErrorCodes.TemplateNotFound, OutboxSettlement.Dead)]
    public void Notification_outcomes_map_to_the_outbox_status_the_sql_functions_leave(
        string outcome,
        string code,
        OutboxSettlement expected) =>
        Assert.Equal(expected, NotificationsOutboxProcessor.Settlement(outcome, code));

    private static void AssertAllowlisted(CapturedLog entry) =>
        Assert.All(
            entry.Values.Keys.Where(key => key != "{OriginalFormat}"),
            key => Assert.Contains(key, TelemetryDimensions.Allowed));

    private sealed class SteppedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedJob(Func<int, Task> cycle) : IScheduledJob
    {
        private int _runs;

        public string Name => "synthetic.obs-002";

        public TimeSpan Interval => TimeSpan.FromMilliseconds(1);

        public Task RunOnceAsync(CancellationToken cancellationToken) => cycle(++_runs);
    }
}
