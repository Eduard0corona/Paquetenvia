extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orders.Infrastructure.Lifecycle;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// LIF-001 in the real Worker composition: the Orders job is registered through its module
/// extension, stays idle unless explicitly enabled, and reports through the Worker's ready checks.
/// </summary>
public sealed class ClaimWindowFinalizationWorkerTests
{
    private const string CheckName = "orders_claim_window_finalization";
    private const string UnreachableWorkerDatabase =
        "Host=127.0.0.1;Port=1;Database=lif001;Username=unused;Password=unused;Timeout=3;Pooling=false";

    [Fact]
    public async Task Worker_hosts_the_job_disabled_by_default_and_ready()
    {
        await using var worker = new LifecycleWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        Assert.False(worker.Services.GetRequiredService<IOptions<ClaimWindowFinalizationOptions>>().Value.Enabled);
        Assert.Single(
            worker.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "ClaimWindowFinalizationHostedService");
        var registration = Assert.Single(
            worker.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            candidate => candidate.Name == CheckName);
        Assert.Contains("ready", registration.Tags);

        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == CheckName);
        Assert.Equal(HealthStatus.Healthy, report.Entries[CheckName].Status);
        Assert.Equal("Claim-window finalization is disabled.", report.Entries[CheckName].Description);
    }

    [Fact]
    public void Enabling_without_the_worker_connection_string_fails_at_startup()
    {
        using var worker = new LifecycleWorkerFactory(new()
        {
            ["Orders:ClaimWindowFinalization:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = string.Empty,
        });

        var exception = Assert.ThrowsAny<Exception>(() => worker.Services);
        Assert.Contains(
            "ConnectionStrings:PaqueteriaWorker",
            Flatten(exception),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_job_survives_an_unreachable_database_and_reports_not_ready()
    {
        await using var worker = new LifecycleWorkerFactory(new()
        {
            ["Orders:ClaimWindowFinalization:Enabled"] = "true",
            ["Orders:ClaimWindowFinalization:PollIntervalSeconds"] = "1",
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        var hosted = worker.Services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "ClaimWindowFinalizationHostedService");
        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == CheckName);

        Assert.Equal(HealthStatus.Unhealthy, report.Entries[CheckName].Status);
        Assert.NotNull(hosted.ExecuteTask);
        Assert.False(hosted.ExecuteTask!.IsCompleted, "a failed cycle must not end the schedule");
    }

    private static string Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(Flatten))
            : exception.InnerException is null
                ? exception.Message
                : $"{exception.Message} | {Flatten(exception.InnerException)}";

    private sealed class LifecycleWorkerFactory(Dictionary<string, string?> settings)
        : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }
    }
}
