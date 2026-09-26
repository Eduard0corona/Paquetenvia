using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Database.Outbox.Retention;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OPS-004 host: inert when disabled, otherwise it only hands the retention job to the shared
/// <see cref="IJobScheduler"/>, which keeps running bounded cycles and retries a failed cycle on
/// the next interval instead of stopping the Worker.
/// </summary>
public sealed class OutboxRetentionHostedServiceTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly OutboxRetentionTelemetry _telemetry = new();

    [Fact]
    public async Task Disabled_host_never_schedules_the_job_or_calls_the_purge_functions()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.Enabled = false;
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);
        var scheduler = new RecordingJobScheduler();
        using var host = Host(gateway, options, scheduler);

        await host.StartAsync(CancellationToken.None);
        await host.ExecuteTask!.WaitAsync(Deadline);
        await host.StopAsync(CancellationToken.None);

        Assert.Empty(scheduler.Jobs);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task Enabled_host_delegates_once_to_the_shared_scheduler_and_runs_no_cycle_itself()
    {
        var options = OutboxRetentionServiceTests.Destructive();
        options.PollInterval = TimeSpan.FromMinutes(7);
        var gateway = RecordingPurgeGateway.Returning(request => request.BatchSize);
        var scheduler = new RecordingJobScheduler();
        using var host = Host(gateway, options, scheduler);

        await host.StartAsync(CancellationToken.None);

        // The recording scheduler returns at once, so the host completes: it owns no loop.
        await host.ExecuteTask!.WaitAsync(Deadline);
        await host.StopAsync(CancellationToken.None);

        var job = Assert.IsType<OutboxRetentionJob>(Assert.Single(scheduler.Jobs));
        Assert.Equal(TimeSpan.FromMinutes(7), job.Interval);
        Assert.True(Assert.Single(scheduler.Tokens).IsCancellationRequested);
        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task Enabled_job_repeats_bounded_cycles_and_retries_after_a_failed_cycle()
    {
        var options = OutboxRetentionServiceTests.Destructive();
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
        using var host = Host(gateway, options);

        await host.StartAsync(CancellationToken.None);
        await cycles.Task.WaitAsync(Deadline);
        await host.StopAsync(CancellationToken.None);

        Assert.True(host.ExecuteTask!.IsCompletedSuccessfully);
        Assert.All(gateway.Requests, request => Assert.False(request.DryRun));

        // Later cycles ran after the failed one, and the business lane resumed purging.
        Assert.True(gateway.For(OutboxRetentionLane.Location).Count >= 3);
        Assert.True(gateway.For(OutboxRetentionLane.Business).Count >= 2);
    }

    [Fact]
    public async Task Configured_dry_run_job_only_counts_on_every_cycle()
    {
        var options = OutboxRetentionServiceTests.Destructive();
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
        using var host = Host(gateway, options);

        await host.StartAsync(CancellationToken.None);
        await cycles.Task.WaitAsync(Deadline);
        await host.StopAsync(CancellationToken.None);

        Assert.All(gateway.Requests, request => Assert.True(request.DryRun));
    }

    public void Dispose() => _telemetry.Dispose();

    /// <summary>The Worker composition: the real shared scheduler unless a test records it.</summary>
    private OutboxRetentionHostedService Host(
        RecordingPurgeGateway gateway,
        OutboxRetentionOptions options,
        IJobScheduler? scheduler = null)
    {
        var time = new AcceleratedTimeProvider();
        var service = new OutboxRetentionService(
            gateway,
            Options.Create(options),
            _telemetry,
            time,
            new CapturingLogger<OutboxRetentionService>());
        return new OutboxRetentionHostedService(
            scheduler ?? new PeriodicJobScheduler(time, new CapturingLogger<PeriodicJobScheduler>()),
            new OutboxRetentionJob(service, Options.Create(options)),
            Options.Create(options),
            new CapturingLogger<OutboxRetentionHostedService>());
    }
}
