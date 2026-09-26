using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Database.Outbox.Retention;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OPS-004 scheduled job: an <see cref="IScheduledJob"/> on the configured interval whose every
/// invocation is exactly one bounded retention cycle; repetition belongs to the scheduler.
/// </summary>
public sealed class OutboxRetentionJobTests : IDisposable
{
    private readonly OutboxRetentionTelemetry _telemetry = new();

    [Fact]
    public void Job_is_a_shared_scheduled_job_on_the_configured_interval()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.PollInterval = TimeSpan.FromMinutes(7);

        IScheduledJob job = Job(RecordingPurgeGateway.Returning(_ => 0), options);

        Assert.Equal("outbox.retention", job.Name);
        Assert.Equal(TimeSpan.FromMinutes(7), job.Interval);
    }

    [Fact]
    public async Task Each_invocation_is_exactly_one_bounded_cycle()
    {
        // Every batch comes back full, so only MaxBatchesPerRun can end a lane.
        var options = OutboxRetentionServiceTests.Destructive();
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);
        var job = Job(gateway, options);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(options.Business.MaxBatchesPerRun, gateway.For(OutboxRetentionLane.Business).Count);
        Assert.Equal(options.Location.MaxBatchesPerRun, gateway.For(OutboxRetentionLane.Location).Count);
        Assert.All(gateway.Requests, request => Assert.False(request.DryRun));

        await job.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2 * options.Business.MaxBatchesPerRun, gateway.For(OutboxRetentionLane.Business).Count);
        Assert.Equal(2 * options.Location.MaxBatchesPerRun, gateway.For(OutboxRetentionLane.Location).Count);
    }

    [Fact]
    public async Task Dry_run_invocation_is_one_counting_call_per_lane()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.DryRun = true;
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);

        await Job(gateway, options).RunOnceAsync(CancellationToken.None);

        Assert.Single(gateway.For(OutboxRetentionLane.Business));
        Assert.Single(gateway.For(OutboxRetentionLane.Location));
        Assert.All(gateway.Requests, request => Assert.True(request.DryRun));
    }

    [Fact]
    public async Task A_failed_lane_neither_fails_the_invocation_nor_blocks_the_other_lane()
    {
        var gateway = new RecordingPurgeGateway((request, _, _) => request.Lane == OutboxRetentionLane.Business
            ? Task.FromException<int>(new TimeoutException("synthetic"))
            : Task.FromResult(0));

        await Job(gateway, OutboxRetentionServiceTests.Destructive()).RunOnceAsync(CancellationToken.None);

        Assert.Single(gateway.For(OutboxRetentionLane.Business));
        Assert.Single(gateway.For(OutboxRetentionLane.Location));
    }

    [Fact]
    public async Task Cancellation_propagates_so_the_scheduler_stops_cleanly()
    {
        using var cancellation = new CancellationTokenSource();
        var gateway = new RecordingPurgeGateway((request, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(request.BatchSize);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Job(gateway, OutboxRetentionServiceTests.Destructive()).RunOnceAsync(cancellation.Token));

        // Cancellation is observed between batches: the first batch finished and nothing followed.
        Assert.Single(gateway.Requests);
    }

    public void Dispose() => _telemetry.Dispose();

    private OutboxRetentionJob Job(RecordingPurgeGateway gateway, OutboxRetentionOptions options) =>
        new(
            new OutboxRetentionService(
                gateway,
                Options.Create(options),
                _telemetry,
                TimeProvider.System,
                new CapturingLogger<OutboxRetentionService>()),
            Options.Create(options));
}
