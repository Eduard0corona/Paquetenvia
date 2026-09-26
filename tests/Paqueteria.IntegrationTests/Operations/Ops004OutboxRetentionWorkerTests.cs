extern alias WorkerHost;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Database.Outbox.Retention;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// OPS-004 Worker composition: the retention job is registered, inert and in dry-run by default,
/// and the Worker refuses to start with configuration the purge functions would reject.
/// </summary>
public sealed class Ops004OutboxRetentionWorkerTests
{
    [Fact]
    public async Task Worker_registers_the_retention_job_inert_and_in_dry_run_by_default()
    {
        await using var worker = new RetentionWorkerFactory([]);
        using var client = worker.CreateClient();

        var options = worker.Services.GetRequiredService<IOptions<OutboxRetentionOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.True(options.DryRun);
        Assert.Equal(TimeSpan.FromMinutes(15), options.PollInterval);
        Assert.Equal(TimeSpan.FromDays(7), options.Business.ProcessedRetention);
        Assert.Equal(TimeSpan.FromDays(30), options.Business.DeadRetention);
        Assert.Equal(1_000, options.Business.BatchSize);
        Assert.Equal(10, options.Business.MaxBatchesPerRun);
        Assert.Equal(TimeSpan.FromDays(1), options.Location.ProcessedRetention);
        Assert.Equal(TimeSpan.FromDays(7), options.Location.DeadRetention);
        Assert.Equal(5_000, options.Location.BatchSize);
        Assert.Equal(10, options.Location.MaxBatchesPerRun);
        Assert.Empty(OutboxRetentionOptionsValidator.Errors(options));
        Assert.Single(
            worker.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "OutboxRetentionHostedService");

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.True(live.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("OutboxRetention:Business:ProcessedRetention", "12:00:00")]
    [InlineData("OutboxRetention:Business:DeadRetention", "6.00:00:00")]
    [InlineData("OutboxRetention:Location:ProcessedRetention", "00:30:00")]
    [InlineData("OutboxRetention:Location:DeadRetention", "12:00:00")]
    [InlineData("OutboxRetention:Location:BatchSize", "50001")]
    [InlineData("OutboxRetention:Business:MaxBatchesPerRun", "0")]
    [InlineData("OutboxRetention:PollInterval", "00:00:05")]
    [InlineData("OutboxRetention:PollInterval", "01:00:01")]
    public async Task Worker_refuses_to_start_with_unsafe_retention_configuration(string key, string value)
    {
        await using var worker = new RetentionWorkerFactory(new() { [key] = value });

        Assert.ThrowsAny<Exception>(() => worker.CreateClient());

        Assert.Contains(worker.ValidationFailures, failure => failure.Message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_retention_requires_the_worker_connection_string()
    {
        await using var worker = new RetentionWorkerFactory(new()
        {
            ["OutboxRetention:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = string.Empty,
        });

        Assert.ThrowsAny<Exception>(() => worker.CreateClient());

        Assert.Contains(
            worker.ValidationFailures,
            failure => failure.Message.Contains("ConnectionStrings:PaqueteriaWorker", StringComparison.Ordinal));
    }

    private sealed class RetentionWorkerFactory(Dictionary<string, string?> settings)
        : WebApplicationFactory<WorkerProgram>
    {
        /// <summary>
        /// Retention option failures, recorded where options are validated and before the Worker
        /// disposes its failed host. <see cref="WebApplicationFactory{TEntryPoint}"/> itself may
        /// surface an <see cref="ObjectDisposedException"/> instead of the original error when the
        /// Worker's own <c>Run</c> disposes the host before the factory reads its services.
        /// </summary>
        public ConcurrentQueue<OptionsValidationException> ValidationFailures { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
                services.AddTransient<IOptionsFactory<OutboxRetentionOptions>>(provider => new RecordingOptionsFactory(
                    new OptionsFactory<OutboxRetentionOptions>(
                        provider.GetServices<IConfigureOptions<OutboxRetentionOptions>>(),
                        provider.GetServices<IPostConfigureOptions<OutboxRetentionOptions>>(),
                        provider.GetServices<IValidateOptions<OutboxRetentionOptions>>()),
                    ValidationFailures)));
        }
    }

    private sealed class RecordingOptionsFactory(
        IOptionsFactory<OutboxRetentionOptions> inner,
        ConcurrentQueue<OptionsValidationException> failures) : IOptionsFactory<OutboxRetentionOptions>
    {
        public OutboxRetentionOptions Create(string name)
        {
            try
            {
                return inner.Create(name);
            }
            catch (OptionsValidationException exception)
            {
                failures.Enqueue(exception);
                throw;
            }
        }
    }
}
