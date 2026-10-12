using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-002: the Worker and outbox paths log and measure only allowlisted, low-cardinality dimensions
/// (fixed lane, job and outcome names, bounded error classes, counts and cutoffs), and the pilot alert
/// queries read only the OBS-002 telemetry dimensions. No payload, token, tenant, identifier or
/// personal datum can become a log property, a metric tag or an alert dimension.
/// </summary>
public sealed partial class ObservabilityArchitectureTests
{
    /// <summary>Worker, outbox and scheduler code whose logs reach Log Analytics in the pilot.</summary>
    private static readonly string[] WorkerAndOutboxPaths =
    [
        "src/BuildingBlocks/Paqueteria.Infrastructure/Observability",
        "src/BuildingBlocks/Paqueteria.Infrastructure/Scheduling",
        "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox",
        "src/Modules/Notifications/Notifications.Infrastructure/Dispatching",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle",
        "src/Modules/Realtime/Realtime.Infrastructure/Dispatching",
        "src/Modules/Custody/Custody.Infrastructure/Cleanup",
        "src/Modules/Orders/Orders.Infrastructure/Lifecycle",
        "src/Paqueteria.Worker",
    ];

    /// <summary>
    /// Log template properties allowed on those paths: the OBS-002 dimensions plus the existing bounded
    /// OPS-003/OPS-004/NTF-001 fields (fixed names, error classes, counts, configured values and cutoffs).
    /// </summary>
    private static readonly HashSet<string> WorkerLogProperties = new(TelemetryDimensions.Allowed, StringComparer.Ordinal)
    {
        "Owner",
        "Channel",
        "Code",
        "Provider",
        "ErrorClass",
        "DryRun",
        "PollInterval",
        "Batches",
        "Rows",
        "Finalized",
        "BatchSize",
        "MaxBatchesPerRun",
        "AffectedRows",
        "DeadEligible",
        "DeadEligibleBound",
        "Exhausted",
        "ProcessedBefore",
        "DeadBefore",
        "StartedAt",
        "CompletedAt",
        // ORD-AUTO-CLOSE-2026-10-10: per-cycle attempt counts of the order auto-close job.
        "Closed",
        "NotEligible",
        "Superseded",
        "Failed",
        "Attempted",
    };

    private static readonly HashSet<string> MetricTags = new(StringComparer.Ordinal)
    {
        "lane",
        "mode",
        "outcome",
        "error_class",
        "job",
        "audience",
        "event_type",
    };

    private static readonly string[] ForbiddenFragments =
    [
        "payload", "token", "secret", "password", "email", "phone", "address", "recipient", "tenant",
        "organization", "orgid", "userid", "driverid", "orderid", "publicid", "aggregateid", "messageid",
        "name", "body", "variables", "coordinate", "latitude", "longitude",
    ];

    [Fact]
    public void Obs002_dimension_allowlist_carries_no_identifier_or_personal_field()
    {
        Assert.All(TelemetryDimensions.Allowed, AssertNotSensitive);
        Assert.All(WorkerLogProperties, AssertNotSensitive);
        Assert.All(MetricTags, AssertNotSensitive);
        Assert.Equal(
            ["dispatch", "notifications", "realtime_business", "realtime_location"],
            OutboxLanes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Worker_and_outbox_logs_use_only_allowlisted_properties()
    {
        var calls = LogCalls().ToArray();

        Assert.NotEmpty(calls);
        foreach (var (path, call) in calls)
        {
            Assert.False(call.Contains("$\"", StringComparison.Ordinal), $"{path}: interpolated log message: {call}");
            foreach (var property in Placeholders(call))
            {
                Assert.True(
                    WorkerLogProperties.Contains(property),
                    $"{path}: log property {{{property}}} is not in the OBS-002 Worker/outbox allowlist");
            }
        }
    }

    [Fact]
    public void Obs002_events_carry_exactly_the_telemetry_dimensions()
    {
        var properties = LogCalls()
            .Where(call => call.Path.Contains("/Observability/", StringComparison.Ordinal) ||
                           call.Path.EndsWith("Scheduling/PeriodicJobScheduler.cs", StringComparison.Ordinal))
            .SelectMany(call => Placeholders(call.Call))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(TelemetryDimensions.Allowed.ToHashSet(StringComparer.Ordinal), properties);
        Assert.Equal(TelemetryDimensions.Allowed.Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));
        Assert.Equal(4601, TelemetryEvents.OutboxLaneSummary.Id);
        Assert.Equal(4602, TelemetryEvents.ScheduledJobCycle.Id);
        Assert.Equal(4603, TelemetryEvents.HttpStatusSummary.Id);
    }

    [Fact]
    public void Worker_and_outbox_metric_tags_are_allowlisted()
    {
        var tags = SourceFiles()
            .Where(file => file.Text.Contains("System.Diagnostics.Metrics", StringComparison.Ordinal))
            .SelectMany(file => MetricTagPattern().Matches(file.Text).Select(match => (file.Path, Tag: match.Groups["tag"].Value)))
            .ToArray();

        Assert.NotEmpty(tags);
        Assert.All(tags, tag => Assert.True(MetricTags.Contains(tag.Tag), $"{tag.Path}: metric tag {tag.Tag} is not allowlisted"));
    }

    [Fact]
    public void Observability_code_never_reads_the_outbox_or_the_database()
    {
        foreach (var file in SourceFiles().Where(file => file.Path.Contains("/Observability/", StringComparison.Ordinal)))
        {
            foreach (var forbidden in new[] { "Npgsql", "outbox_events", "SELECT", "DbContext", "HttpContext.User", "Request.Path.Value", "Request.Query" })
            {
                Assert.DoesNotContain(forbidden, file.Text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Pilot_alerts_query_only_obs002_events_and_dimensions()
    {
        var template = File.ReadAllText(TestRepository.GetPath("deploy/azure/pilot/observability.bicep"));
        var retention = File.ReadAllText(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox/Retention/OutboxRetentionService.cs"));

        var properties = StatePropertyPattern().Matches(template).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(properties);
        Assert.Subset(TelemetryDimensions.Allowed.ToHashSet(StringComparer.Ordinal), properties);

        var eventIds = EventIdPattern().Matches(template).Select(match => int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        Assert.Equal(
            new[] { 4004, TelemetryEvents.OutboxLaneSummary.Id, TelemetryEvents.ScheduledJobCycle.Id, TelemetryEvents.HttpStatusSummary.Id }.Order(),
            eventIds.Order());
        Assert.Contains("new(4004, \"OutboxRetentionLaneCompleted\")", retention, StringComparison.Ordinal);

        foreach (var lane in OutboxLanes.All)
        {
            Assert.Contains($"\"{lane}\"", template, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("param alertEmailAddress string =", template, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosts_wire_the_obs002_events()
    {
        var api = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        var telemetry = api.IndexOf("app.UseHttpStatusTelemetry();", StringComparison.Ordinal);
        var exceptionHandler = api.IndexOf("app.UseExceptionHandler();", StringComparison.Ordinal);
        Assert.True(telemetry > 0 && telemetry < exceptionHandler, "the status counter must wrap the exception handler");
        Assert.Contains("builder.Services.AddHttpStatusTelemetry();", api, StringComparison.Ordinal);

        foreach (var (path, lane) in new[]
                 {
                     ("src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxDispatcher.cs", "OutboxLanes.Notifications"),
                     ("src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs", "OutboxLanes.Dispatch"),
                 })
        {
            var source = File.ReadAllText(TestRepository.GetPath(path));
            Assert.Contains($"lanes.ReportIfDue({lane});", source, StringComparison.Ordinal);
            Assert.Contains($"lanes.LoopFailed({lane});", source, StringComparison.Ordinal);
        }

        var realtime = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxDispatchers.cs"));
        Assert.Equal(2, Regex.Count(realtime, @"lanes\.ReportIfDue\(RealtimeOutboxLanes\.Monitored\(""(business|location)""\)\);"));
    }

    private static void AssertNotSensitive(string dimension)
    {
        var normalized = dimension.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        Assert.All(ForbiddenFragments, fragment => Assert.False(
            normalized.Contains(fragment, StringComparison.Ordinal),
            $"dimension {dimension} looks like an identifier, payload or personal field ({fragment})"));
    }

    private static IEnumerable<string> Placeholders(string call) =>
        StringLiteralPattern().Matches(call)
            .SelectMany(literal => PlaceholderPattern().Matches(literal.Value))
            .Select(match => match.Groups["name"].Value);

    private static IEnumerable<(string Path, string Call)> LogCalls() =>
        SourceFiles().SelectMany(file => file.Text.Split("logger.Log", StringSplitOptions.None).Skip(1)
            .Select(call => (file.Path, call[..call.IndexOf(");", StringComparison.Ordinal)])));

    private static IEnumerable<(string Path, string Text)> SourceFiles() =>
        WorkerAndOutboxPaths
            .SelectMany(root => Directory.EnumerateFiles(TestRepository.GetPath(root), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/'), File.ReadAllText(path)));

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"(?:KeyValuePair<string, object\?>\(|\bnew\()""(?<tag>[a-z_]+)"",")]
    private static partial Regex MetricTagPattern();

    [GeneratedRegex(@"\be\.State\.(?<name>[A-Za-z0-9_]+)")]
    private static partial Regex StatePropertyPattern();

    [GeneratedRegex(@"toint\(e\.EventId\) == (?<id>\d{4})|EventId (?:==|in \()\s*(?<id>\d{4})|Log_s has ""(?<id>\d{4})""")]
    private static partial Regex EventIdPattern();
}
