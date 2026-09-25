using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Database.Outbox.Retention;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OPS-004: the scheduled job is inert when disabled, keeps running bounded cycles on schedule,
/// and retries a failed cycle on the next schedule instead of stopping the Worker.
/// </summary>
public sealed class OutboxRetentionHostedServiceTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly OutboxRetentionTelemetry _telemetry = new();

    [Fact]
    public async Task Disabled_job_never_calls_the_purge_functions()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.Enabled = false;
        options.InitialDelay = TimeSpan.Zero;
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);
        using var job = Job(gateway, options);

        await job.StartAsync(CancellationToken.None);
        await job.ExecuteTask!.WaitAsync(Deadline);
        await job.StopAsync(CancellationToken.None);

        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task Enabled_job_repeats_bounded_cycles_and_retries_after_a_failed_cycle()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.InitialDelay = TimeSpan.Zero;
        var cycles = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new RecordingPurgeGateway((request, call, _) =>
        {
            if (request.Lane == OutboxRetentionLane.Location && call >= 3)
            {
                cycles.TrySetResult();
            }

            // The first business attempt fails; the schedule must still reach later cycles.
            return request.Lane == OutboxRetentionLane.Business && call == 1
                ? Task.FromException<int>(new TimeoutException("synthetic"))
                : Task.FromResult(request.BatchSize);
        });
        using var job = Job(gateway, options);

        await job.StartAsync(CancellationToken.None);
        await cycles.Task.WaitAsync(Deadline);
        await job.StopAsync(CancellationToken.None);

        Assert.True(job.ExecuteTask!.IsCompletedSuccessfully);
        Assert.All(gateway.Requests, request => Assert.False(request.DryRun));

        // Later cycles ran after the failed one, and the business lane resumed purging.
        Assert.True(gateway.For(OutboxRetentionLane.Location).Count >= 3);
        Assert.True(gateway.For(OutboxRetentionLane.Business).Count >= 2);
    }

    [Fact]
    public async Task Configured_dry_run_job_only_counts_on_every_cycle()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.InitialDelay = TimeSpan.Zero;
        options.DryRun = true;
        var cycles = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new RecordingPurgeGateway((request, call, _) =>
        {
            if (request.Lane == OutboxRetentionLane.Location && call >= 2)
            {
                cycles.TrySetResult();
            }

            return Task.FromResult(request.BatchSize);
        });
        using var job = Job(gateway, options);

        await job.StartAsync(CancellationToken.None);
        await cycles.Task.WaitAsync(Deadline);
        await job.StopAsync(CancellationToken.None);

        Assert.All(gateway.Requests, request => Assert.True(request.DryRun));
    }

    public void Dispose() => _telemetry.Dispose();

    private OutboxRetentionHostedService Job(RecordingPurgeGateway gateway, OutboxRetentionOptions options)
    {
        var time = new AcceleratedTimeProvider();
        var service = new OutboxRetentionService(
            gateway,
            Options.Create(options),
            _telemetry,
            time,
            new CapturingLogger<OutboxRetentionService>());
        return new OutboxRetentionHostedService(
            service,
            Options.Create(options),
            time,
            new CapturingLogger<OutboxRetentionHostedService>());
    }
}
