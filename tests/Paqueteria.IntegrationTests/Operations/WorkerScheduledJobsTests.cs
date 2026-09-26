extern alias WorkerHost;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Scheduling;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// ADR-034 in the real Worker composition: LIF-001 claim-window finalization and OPS-004 outbox
/// retention are independent jobs on the one shared <see cref="IJobScheduler"/>. Each has its own
/// host and interval, and enabling or disabling one never affects the other.
/// </summary>
public sealed class WorkerScheduledJobsTests
{
    private const string FinalizationHost = "ClaimWindowFinalizationHostedService";
    private const string FinalizationJob = "orders.claim-window-finalization";
    private const string RetentionHost = "OutboxRetentionHostedService";
    private const string RetentionJob = "outbox.retention";
    private const string UnreachableWorkerDatabase =
        "Host=127.0.0.1;Port=1;Database=scheduling;Username=unused;Password=unused;Timeout=3;Pooling=false";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Worker_composes_one_shared_scheduler_and_one_host_per_job()
    {
        await using var worker = new SchedulingWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        Assert.IsType<PeriodicJobScheduler>(Assert.Single(worker.Services.GetServices<IJobScheduler>()));
        var hosts = worker.Services.GetServices<IHostedService>().Select(host => host.GetType().Name).ToArray();
        Assert.Single(hosts, name => name == FinalizationHost);
        Assert.Single(hosts, name => name == RetentionHost);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Each_enabled_job_is_scheduled_once_on_its_own_interval(bool finalization, bool retention)
    {
        var scheduler = new RecordingJobScheduler();
        await using var worker = new SchedulingWorkerFactory(
            new()
            {
                ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
                ["Orders:ClaimWindowFinalization:Enabled"] = finalization.ToString(),
                ["Orders:ClaimWindowFinalization:PollIntervalSeconds"] = "45",
                ["OutboxRetention:Enabled"] = retention.ToString(),
                ["OutboxRetention:PollInterval"] = "00:20:00",
            },
            scheduler);

        var hosts = worker.Services.GetServices<IHostedService>().OfType<BackgroundService>().ToArray();
        await SettledAsync(hosts, FinalizationHost, FinalizationJob, finalization, scheduler);
        await SettledAsync(hosts, RetentionHost, RetentionJob, retention, scheduler);

        var expected = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        if (finalization)
        {
            expected[FinalizationJob] = TimeSpan.FromSeconds(45);
        }

        if (retention)
        {
            expected[RetentionJob] = TimeSpan.FromMinutes(20);
        }

        // One scheduler call per enabled job, none for a disabled one: no duplicate loop.
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            scheduler.Jobs.Select(job => job.Name).Order(StringComparer.Ordinal));
        Assert.All(scheduler.Jobs, job => Assert.Equal(expected[job.Name], job.Interval));
    }

    /// <summary>
    /// An enabled host has handed its job to the scheduler; a disabled one has returned without
    /// scheduling anything.
    /// </summary>
    private static Task SettledAsync(
        IEnumerable<BackgroundService> hosts,
        string hostName,
        string jobName,
        bool enabled,
        RecordingJobScheduler scheduler) =>
        enabled
            ? scheduler.Scheduled(jobName).WaitAsync(Deadline)
            : hosts.Single(host => host.GetType().Name == hostName).ExecuteTask!.WaitAsync(Deadline);

    /// <summary>Records each job handed to it and holds its schedule without running a cycle.</summary>
    private sealed class RecordingJobScheduler : IJobScheduler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _scheduled = new(StringComparer.Ordinal);

        public ConcurrentQueue<IScheduledJob> Jobs { get; } = new();

        public Task Scheduled(string jobName) => Signal(jobName).Task;

        public async Task RunAsync(IScheduledJob job, CancellationToken cancellationToken)
        {
            Jobs.Enqueue(job);
            Signal(job.Name).TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private TaskCompletionSource Signal(string jobName) =>
            _scheduled.GetOrAdd(jobName, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class SchedulingWorkerFactory(
        Dictionary<string, string?> settings,
        IJobScheduler? scheduler = null) : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            if (scheduler is not null)
            {
                // Only the shared scheduler is replaced, so what is observed is each host's delegation.
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton<IJobScheduler>(scheduler)));
            }
        }
    }
}
