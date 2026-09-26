using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Database.Outbox.Retention;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OPS-004: one retention cycle is bounded per lane, reuses fixed cutoffs, keeps lanes
/// independent and reports auditable evidence through logs and low-cardinality metrics.
/// </summary>
public sealed class OutboxRetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly OutboxRetentionTelemetry _telemetry = new();

    [Fact]
    public void Cutoffs_subtract_each_retention_and_never_move_forward_when_truncated()
    {
        var now = Now.AddTicks(1_234_567);
        var lane = new OutboxRetentionLaneOptions
        {
            ProcessedRetention = TimeSpan.FromDays(2),
            DeadRetention = TimeSpan.FromDays(9),
        };

        var cutoffs = OutboxRetentionCutoffs.Calculate(now.ToOffset(TimeSpan.FromHours(-6)), lane);

        Assert.Equal(TimeSpan.Zero, cutoffs.ProcessedBefore.Offset);
        Assert.Equal(0, cutoffs.ProcessedBefore.UtcTicks % 10);
        Assert.Equal(0, cutoffs.DeadBefore.UtcTicks % 10);
        Assert.InRange(now - TimeSpan.FromDays(2) - cutoffs.ProcessedBefore, TimeSpan.Zero, TimeSpan.FromTicks(9));
        Assert.InRange(now - TimeSpan.FromDays(9) - cutoffs.DeadBefore, TimeSpan.Zero, TimeSpan.FromTicks(9));
    }

    [Fact]
    public async Task Destructive_run_stops_at_the_per_run_ceiling_even_when_rows_remain()
    {
        var options = Destructive();
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);

        var report = await Service(gateway, options).RunOnceAsync(CancellationToken.None);

        foreach (var contract in OutboxRetentionLaneContract.All)
        {
            var lane = options.For(contract.Lane);
            Assert.Equal(lane.MaxBatchesPerRun, gateway.For(contract.Lane).Count);
            var laneReport = Assert.Single(report.Lanes, value => value.Lane == contract.Lane);
            Assert.Equal(lane.MaxBatchesPerRun, laneReport.Batches);
            Assert.Equal((long)lane.MaxBatchesPerRun * lane.BatchSize, laneReport.AffectedRows);
            Assert.False(laneReport.Exhausted);
            Assert.Equal(OutboxRetentionOutcomes.Success, laneReport.Outcome);
        }
    }

    [Fact]
    public async Task Destructive_run_stops_early_when_a_batch_comes_back_short()
    {
        var options = Destructive();
        var gateway = new RecordingPurgeGateway((request, call, _) =>
            Task.FromResult(call == 1 ? request.BatchSize : 2));

        var report = await Service(gateway, options).RunOnceAsync(CancellationToken.None);

        Assert.All(report.Lanes, lane =>
        {
            Assert.Equal(2, lane.Batches);
            Assert.Equal(lane.BatchSize + 2, lane.AffectedRows);
            Assert.True(lane.Exhausted);
        });
        Assert.Equal(4, gateway.Requests.Count);
    }

    [Fact]
    public async Task Every_batch_of_a_lane_run_reuses_the_cutoffs_computed_at_its_start()
    {
        var options = Destructive();
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);
        var time = new FixedTimeProvider(Now) { AdvanceOnRead = TimeSpan.FromMinutes(5) };

        await Service(gateway, options, time).RunOnceAsync(CancellationToken.None);

        foreach (var contract in OutboxRetentionLaneContract.All)
        {
            var requests = gateway.For(contract.Lane);
            Assert.Single(requests.Select(request => (request.ProcessedBefore, request.DeadBefore)).Distinct());
            Assert.All(requests, request => Assert.False(request.DryRun));
        }
    }

    [Fact]
    public async Task Lanes_use_independent_cutoffs_batch_sizes_and_ceilings()
    {
        var options = Destructive();
        options.Business = new() { ProcessedRetention = TimeSpan.FromDays(3), DeadRetention = TimeSpan.FromDays(10), BatchSize = 7, MaxBatchesPerRun = 2 };
        options.Location = new() { ProcessedRetention = TimeSpan.FromHours(6), DeadRetention = TimeSpan.FromDays(2), BatchSize = 11, MaxBatchesPerRun = 4 };
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);

        await Service(gateway, options).RunOnceAsync(CancellationToken.None);

        var business = gateway.For(OutboxRetentionLane.Business);
        var location = gateway.For(OutboxRetentionLane.Location);
        Assert.Equal(2, business.Count);
        Assert.Equal(4, location.Count);
        Assert.All(business, request =>
        {
            Assert.Equal(7, request.BatchSize);
            Assert.Equal(Now - TimeSpan.FromDays(3), request.ProcessedBefore);
            Assert.Equal(Now - TimeSpan.FromDays(10), request.DeadBefore);
        });
        Assert.All(location, request =>
        {
            Assert.Equal(11, request.BatchSize);
            Assert.Equal(Now - TimeSpan.FromHours(6), request.ProcessedBefore);
            Assert.Equal(Now - TimeSpan.FromDays(2), request.DeadBefore);
        });
    }

    [Fact]
    public async Task Configured_dry_run_calls_each_lane_once_and_never_requests_deletion()
    {
        var options = Destructive();
        options.DryRun = true;
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);

        var report = await Service(gateway, options).RunOnceAsync(CancellationToken.None);

        Assert.All(gateway.Requests, request => Assert.True(request.DryRun));
        Assert.Single(gateway.For(OutboxRetentionLane.Business));
        Assert.Single(gateway.For(OutboxRetentionLane.Location));
        Assert.All(report.Lanes, lane =>
        {
            Assert.True(lane.DryRun);
            Assert.Equal(1, lane.Batches);
            Assert.Equal(lane.BatchSize, lane.AffectedRows);

            // A saturated dry-run count means more rows may be eligible beyond one batch.
            Assert.False(lane.Exhausted);
        });
    }

    [Fact]
    public async Task Explicit_dry_run_path_overrides_a_destructive_configuration()
    {
        var gateway = RecordingPurgeGateway.Returning(_ => 3);

        var report = await Service(gateway, Destructive()).DryRunAsync(CancellationToken.None);

        Assert.Equal(2, gateway.Requests.Count);
        Assert.All(gateway.Requests, request => Assert.True(request.DryRun));
        Assert.All(report.Lanes, lane => Assert.Equal(3, lane.AffectedRows));
    }

    [Fact]
    public async Task A_failing_lane_is_reported_without_preventing_the_other_lane()
    {
        var logger = new CapturingLogger<OutboxRetentionService>();
        var gateway = new RecordingPurgeGateway((request, _, _) => request.Lane == OutboxRetentionLane.Business
            ? Task.FromException<int>(new TimeoutException("synthetic"))
            : Task.FromResult(0));

        var report = await Service(gateway, Destructive(), logger: logger).RunOnceAsync(CancellationToken.None);

        var business = Assert.Single(report.Lanes, lane => lane.Lane == OutboxRetentionLane.Business);
        Assert.Equal(OutboxRetentionOutcomes.Failure, business.Outcome);
        Assert.Equal("timeout", business.ErrorClass);
        Assert.Equal(0, business.Batches);
        var location = Assert.Single(report.Lanes, lane => lane.Lane == OutboxRetentionLane.Location);
        Assert.Equal(OutboxRetentionOutcomes.Success, location.Outcome);
        Assert.True(location.Exhausted);

        var failure = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal("business", failure.Values["Lane"]);
        Assert.Equal("timeout", failure.Values["ErrorClass"]);
    }

    [Fact]
    public async Task Cancellation_stops_between_batches_and_reports_partial_progress()
    {
        using var cancellation = new CancellationTokenSource();
        var logger = new CapturingLogger<OutboxRetentionService>();
        var gateway = new RecordingPurgeGateway((request, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(request.BatchSize);
        });
        var options = Destructive();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(gateway, options, logger: logger).RunOnceAsync(cancellation.Token));

        var request = Assert.Single(gateway.Requests);
        Assert.Equal(OutboxRetentionLane.Business, request.Lane);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(OutboxRetentionOutcomes.Cancelled, entry.Values["Outcome"]);
        Assert.Equal(1, entry.Values["Batches"]);
        Assert.Equal((long)options.Business.BatchSize, entry.Values["AffectedRows"]);
    }

    [Fact]
    public async Task Each_lane_run_logs_structured_operational_evidence()
    {
        var logger = new CapturingLogger<OutboxRetentionService>();
        var options = Destructive();
        var gateway = RecordingPurgeGateway.Returning(_ => 1);

        await Service(gateway, options, logger: logger).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(4004, entry.EventId.Id);
            Assert.Equal(LogLevel.Information, entry.Level);
            foreach (var field in new[]
            {
                "Lane", "Outcome", "DryRun", "ProcessedBefore", "DeadBefore", "BatchSize",
                "MaxBatchesPerRun", "Batches", "AffectedRows", "Exhausted", "StartedAt", "CompletedAt", "ErrorClass",
            })
            {
                Assert.True(entry.Values.ContainsKey(field), $"Missing evidence field {field}.");
            }
        });

        var business = Assert.Single(logger.Entries, entry => Equals(entry.Values["Lane"], "business"));
        Assert.Equal(false, business.Values["DryRun"]);
        Assert.Equal(OutboxRetentionOutcomes.Success, business.Values["Outcome"]);
        Assert.Equal("2026-09-18T12:00:00.0000000+00:00", business.Values["ProcessedBefore"]);
        Assert.Equal("2026-08-26T12:00:00.0000000+00:00", business.Values["DeadBefore"]);
        Assert.Equal(options.Business.BatchSize, business.Values["BatchSize"]);
        Assert.Equal(1, business.Values["Batches"]);
        Assert.Equal(1L, business.Values["AffectedRows"]);
        Assert.Equal("2026-09-25T12:00:00.0000000+00:00", business.Values["StartedAt"]);
    }

    [Fact]
    public async Task Metrics_are_split_by_lane_and_mode_without_high_cardinality_dimensions()
    {
        var measurements = new ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (ReferenceEquals(instrument.Meter, _telemetry.Meter))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, Tags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, Tags(tags))));
        listener.Start();

        var options = Destructive();
        var gateway = new RecordingPurgeGateway((request, call, _) => request.Lane == OutboxRetentionLane.Location
            ? Task.FromException<int>(new InvalidOperationException("synthetic"))
            : Task.FromResult(call == 1 ? request.BatchSize : 4));
        var service = Service(gateway, options);
        await service.RunOnceAsync(CancellationToken.None);
        await service.DryRunAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        var allowed = new HashSet<string>(["lane", "mode", "outcome", "error_class"], StringComparer.Ordinal);
        Assert.All(measurements, measurement =>
        {
            Assert.Contains(measurement.Tags["lane"], new object?[] { "business", "location" });
            Assert.Subset(allowed, measurement.Tags.Keys.ToHashSet(StringComparer.Ordinal));
        });

        Assert.Equal(options.Business.BatchSize + 4, Sum("outbox.retention.deleted_rows", "business", null));
        Assert.Equal(2, Sum("outbox.retention.batches", "business", "delete"));
        Assert.Equal(4, Sum("outbox.retention.dry_run_eligible", "business", null));
        Assert.Equal(1, Sum("outbox.retention.failures", "location", "delete"));
        Assert.Contains(measurements, measurement => measurement.Instrument == "outbox.retention.failures" &&
            Equals(measurement.Tags["error_class"], "unexpected"));
        Assert.Contains(measurements, measurement => measurement.Instrument == "outbox.retention.run_duration" &&
            Equals(measurement.Tags["lane"], "location") && Equals(measurement.Tags["outcome"], "failure"));

        // The last-success gauge only reports lane/mode pairs that actually succeeded.
        var lastSuccess = measurements.Where(measurement => measurement.Instrument == "outbox.retention.last_success").ToArray();
        Assert.Contains(lastSuccess, measurement => Equals(measurement.Tags["lane"], "business") && Equals(measurement.Tags["mode"], "delete"));
        Assert.DoesNotContain(lastSuccess, measurement => Equals(measurement.Tags["lane"], "location") && Equals(measurement.Tags["mode"], "delete"));
        Assert.All(lastSuccess, measurement => Assert.Equal(Now.ToUnixTimeSeconds(), measurement.Value));

        double Sum(string instrument, string lane, string? mode) => measurements
            .Where(measurement => measurement.Instrument == instrument &&
                Equals(measurement.Tags["lane"], lane) &&
                (mode is null || Equals(measurement.Tags.GetValueOrDefault("mode"), mode)))
            .Sum(measurement => measurement.Value);
    }

    public void Dispose() => _telemetry.Dispose();

    internal static OutboxRetentionOptions Destructive() => new()
    {
        Enabled = true,
        DryRun = false,
        Business = new() { ProcessedRetention = TimeSpan.FromDays(7), DeadRetention = TimeSpan.FromDays(30), BatchSize = 5, MaxBatchesPerRun = 3 },
        Location = new() { ProcessedRetention = TimeSpan.FromDays(1), DeadRetention = TimeSpan.FromDays(7), BatchSize = 8, MaxBatchesPerRun = 2 },
    };

    private OutboxRetentionService Service(
        RecordingPurgeGateway gateway,
        OutboxRetentionOptions options,
        TimeProvider? time = null,
        ILogger<OutboxRetentionService>? logger = null) =>
        new(
            gateway,
            Options.Create(options),
            _telemetry,
            time ?? new FixedTimeProvider(Now),
            logger ?? new CapturingLogger<OutboxRetentionService>());

    private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }
}
