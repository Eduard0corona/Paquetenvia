using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orders.Application.Lifecycle;
using Orders.Infrastructure;
using Orders.Infrastructure.Lifecycle;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.UnitTests.Orders;

public sealed class ClaimWindowFinalizationTests
{
    [Fact]
    public async Task A_zero_count_batch_ends_the_cycle_as_drained()
    {
        var finalizer = new ScriptedFinalizer(7, 3, 0, 99);

        var result = await new ClaimWindowFinalizationCycle(finalizer)
            .RunAsync(new ClaimWindowFinalizationPolicy(10, 50), CancellationToken.None);

        Assert.Equal(new ClaimWindowFinalizationCycleResult(Batches: 3, Finalized: 10, Drained: true), result);
        Assert.Equal([10, 10, 10], finalizer.RequestedBatchSizes);
    }

    [Fact]
    public async Task A_cycle_never_exceeds_its_maximum_batches()
    {
        var finalizer = new ScriptedFinalizer(Enumerable.Repeat(5, 20).ToArray());

        var result = await new ClaimWindowFinalizationCycle(finalizer)
            .RunAsync(new ClaimWindowFinalizationPolicy(5, 3), CancellationToken.None);

        Assert.Equal(new ClaimWindowFinalizationCycleResult(Batches: 3, Finalized: 15, Drained: false), result);
        Assert.Equal(3, finalizer.RequestedBatchSizes.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task An_impossible_finalized_count_fails_closed(int count)
    {
        var cycle = new ClaimWindowFinalizationCycle(new ScriptedFinalizer(count));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cycle.RunAsync(new ClaimWindowFinalizationPolicy(10, 5), CancellationToken.None));

        Assert.Equal("LIF001_FINALIZED_COUNT_OUT_OF_RANGE", exception.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_cycle_before_the_next_batch()
    {
        using var cancellation = new CancellationTokenSource();
        var finalizer = new ScriptedFinalizer(5, 5, 5) { OnCall = cancellation.Cancel };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ClaimWindowFinalizationCycle(finalizer)
                .RunAsync(new ClaimWindowFinalizationPolicy(5, 10), cancellation.Token));

        Assert.Single(finalizer.RequestedBatchSizes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1_001, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Policy_rejects_unbounded_batches(int batchSize, int maxBatches) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaimWindowFinalizationPolicy(batchSize, maxBatches));

    [Fact]
    public void Defaults_are_bounded_and_disabled()
    {
        using var provider = Provider([]);
        var options = provider.GetRequiredService<IOptions<ClaimWindowFinalizationOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(60), options.PollInterval);
        Assert.Equal(new ClaimWindowFinalizationPolicy(100, 10), options.ToPolicy());
    }

    [Theory]
    [InlineData("PollIntervalSeconds", "0")]
    [InlineData("PollIntervalSeconds", "3601")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("MaxBatchesPerCycle", "0")]
    [InlineData("MaxBatchesPerCycle", "101")]
    public void Out_of_range_settings_fail_validation(string key, string value)
    {
        using var provider = Provider(new() { [$"Orders:ClaimWindowFinalization:{key}"] = value });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ClaimWindowFinalizationOptions>>().Value);
    }

    [Fact]
    public void Enabling_requires_the_worker_connection_string()
    {
        using var withoutConnection = Provider(new() { ["Orders:ClaimWindowFinalization:Enabled"] = "true" });
        var exception = Assert.Throws<OptionsValidationException>(() =>
            withoutConnection.GetRequiredService<IOptions<ClaimWindowFinalizationOptions>>().Value);
        Assert.Contains("ConnectionStrings:PaqueteriaWorker", exception.Message, StringComparison.Ordinal);

        using var withConnection = Provider(new()
        {
            ["Orders:ClaimWindowFinalization:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = "Host=127.0.0.1;Database=lif001;Username=worker;Password=unused",
        });
        Assert.True(withConnection.GetRequiredService<IOptions<ClaimWindowFinalizationOptions>>().Value.Enabled);
    }

    [Fact]
    public async Task Registration_adds_one_hosted_job_behind_the_scheduler_port_and_a_ready_check()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrdersClaimWindowFinalization(new ConfigurationBuilder().Build());

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ClaimWindowFinalizationHostedService));
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<PeriodicJobScheduler>(provider.GetRequiredService<IJobScheduler>());
        var job = provider.GetRequiredService<ClaimWindowFinalizationJob>();
        Assert.Equal("orders.claim-window-finalization", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(60), job.Interval);
        var check = Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            registration => registration.Name == "orders_claim_window_finalization");
        Assert.Contains("ready", check.Tags);
    }

    [Fact]
    public async Task Disabled_job_never_reaches_the_scheduler_and_reports_healthy()
    {
        var scheduler = new RecordingScheduler();
        await using var provider = Provider([], scheduler);

        await RunHostedServiceAsync(provider);
        var health = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Empty(scheduler.Jobs);
        Assert.Equal(HealthStatus.Healthy, health.Entries["orders_claim_window_finalization"].Status);
    }

    [Fact]
    public async Task Enabled_job_is_handed_to_the_scheduler_with_its_bounded_interval()
    {
        var scheduler = new RecordingScheduler();
        await using var provider = Provider(
            new()
            {
                ["Orders:ClaimWindowFinalization:Enabled"] = "true",
                ["Orders:ClaimWindowFinalization:PollIntervalSeconds"] = "15",
                ["ConnectionStrings:PaqueteriaWorker"] = "Host=127.0.0.1;Database=lif001;Username=worker;Password=unused",
            },
            scheduler);

        await RunHostedServiceAsync(provider);

        var job = Assert.Single(scheduler.Jobs);
        Assert.Equal("orders.claim-window-finalization", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(15), job.Interval);
    }

    [Fact]
    public async Task One_job_run_executes_one_bounded_cycle_and_surfaces_failures_to_the_scheduler()
    {
        var finalizer = new ScriptedFinalizer(2, 0);
        await using var provider = Provider(
            new()
            {
                ["Orders:ClaimWindowFinalization:BatchSize"] = "2",
                ["Orders:ClaimWindowFinalization:MaxBatchesPerCycle"] = "4",
            },
            finalizer: finalizer);
        var job = provider.GetRequiredService<ClaimWindowFinalizationJob>();

        await job.RunOnceAsync(CancellationToken.None);
        Assert.Equal([2, 2], finalizer.RequestedBatchSizes);

        finalizer.Enqueue(-5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunOnceAsync(CancellationToken.None));
    }

    private static ServiceProvider Provider(
        Dictionary<string, string?> settings,
        IJobScheduler? scheduler = null,
        IExpiredClaimWindowFinalizer? finalizer = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (scheduler is not null)
        {
            services.AddSingleton(scheduler);
        }

        services.AddOrdersClaimWindowFinalization(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (finalizer is not null)
        {
            services.AddSingleton(finalizer);
        }

        return services.BuildServiceProvider();
    }

    private static async Task RunHostedServiceAsync(ServiceProvider provider)
    {
        var hosted = provider.GetServices<IHostedService>().OfType<ClaimWindowFinalizationHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await hosted.ExecuteTask!;
        await hosted.StopAsync(CancellationToken.None);
    }

    private sealed class ScriptedFinalizer(params int[] counts) : IExpiredClaimWindowFinalizer
    {
        private readonly Queue<int> _counts = new(counts);

        public List<int> RequestedBatchSizes { get; } = [];
        public Action? OnCall { get; init; }

        public void Enqueue(int count) => _counts.Enqueue(count);

        public Task<int> FinalizeExpiredBatchAsync(int batchSize, CancellationToken cancellationToken)
        {
            RequestedBatchSizes.Add(batchSize);
            OnCall?.Invoke();
            return Task.FromResult(_counts.Dequeue());
        }
    }

    private sealed class RecordingScheduler : IJobScheduler
    {
        public List<IScheduledJob> Jobs { get; } = [];

        public Task RunAsync(IScheduledJob job, CancellationToken cancellationToken)
        {
            Jobs.Add(job);
            return Task.CompletedTask;
        }
    }
}
