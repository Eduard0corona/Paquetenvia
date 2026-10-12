extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orders.Application.Orders;
using Orders.Infrastructure.Lifecycle;
using Orders.Infrastructure.Orders;
using Paqueteria.IntegrationTests.Hosting;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10 in the real Worker composition: the Orders auto-close job is registered through its
/// module extension, stays idle unless explicitly enabled, reports through the Worker's ready checks and reaches the
/// ORD-002 transition only through the system actor port.
/// </summary>
public sealed class OrderAutoCloseWorkerTests
{
    private const string CheckName = "orders_auto_close";
    private const string UnreachableWorkerDatabase =
        "Host=127.0.0.1;Port=1;Database=ordautoclose;Username=unused;Password=unused;Timeout=3;Pooling=false";

    [Fact]
    public async Task Worker_hosts_the_job_disabled_by_default_and_ready()
    {
        await using var worker = new AutoCloseWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        Assert.False(worker.Services.GetRequiredService<IOptions<OrderAutoCloseOptions>>().Value.Enabled);
        Assert.Single(
            worker.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "OrderAutoCloseHostedService");
        var registration = Assert.Single(
            worker.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            candidate => candidate.Name == CheckName);
        Assert.Contains("ready", registration.Tags);

        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == CheckName);
        Assert.Equal(HealthStatus.Healthy, report.Entries[CheckName].Status);
        Assert.Equal("Order auto-close is disabled.", report.Entries[CheckName].Description);
    }

    [Fact]
    public async Task Worker_reaches_the_transition_only_as_the_system_actor_with_every_AI04_guard()
    {
        await using var worker = new AutoCloseWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        await using var scope = worker.Services.CreateAsyncScope();
        Assert.IsType<PostgreSqlOrderTransitionService>(
            scope.ServiceProvider.GetRequiredService<IOrderSystemTransitionService>());
        Assert.Null(scope.ServiceProvider.GetService<IOrderTransitionService>());

        // The automatic close decides nothing itself: it must run the same CLOSED guards as the transition defines.
        var guards = worker.Services.GetRequiredService<OrderTransitionGuardRegistry>().Guards
            .Select(guard => guard.Code)
            .ToArray();
        Assert.Equal(new OrderTransitionGuardRegistry().Guards.Select(guard => guard.Code), guards);
        Assert.Contains("no_unresolved_incident", guards);
        Assert.Contains("if_cod_expected_then_cod_status_reconciled", guards);
        Assert.Contains("financial_reconciliation_complete", guards);
        Assert.Contains("claim_window_ends_at_set", guards);
    }

    [Fact]
    public void Enabling_without_the_worker_connection_string_fails_at_startup()
    {
        using var worker = new AutoCloseWorkerFactory(new()
        {
            ["Orders:AutoClose:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = string.Empty,
        });

        var exception = Assert.ThrowsAny<Exception>(() => worker.Services);
        Assert.Contains(
            "Orders:AutoClose:Enabled requires ConnectionStrings:PaqueteriaWorker",
            Flatten(exception),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Out_of_range_options_fail_at_startup()
    {
        using var worker = new AutoCloseWorkerFactory(new()
        {
            ["Orders:AutoClose:BatchSize"] = "1001",
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        var exception = Assert.ThrowsAny<Exception>(() => worker.Services);
        Assert.Contains("Orders:AutoClose", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_job_survives_an_unreachable_database_and_reports_not_ready()
    {
        await using var worker = new AutoCloseWorkerFactory(new()
        {
            ["Orders:AutoClose:Enabled"] = "true",
            ["Orders:AutoClose:PollIntervalSeconds"] = "1",
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        var hosted = worker.Services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Single(service => service.GetType().Name == "OrderAutoCloseHostedService");
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

    private sealed class AutoCloseWorkerFactory(Dictionary<string, string?> settings)
        : StartupFailureSurfacingWebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }
    }
}
