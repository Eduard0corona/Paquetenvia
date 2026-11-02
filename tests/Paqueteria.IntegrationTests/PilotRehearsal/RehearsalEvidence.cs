using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>
/// The evidence the REL-001 synthetic rehearsal writes for <c>tools/rel-001/rel001.py</c>
/// (<c>paquetenvia-rel001-rehearsal-v1</c>, AI-24 <c>pilot_readiness</c>). Counts and latency summaries only: no
/// identifier, public id, token, link, name, address, phone or e-mail ever reaches it. The tool judges the thresholds;
/// the rehearsal only measures.
/// </summary>
internal sealed record RehearsalEvidence(
    [property: JsonPropertyName("format_version")] string FormatVersion,
    [property: JsonPropertyName("tested_git_sha")] string TestedGitSha,
    [property: JsonPropertyName("workflow_run_id")] string WorkflowRunId,
    [property: JsonPropertyName("workflow_run_attempt")] int WorkflowRunAttempt,
    [property: JsonPropertyName("environment")] RehearsalEnvironment Environment,
    [property: JsonPropertyName("journey")] RehearsalJourneySteps Journey,
    [property: JsonPropertyName("completeness")] RehearsalCompleteness Completeness,
    [property: JsonPropertyName("latency")] RehearsalLatency Latency)
{
    internal const string Format = "paquetenvia-rel001-rehearsal-v1";
    internal const string FileName = "rel001-rehearsal-evidence.json";

    /// <summary>Where the workflow wants the evidence; locally the test writes under its own temporary directory.</summary>
    internal const string OutputDirectoryVariable = "REL001_REHEARSAL_OUTPUT";
    internal const string TestedGitShaVariable = "REL001_TESTED_GIT_SHA";
    internal const string RunIdVariable = "GITHUB_RUN_ID";
    internal const string RunAttemptVariable = "GITHUB_RUN_ATTEMPT";

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Z4: containers on the runner, no Azure, photos through the S3-compatible double and the Synthetic scanner.</summary>
internal sealed record RehearsalEnvironment(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("azure")] bool Azure,
    [property: JsonPropertyName("proof_storage")] string ProofStorage,
    [property: JsonPropertyName("proof_scanner")] string ProofScanner,
    [property: JsonPropertyName("representative_of_pilot_database")] bool RepresentativeOfPilotDatabase)
{
    internal static RehearsalEnvironment LocalCiSynthetic { get; } =
        new("LOCAL_CI_SYNTHETIC", false, "S3_COMPATIBLE_DOUBLE", "SYNTHETIC", false);
}

/// <summary>Z5, the cobro y fallas journey: each step is PASSED once it ran to its end on the real platform.</summary>
internal sealed record RehearsalJourneySteps(
    [property: JsonPropertyName("cod_recorded_reconciled_closed")] string CodRecordedReconciledClosed,
    [property: JsonPropertyName("failed_attempt_decided_redelivered")] string FailedAttemptDecidedRedelivered,
    [property: JsonPropertyName("csv_import_created_orders")] string CsvImportCreatedOrders,
    [property: JsonPropertyName("settlement_calculated_approved_paid_exported")] string SettlementCalculatedApprovedPaidExported);

/// <summary>Z6 entry half: AI-02 orders_with_complete_events_and_pod over the rehearsal's delivered orders.</summary>
internal sealed record RehearsalCompleteness(
    [property: JsonPropertyName("delivered_orders")] int DeliveredOrders,
    [property: JsonPropertyName("complete_orders")] int CompleteOrders,
    [property: JsonPropertyName("missing")] RehearsalMissing Missing);

internal sealed record RehearsalMissing(
    [property: JsonPropertyName("event_version_gaps")] int EventVersionGaps,
    [property: JsonPropertyName("unaudited_status_changes")] int UnauditedStatusChanges,
    [property: JsonPropertyName("missing_pickup_photo")] int MissingPickupPhoto,
    [property: JsonPropertyName("missing_delivery_photo")] int MissingDeliveryPhoto);

/// <summary>
/// Z7 and Z8: the blocking AI-02 latencies (every critical screen and order creation) and the AI-15 technical ones,
/// which are reported only. A technical metric that was not measured is left out.
/// </summary>
internal sealed record RehearsalLatency(
    [property: JsonPropertyName("critical_screens")] IReadOnlyDictionary<string, LatencySummary> CriticalScreens,
    [property: JsonPropertyName("order_creation")] LatencySummary OrderCreation,
    [property: JsonPropertyName("technical")] IReadOnlyDictionary<string, double> Technical);

/// <summary>Nearest-rank percentiles of whole milliseconds (AI-24 pilot_readiness.latency.percentile_method).</summary>
internal sealed record LatencySummary(
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("p50_ms")] long P50Ms,
    [property: JsonPropertyName("p95_ms")] long P95Ms,
    [property: JsonPropertyName("p99_ms")] long P99Ms)
{
    internal static LatencySummary From(IReadOnlyCollection<TimeSpan> samples)
    {
        if (samples.Count == 0)
        {
            throw new ArgumentException("A latency summary needs at least one sample.", nameof(samples));
        }

        var milliseconds = samples.Select(sample => (long)Math.Ceiling(sample.TotalMilliseconds)).Order().ToArray();
        return new LatencySummary(
            milliseconds.Length,
            NearestRank(milliseconds, 50),
            NearestRank(milliseconds, 95),
            NearestRank(milliseconds, 99));
    }

    /// <summary>The smallest value with at least <paramref name="percentile"/>% of the sorted values at or below it.</summary>
    internal static long NearestRank(IReadOnlyList<long> sorted, int percentile)
    {
        if (sorted.Count == 0 || percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        // ceil(percentile * count / 100) in integers: no binary rounding can move the rank.
        var rank = (percentile * sorted.Count + 99) / 100;
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
