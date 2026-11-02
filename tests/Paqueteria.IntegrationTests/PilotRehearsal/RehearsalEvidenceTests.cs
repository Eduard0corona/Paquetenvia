using System.Text.Json;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// The rehearsal's evidence shape and percentile arithmetic. No container, browser or database: these run in the
/// <c>dotnet</c> job on every change, so the on-demand rehearsal cannot rot unnoticed between dispatches.
/// </summary>
public sealed class RehearsalEvidenceTests
{
    [Fact]
    public void Nearest_rank_percentiles_use_integer_ranks()
    {
        var hundred = Enumerable.Range(1, 100).Select(value => (long)value).ToArray();
        Assert.Equal(50, LatencySummary.NearestRank(hundred, 50));
        Assert.Equal(95, LatencySummary.NearestRank(hundred, 95));
        Assert.Equal(99, LatencySummary.NearestRank(hundred, 99));
        Assert.Equal(100, LatencySummary.NearestRank(hundred, 100));

        // 120 samples: rank ceil(0.95 * 120) = 114, never 115 from binary rounding.
        var hundredTwenty = Enumerable.Range(1, 120).Select(value => (long)value).ToArray();
        Assert.Equal(114, LatencySummary.NearestRank(hundredTwenty, 95));
        Assert.Equal(7, LatencySummary.NearestRank([7L], 95));
        Assert.Throws<ArgumentOutOfRangeException>(() => LatencySummary.NearestRank(hundred, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LatencySummary.NearestRank([], 95));
    }

    [Fact]
    public void Summary_rounds_each_sample_up_to_whole_milliseconds_and_orders_them()
    {
        var samples = new[] { 0.2, 1999.01, 10.0, 3.5 }.Select(TimeSpan.FromMilliseconds).ToArray();
        var summary = LatencySummary.From(samples);
        Assert.Equal(new LatencySummary(4, 4, 2000, 2000), summary);
        Assert.Throws<ArgumentException>(() => LatencySummary.From([]));
    }

    [Fact]
    public void Evidence_serializes_to_the_closed_shape_the_rel001_tool_reads()
    {
        var sample = new LatencySummary(100, 300, 900, 1200);
        var evidence = new RehearsalEvidence(
            RehearsalEvidence.Format,
            new string('a', 40),
            "123",
            1,
            RehearsalEnvironment.LocalCiSynthetic,
            new RehearsalJourneySteps("PASSED", "PASSED", "PASSED", "PASSED"),
            new RehearsalCompleteness(60, 60, new RehearsalMissing(0, 0, 0, 0)),
            new RehearsalLatency(
                RehearsalScreens.Critical.ToDictionary(screen => screen, _ => sample),
                sample,
                new Dictionary<string, double> { ["http_p95_ms"] = 120 }));
        using var json = JsonDocument.Parse(evidence.ToJson());
        var root = json.RootElement;
        Assert.Equal(
            ["completeness", "environment", "format_version", "journey", "latency", "tested_git_sha", "workflow_run_attempt",
             "workflow_run_id"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("paquetenvia-rel001-rehearsal-v1", root.GetProperty("format_version").GetString());
        Assert.Equal(
            ["azure", "kind", "proof_scanner", "proof_storage", "representative_of_pilot_database"],
            root.GetProperty("environment").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["cod_recorded_reconciled_closed", "csv_import_created_orders", "failed_attempt_decided_redelivered",
             "settlement_calculated_approved_paid_exported"],
            root.GetProperty("journey").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["event_version_gaps", "missing_delivery_photo", "missing_pickup_photo", "unaudited_status_changes"],
            root.GetProperty("completeness").GetProperty("missing").EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        var screens = root.GetProperty("latency").GetProperty("critical_screens");
        Assert.Equal(
            ["/driver/stops", "/driver/stops/:id", "/ops/inbox", "/ops/orders/:id", "/track/:token"],
            screens.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["p50_ms", "p95_ms", "p99_ms", "samples"],
            screens.GetProperty("/ops/inbox").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_critical_screens_are_the_ai24_list()
    {
        var contract = File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(AppContext.BaseDirectory),
            "docs", "normative", "v0.6", "specs", "AI-24_RUNTIME_HARDENING_CONTRACT.yaml"));
        var block = contract[contract.IndexOf("    critical_screens:", StringComparison.Ordinal)..];
        var listed = block.Split('\n').Skip(1).TakeWhile(line => line.StartsWith("    - ", StringComparison.Ordinal))
            .Select(line => line[6..].Trim()).ToArray();
        Assert.Equal(listed, RehearsalScreens.Critical);
    }
}
