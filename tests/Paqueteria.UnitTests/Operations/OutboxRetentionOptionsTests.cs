using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Database.Outbox.Retention;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OPS-004: retention configuration is rejected before execution when it would ask the AI-06
/// purge functions for a cutoff inside their minimum window or a batch they would silently clamp.
/// </summary>
public sealed class OutboxRetentionOptionsTests
{
    [Fact]
    public void Defaults_are_inert_valid_and_keep_a_margin_above_the_normative_minimums()
    {
        var options = new OutboxRetentionOptions();

        Assert.Empty(OutboxRetentionOptionsValidator.Errors(options));
        Assert.False(options.Enabled);
        Assert.True(options.DryRun);
        foreach (var contract in OutboxRetentionLaneContract.All)
        {
            var lane = options.For(contract.Lane);
            Assert.True(lane.ProcessedRetention > contract.MinimumProcessedRetention);
            Assert.True(lane.DeadRetention > contract.MinimumDeadRetention);
            Assert.InRange(lane.BatchSize, 1, contract.MaximumBatchSize);
            Assert.InRange(lane.MaxBatchesPerRun, 1, OutboxRetentionOptionsValidator.MaximumBatchesPerRun);
        }
    }

    [Fact]
    public void Lane_contracts_mirror_the_ai06_purge_functions()
    {
        Assert.Equal("security.purge_outbox", OutboxRetentionLaneContract.Business.PurgeFunction);
        Assert.Equal(TimeSpan.FromDays(1), OutboxRetentionLaneContract.Business.MinimumProcessedRetention);
        Assert.Equal(TimeSpan.FromDays(7), OutboxRetentionLaneContract.Business.MinimumDeadRetention);
        Assert.Equal(10_000, OutboxRetentionLaneContract.Business.MaximumBatchSize);

        Assert.Equal("security.purge_location_outbox", OutboxRetentionLaneContract.Location.PurgeFunction);
        Assert.Equal(TimeSpan.FromHours(1), OutboxRetentionLaneContract.Location.MinimumProcessedRetention);
        Assert.Equal(TimeSpan.FromDays(1), OutboxRetentionLaneContract.Location.MinimumDeadRetention);
        Assert.Equal(50_000, OutboxRetentionLaneContract.Location.MaximumBatchSize);
    }

    [Theory]
    [InlineData(OutboxRetentionLane.Business, "ProcessedRetention", "23:59:59")]
    [InlineData(OutboxRetentionLane.Business, "DeadRetention", "6.23:59:59")]
    [InlineData(OutboxRetentionLane.Location, "ProcessedRetention", "00:59:59")]
    [InlineData(OutboxRetentionLane.Location, "DeadRetention", "23:59:59")]
    [InlineData(OutboxRetentionLane.Business, "ProcessedRetention", "00:00:00")]
    [InlineData(OutboxRetentionLane.Location, "DeadRetention", "-1.00:00:00")]
    public void Retention_inside_the_normative_minimum_window_is_rejected(
        OutboxRetentionLane lane,
        string field,
        string value)
    {
        var options = new OutboxRetentionOptions();
        Set(options.For(lane), field, TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

        var error = Assert.Single(OutboxRetentionOptionsValidator.Errors(options));
        Assert.Contains($"OutboxRetention:{lane}:{field}", error, StringComparison.Ordinal);
        Assert.Contains("normative minimum", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_normative_minimum_itself_is_accepted_because_the_database_stays_authoritative()
    {
        var options = new OutboxRetentionOptions();
        foreach (var contract in OutboxRetentionLaneContract.All)
        {
            options.For(contract.Lane).ProcessedRetention = contract.MinimumProcessedRetention;
            options.For(contract.Lane).DeadRetention = contract.MinimumDeadRetention;
        }

        Assert.Empty(OutboxRetentionOptionsValidator.Errors(options));
    }

    [Theory]
    [InlineData(OutboxRetentionLane.Business, 0)]
    [InlineData(OutboxRetentionLane.Business, 10_001)]
    [InlineData(OutboxRetentionLane.Location, -1)]
    [InlineData(OutboxRetentionLane.Location, 50_001)]
    public void Batch_size_outside_the_database_clamp_is_rejected(OutboxRetentionLane lane, int batchSize)
    {
        var options = new OutboxRetentionOptions();
        options.For(lane).BatchSize = batchSize;

        var error = Assert.Single(OutboxRetentionOptionsValidator.Errors(options));
        Assert.Contains($"OutboxRetention:{lane}:BatchSize", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OutboxRetentionLane.Business, 10_000)]
    [InlineData(OutboxRetentionLane.Location, 50_000)]
    public void Batch_size_at_the_database_clamp_is_accepted(OutboxRetentionLane lane, int batchSize)
    {
        var options = new OutboxRetentionOptions();
        options.For(lane).BatchSize = batchSize;

        Assert.Empty(OutboxRetentionOptionsValidator.Errors(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Per_run_batch_ceiling_must_be_bounded(int maxBatchesPerRun)
    {
        var options = new OutboxRetentionOptions();
        options.Business.MaxBatchesPerRun = maxBatchesPerRun;
        options.Location.MaxBatchesPerRun = maxBatchesPerRun;

        var errors = OutboxRetentionOptionsValidator.Errors(options);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Contains("MaxBatchesPerRun", error, StringComparison.Ordinal));
    }

    [Fact]
    public void Schedule_and_command_bounds_are_enforced()
    {
        var options = new OutboxRetentionOptions
        {
            PollInterval = TimeSpan.FromSeconds(59),
            InitialDelay = TimeSpan.FromSeconds(-1),
            CommandTimeoutSeconds = 0,
        };

        var errors = OutboxRetentionOptionsValidator.Errors(options);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, error => error.Contains("PollInterval", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("InitialDelay", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("CommandTimeoutSeconds", StringComparison.Ordinal));

        options.PollInterval = TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1);
        Assert.Contains(OutboxRetentionOptionsValidator.Errors(options), error => error.Contains("PollInterval", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_pipeline_reports_every_unsafe_value()
    {
        var options = new OutboxRetentionOptions();
        options.Business.DeadRetention = TimeSpan.FromDays(1);
        options.Location.ProcessedRetention = TimeSpan.FromMinutes(5);

        var result = new OutboxRetentionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }

    private static void Set(OutboxRetentionLaneOptions lane, string field, TimeSpan value)
    {
        if (field == nameof(OutboxRetentionLaneOptions.ProcessedRetention))
        {
            lane.ProcessedRetention = value;
        }
        else
        {
            lane.DeadRetention = value;
        }
    }
}
