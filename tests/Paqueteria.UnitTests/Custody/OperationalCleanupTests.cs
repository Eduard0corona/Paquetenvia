using System.Diagnostics.Metrics;
using Custody.Application.Cleanup;
using Custody.Infrastructure;
using Custody.Infrastructure.Cleanup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.UnitTests.Custody;

/// <summary>OPS-003 Worker jobs: bounded cycles, bounded options, registration and PII-free metrics.</summary>
public sealed class OperationalCleanupTests
{
    private const string WorkerConnection = "Host=127.0.0.1;Database=ops003;Username=worker;Password=unused";

    [Fact]
    public async Task A_short_batch_ends_the_cycle_as_drained()
    {
        var calls = new List<int>();
        var counts = new Queue<int>([10, 10, 4, 10]);

        var result = await OperationalCleanupCycle.RunAsync(
            new OperationalCleanupPolicy(10, 100, 50, dryRun: false),
            (size, _) =>
            {
                calls.Add(size);
                return Task.FromResult(counts.Dequeue());
            },
            CancellationToken.None);

        Assert.Equal(new OperationalCleanupCycleResult(Batches: 3, Affected: 24, Drained: true), result);
        Assert.Equal([10, 10, 10], calls);
    }

    [Fact]
    public async Task A_cycle_never_exceeds_its_maximum_batches()
    {
        var calls = 0;

        var result = await OperationalCleanupCycle.RunAsync(
            new OperationalCleanupPolicy(5, 100, 3, dryRun: false),
            (size, _) =>
            {
                calls++;
                return Task.FromResult(size);
            },
            CancellationToken.None);

        Assert.Equal(new OperationalCleanupCycleResult(Batches: 3, Affected: 15, Drained: false), result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_dry_run_is_a_single_call_even_when_the_batch_is_full()
    {
        var calls = 0;

        var result = await OperationalCleanupCycle.RunAsync(
            new OperationalCleanupPolicy(5, 100, 10, dryRun: true),
            (size, _) =>
            {
                calls++;
                return Task.FromResult(size);
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(new OperationalCleanupCycleResult(Batches: 1, Affected: 5, Drained: false), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task An_impossible_count_fails_closed(int count)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OperationalCleanupCycle.RunAsync(
                new OperationalCleanupPolicy(10, 100, 5, dryRun: false),
                (_, _) => Task.FromResult(count),
                CancellationToken.None));

        Assert.Equal("OPS003_CLEANUP_COUNT_OUT_OF_RANGE", exception.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_cycle_before_the_next_batch()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OperationalCleanupCycle.RunAsync(
                new OperationalCleanupPolicy(5, 100, 10, dryRun: false),
                (size, _) =>
                {
                    calls++;
                    cancellation.Cancel();
                    return Task.FromResult(size);
                },
                cancellation.Token));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0, 100, 1)]
    [InlineData(101, 100, 1)]
    [InlineData(1, 100, 0)]
    [InlineData(1, 100, 101)]
    [InlineData(1, 0, 1)]
    public void Policy_rejects_unbounded_batches(int batchSize, int maximumBatchSize, int maxBatches) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OperationalCleanupPolicy(batchSize, maximumBatchSize, maxBatches, dryRun: false));

    [Fact]
    public void Defaults_are_bounded_disabled_and_the_key_purge_starts_in_dry_run()
    {
        using var provider = Provider([]);
        var options = provider.GetRequiredService<IOptions<OperationalCleanupOptions>>().Value;

        Assert.False(options.AnyEnabled);
        Assert.False(options.IdempotencyKeys.Enabled);
        Assert.True(options.IdempotencyKeys.DryRun);
        Assert.Equal(TimeSpan.FromMinutes(15), options.IdempotencyKeys.PollInterval);
        Assert.Equal(1_000, options.IdempotencyKeys.BatchSize);
        Assert.Equal(10, options.IdempotencyKeys.MaxBatchesPerCycle);
        Assert.False(options.ProofUploadSessions.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(1), options.ProofUploadSessions.PollInterval);
        Assert.Equal(500, options.ProofUploadSessions.BatchSize);
        Assert.Equal(10, options.ProofUploadSessions.MaxBatchesPerCycle);
        Assert.Equal(30, options.CommandTimeoutSeconds);
        Assert.False(options.ProofUploadSessions.ToPolicy().DryRun);
        Assert.Equal(5_000, OperationalCleanupLimits.MaximumIdempotencyBatchSize);
        Assert.Equal(1_000, OperationalCleanupLimits.MaximumSessionBatchSize);
    }

    [Theory]
    [InlineData("CommandTimeoutSeconds", "0")]
    [InlineData("CommandTimeoutSeconds", "301")]
    [InlineData("IdempotencyKeys:PollIntervalSeconds", "0")]
    [InlineData("IdempotencyKeys:PollIntervalSeconds", "3601")]
    [InlineData("IdempotencyKeys:BatchSize", "0")]
    [InlineData("IdempotencyKeys:BatchSize", "5001")]
    [InlineData("IdempotencyKeys:MaxBatchesPerCycle", "0")]
    [InlineData("IdempotencyKeys:MaxBatchesPerCycle", "101")]
    [InlineData("ProofUploadSessions:PollIntervalSeconds", "0")]
    [InlineData("ProofUploadSessions:PollIntervalSeconds", "3601")]
    [InlineData("ProofUploadSessions:BatchSize", "0")]
    [InlineData("ProofUploadSessions:BatchSize", "1001")]
    [InlineData("ProofUploadSessions:MaxBatchesPerCycle", "0")]
    [InlineData("ProofUploadSessions:MaxBatchesPerCycle", "101")]
    public void Out_of_range_settings_fail_validation(string key, string value)
    {
        using var provider = Provider(new() { [$"OperationalCleanup:{key}"] = value });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<OperationalCleanupOptions>>().Value);
    }

    [Theory]
    [InlineData("IdempotencyKeys")]
    [InlineData("ProofUploadSessions")]
    public void Enabling_either_job_requires_the_worker_connection_string(string job)
    {
        using var withoutConnection = Provider(new() { [$"OperationalCleanup:{job}:Enabled"] = "true" });
        var exception = Assert.Throws<OptionsValidationException>(() =>
            withoutConnection.GetRequiredService<IOptions<OperationalCleanupOptions>>().Value);
        Assert.Contains("ConnectionStrings:PaqueteriaWorker", exception.Message, StringComparison.Ordinal);

        using var withConnection = Provider(new()
        {
            [$"OperationalCleanup:{job}:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = WorkerConnection,
        });
        Assert.True(withConnection.GetRequiredService<IOptions<OperationalCleanupOptions>>().Value.AnyEnabled);
    }

    [Fact]
    public async Task Registration_adds_one_hosted_service_two_jobs_on_the_scheduler_port_and_a_ready_check()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCustodyOperationalCleanup(new ConfigurationBuilder().Build());

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(OperationalCleanupHostedService));
        await using var provider = services.BuildServiceProvider();
        Assert.IsType<PeriodicJobScheduler>(provider.GetRequiredService<IJobScheduler>());
        var keys = provider.GetRequiredService<IdempotencyKeyPurgeJob>();
        var sessions = provider.GetRequiredService<ProofUploadSessionExpiryJob>();
        Assert.Equal("operations.cleanup.idempotency-keys", keys.Name);
        Assert.Equal(TimeSpan.FromMinutes(15), keys.Interval);
        Assert.Equal("operations.cleanup.proof-upload-sessions", sessions.Name);
        Assert.Equal(TimeSpan.FromMinutes(1), sessions.Interval);
        var check = Assert.Single(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            registration => registration.Name == "custody_operational_cleanup");
        Assert.Contains("ready", check.Tags);
    }

    [Fact]
    public async Task Disabled_jobs_never_reach_the_scheduler_and_report_healthy()
    {
        var scheduler = new RecordingScheduler();
        await using var provider = Provider([], scheduler);

        await RunHostedServiceAsync(provider);
        var health = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Empty(scheduler.Jobs);
        Assert.Equal(HealthStatus.Healthy, health.Entries["custody_operational_cleanup"].Status);
    }

    [Theory]
    [InlineData(true, false, new[] { "operations.cleanup.idempotency-keys" })]
    [InlineData(false, true, new[] { "operations.cleanup.proof-upload-sessions" })]
    [InlineData(true, true, new[] { "operations.cleanup.idempotency-keys", "operations.cleanup.proof-upload-sessions" })]
    public async Task Only_enabled_jobs_are_handed_to_the_scheduler(bool keys, bool sessions, string[] expected)
    {
        var scheduler = new RecordingScheduler();
        await using var provider = Provider(
            new()
            {
                ["OperationalCleanup:IdempotencyKeys:Enabled"] = keys.ToString(),
                ["OperationalCleanup:IdempotencyKeys:PollIntervalSeconds"] = "120",
                ["OperationalCleanup:ProofUploadSessions:Enabled"] = sessions.ToString(),
                ["OperationalCleanup:ProofUploadSessions:PollIntervalSeconds"] = "30",
                ["ConnectionStrings:PaqueteriaWorker"] = WorkerConnection,
            },
            scheduler);

        await RunHostedServiceAsync(provider);

        Assert.Equal(expected, scheduler.Jobs.Select(job => job.Name));
        Assert.All(scheduler.Jobs, job => Assert.Equal(
            job is IdempotencyKeyPurgeJob ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30),
            job.Interval));
    }

    [Fact]
    public async Task The_key_job_passes_its_mode_batch_size_and_the_current_instant_to_the_gateway()
    {
        var gateway = new ScriptedGateway { IdempotencyCounts = new Queue<int>([3, 3, 1]) };
        await using var provider = Provider(
            new()
            {
                ["OperationalCleanup:IdempotencyKeys:DryRun"] = "false",
                ["OperationalCleanup:IdempotencyKeys:BatchSize"] = "3",
            },
            gateway: gateway);

        var before = DateTimeOffset.UtcNow;
        await provider.GetRequiredService<IdempotencyKeyPurgeJob>().RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, gateway.IdempotencyCalls.Count);
        Assert.All(gateway.IdempotencyCalls, call =>
        {
            Assert.Equal(3, call.BatchSize);
            Assert.False(call.DryRun);
            Assert.InRange(call.ExpiredBefore, before, DateTimeOffset.UtcNow);
        });
    }

    [Fact]
    public async Task The_key_job_dry_run_is_one_non_mutating_call_by_default()
    {
        var gateway = new ScriptedGateway { IdempotencyCounts = new Queue<int>([1_000]) };
        await using var provider = Provider([], gateway: gateway);

        await provider.GetRequiredService<IdempotencyKeyPurgeJob>().RunOnceAsync(CancellationToken.None);

        var call = Assert.Single(gateway.IdempotencyCalls);
        Assert.True(call.DryRun);
    }

    [Fact]
    public async Task The_session_job_runs_bounded_batches_and_surfaces_failures_to_the_scheduler()
    {
        var gateway = new ScriptedGateway { SessionCounts = new Queue<int>([2, 0]) };
        await using var provider = Provider(
            new()
            {
                ["OperationalCleanup:ProofUploadSessions:BatchSize"] = "2",
                ["OperationalCleanup:ProofUploadSessions:MaxBatchesPerCycle"] = "4",
            },
            gateway: gateway);
        var job = provider.GetRequiredService<ProofUploadSessionExpiryJob>();

        await job.RunOnceAsync(CancellationToken.None);
        Assert.Equal([2, 2], gateway.SessionCalls);

        gateway.SessionCounts.Enqueue(-5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Metrics_carry_only_bounded_job_mode_and_outcome_dimensions()
    {
        var gateway = new ScriptedGateway
        {
            IdempotencyCounts = new Queue<int>([2]),
            SessionCounts = new Queue<int>([7, 1]),
        };
        await using var provider = Provider(
            new() { ["OperationalCleanup:ProofUploadSessions:BatchSize"] = "7" },
            gateway: gateway);
        var measurements = new List<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OperationalCleanupTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        listener.Start();

        await provider.GetRequiredService<IdempotencyKeyPurgeJob>().RunOnceAsync(CancellationToken.None);
        await provider.GetRequiredService<ProofUploadSessionExpiryJob>().RunOnceAsync(CancellationToken.None);

        Assert.Contains(measurements, m => m.Instrument == "operations.cleanup.cycles" &&
            (string?)m.Tags["job"] == "idempotency_keys" && (string?)m.Tags["mode"] == "dry_run");
        Assert.Contains(measurements, m => m.Instrument == "operations.cleanup.rows" &&
            (string?)m.Tags["job"] == "proof_upload_sessions" && m.Value == 8);
        var allowed = new HashSet<string>(["job", "mode", "outcome", "error_class"], StringComparer.Ordinal);
        Assert.All(measurements, m =>
        {
            Assert.All(m.Tags.Keys, key => Assert.Contains(key, allowed));
            Assert.All(m.Tags.Values, value => Assert.IsType<string>(value));
            Assert.All(m.Tags.Values.Cast<string>(), value => Assert.False(Guid.TryParse(value, out _)));
        });
    }

    private static ServiceProvider Provider(
        Dictionary<string, string?> settings,
        IJobScheduler? scheduler = null,
        IOperationalCleanupGateway? gateway = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (scheduler is not null)
        {
            services.AddSingleton(scheduler);
        }

        services.AddCustodyOperationalCleanup(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        if (gateway is not null)
        {
            services.AddSingleton(gateway);
        }

        return services.BuildServiceProvider();
    }

    private static async Task RunHostedServiceAsync(ServiceProvider provider)
    {
        var hosted = provider.GetServices<IHostedService>().OfType<OperationalCleanupHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await hosted.ExecuteTask!;
        await hosted.StopAsync(CancellationToken.None);
    }

    private sealed class ScriptedGateway : IOperationalCleanupGateway
    {
        public Queue<int> IdempotencyCounts { get; init; } = new();

        public Queue<int> SessionCounts { get; init; } = new();

        public List<(DateTimeOffset ExpiredBefore, int BatchSize, bool DryRun)> IdempotencyCalls { get; } = [];

        public List<int> SessionCalls { get; } = [];

        public Task<int> PurgeExpiredIdempotencyKeysAsync(
            DateTimeOffset expiredBefore,
            int batchSize,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            IdempotencyCalls.Add((expiredBefore, batchSize, dryRun));
            return Task.FromResult(IdempotencyCounts.Dequeue());
        }

        public Task<int> ExpireProofUploadSessionsAsync(int batchSize, CancellationToken cancellationToken)
        {
            SessionCalls.Add(batchSize);
            return Task.FromResult(SessionCounts.Dequeue());
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
