extern alias WorkerHost;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Custody.Infrastructure.Cleanup;
using Custody.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;
using Testcontainers.PostgreSql;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// OPS-003 in the real Worker composition: both cleanup jobs are registered through the Custody
/// module, idle unless enabled, scheduled on the shared <see cref="IJobScheduler"/> next to LIF-001
/// and OPS-004, and report through the Worker's ready checks.
/// </summary>
public sealed class Ops003OperationalCleanupWorkerTests
{
    private const string CheckName = "custody_operational_cleanup";
    private const string HostName = "OperationalCleanupHostedService";
    private const string UnreachableWorkerDatabase =
        "Host=127.0.0.1;Port=1;Database=ops003;Username=unused;Password=unused;Timeout=3;Pooling=false";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Worker_hosts_both_jobs_disabled_and_in_dry_run_by_default_and_ready()
    {
        await using var worker = new CleanupWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        var options = worker.Services.GetRequiredService<IOptions<OperationalCleanupOptions>>().Value;
        Assert.False(options.AnyEnabled);
        Assert.True(options.IdempotencyKeys.DryRun);
        Assert.Single(worker.Services.GetServices<IHostedService>(), service => service.GetType().Name == HostName);
        var registration = Assert.Single(
            worker.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            candidate => candidate.Name == CheckName);
        Assert.Contains("ready", registration.Tags);

        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == CheckName);
        Assert.Equal(HealthStatus.Healthy, report.Entries[CheckName].Status);
        Assert.Equal("Operational cleanup is disabled.", report.Entries[CheckName].Description);
    }

    [Fact]
    public void Enabling_without_the_worker_connection_string_fails_at_startup()
    {
        using var worker = new CleanupWorkerFactory(new()
        {
            ["OperationalCleanup:ProofUploadSessions:Enabled"] = "true",
            ["ConnectionStrings:PaqueteriaWorker"] = string.Empty,
        });

        var exception = Assert.ThrowsAny<Exception>(() => worker.Services);
        Assert.Contains("ConnectionStrings:PaqueteriaWorker", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_jobs_survive_an_unreachable_database_and_report_not_ready()
    {
        await using var worker = new CleanupWorkerFactory(new()
        {
            ["OperationalCleanup:IdempotencyKeys:Enabled"] = "true",
            ["OperationalCleanup:IdempotencyKeys:PollIntervalSeconds"] = "1",
            ["OperationalCleanup:ProofUploadSessions:Enabled"] = "true",
            ["OperationalCleanup:ProofUploadSessions:PollIntervalSeconds"] = "1",
            ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
        });

        var hosted = worker.Services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Single(service => service.GetType().Name == HostName);
        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == CheckName);

        Assert.Equal(HealthStatus.Unhealthy, report.Entries[CheckName].Status);
        Assert.NotNull(hosted.ExecuteTask);
        Assert.False(hosted.ExecuteTask!.IsCompleted, "a failed cycle must not end the schedule");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Each_enabled_cleanup_job_shares_the_scheduler_with_the_other_worker_jobs(bool keys, bool sessions)
    {
        var scheduler = new RecordingJobScheduler();
        await using var worker = new CleanupWorkerFactory(
            new()
            {
                ["ConnectionStrings:PaqueteriaWorker"] = UnreachableWorkerDatabase,
                ["OperationalCleanup:IdempotencyKeys:Enabled"] = keys.ToString(),
                ["OperationalCleanup:IdempotencyKeys:PollIntervalSeconds"] = "600",
                ["OperationalCleanup:ProofUploadSessions:Enabled"] = sessions.ToString(),
                ["OperationalCleanup:ProofUploadSessions:PollIntervalSeconds"] = "90",
                ["Orders:ClaimWindowFinalization:Enabled"] = "true",
                ["Orders:ClaimWindowFinalization:PollIntervalSeconds"] = "45",
            },
            scheduler);

        _ = worker.Services;
        var expected = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
        {
            ["orders.claim-window-finalization"] = TimeSpan.FromSeconds(45),
        };
        if (keys)
        {
            expected[IdempotencyKeyPurgeJob.JobName] = TimeSpan.FromMinutes(10);
        }

        if (sessions)
        {
            expected[ProofUploadSessionExpiryJob.JobName] = TimeSpan.FromSeconds(90);
        }

        foreach (var job in expected.Keys)
        {
            await scheduler.Scheduled(job).WaitAsync(Deadline);
        }

        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            scheduler.Jobs.Select(job => job.Name).Order(StringComparer.Ordinal));
        Assert.All(scheduler.Jobs, job => Assert.Equal(expected[job.Name], job.Interval));
    }

    internal static string Flatten(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(Flatten))
            : exception.InnerException is null
                ? exception.Message
                : $"{exception.Message} | {Flatten(exception.InnerException)}";

    internal sealed class CleanupWorkerFactory(
        Dictionary<string, string?> settings,
        IJobScheduler? scheduler = null) : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            if (scheduler is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton(scheduler)));
            }
        }
    }

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
}

/// <summary>
/// OPS-003 end to end: the real Worker, with its Worker login only, cleans a real PostgreSQL through
/// the OPS-003-CLEANUP-ROLE functions installed by the Custody lane, and leaves everything the 72-hour
/// floor or the session lifecycle protects exactly as it was.
/// </summary>
[Trait("Category", "PostgreSqlIntegration")]
public sealed class Ops003OperationalCleanupPostgreSqlWorkerTests : IAsyncLifetime
{
    private const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly Guid _organization = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _order = Guid.NewGuid();
    private PostgreSqlContainer _container = null!;
    private string _adminConnectionString = string.Empty;
    private string _workerConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_ops003")
            .WithUsername("postgres")
            .WithPassword(_adminPassword)
            .WithCleanUp(true)
            .Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, _adminConnectionString);
        await ApplyCustodyLaneAsync();
        await ExecuteAsync($$"""
            CREATE ROLE paqueteria_ops003_worker LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
              NOREPLICATION NOBYPASSRLS PASSWORD '{{_workerPassword}}';
            GRANT paqueteria_worker TO paqueteria_ops003_worker;
            """);
        _workerConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = "paqueteria_ops003_worker",
            Password = _workerPassword,
            Pooling = true,
            MaxPoolSize = 4,
            ApplicationName = "Paqueteria.OPS003.IntegrationTests",
        }.ConnectionString;
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task The_real_worker_purges_only_keys_past_the_floor_and_expires_only_elapsed_sessions()
    {
        await InsertKeyAsync("old-expired", "-80 hours", "-1 hour");
        await InsertKeyAsync("inside-floor-expired", "-71 hours -59 minutes", "-1 hour");
        await InsertKeyAsync("old-live", "-100 hours", "+1 hour");
        var elapsed = await InsertSessionAsync("UPLOADED", "-1 minute");
        var live = await InsertSessionAsync("READY", "+30 minutes");
        var consumed = await InsertSessionAsync("CONSUMED", "-1 hour");

        await using var worker = new Ops003OperationalCleanupWorkerTests.CleanupWorkerFactory(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = _workerConnectionString,
            ["OperationalCleanup:IdempotencyKeys:Enabled"] = "true",
            ["OperationalCleanup:IdempotencyKeys:DryRun"] = "false",
            ["OperationalCleanup:IdempotencyKeys:PollIntervalSeconds"] = "1",
            ["OperationalCleanup:ProofUploadSessions:Enabled"] = "true",
            ["OperationalCleanup:ProofUploadSessions:PollIntervalSeconds"] = "1",
        });
        _ = worker.Services;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline &&
               ((await KeysAsync()).Contains("old-expired", StringComparer.Ordinal) ||
                await StatusAsync(elapsed) != "EXPIRED"))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        Assert.Equal(["inside-floor-expired", "old-live"], await KeysAsync());
        Assert.Equal("EXPIRED", await StatusAsync(elapsed));
        Assert.Equal("READY", await StatusAsync(live));
        Assert.Equal("CONSUMED", await StatusAsync(consumed));

        var report = await worker.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(candidate => candidate.Name == "custody_operational_cleanup");
        Assert.Equal(HealthStatus.Healthy, report.Entries["custody_operational_cleanup"].Status);
    }

    private async Task ApplyCustodyLaneAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<CustodyDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(CustodyDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_custody", "platform");
            }).Options;
        await using var context = new CustodyDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync();
    }

    private async Task SeedAsync()
    {
        var city = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var quote = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type)
              VALUES (@org,'OPS-003 Synthetic Organization','OPS-003 Synthetic','BUSINESS');
            INSERT INTO identity.users(id,identity_subject) VALUES (@user,@subject);
            INSERT INTO locations.cities(id,state_code,name,timezone) VALUES (@city,'SI','OPS-003 Synthetic City','America/Mazatlan');
            INSERT INTO locations.locations(id,owner_org_id,city_id,point,address_ciphertext,address_summary,pii_key_version) VALUES
              (@origin,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.40,24.80),4326),decode('00','hex'),'Synthetic origin','test-v1'),
              (@destination,@org,@city,public.ST_SetSRID(public.ST_MakePoint(-107.39,24.81),4326),decode('01','hex'),'Synthetic destination','test-v1');
            INSERT INTO pricing.quotes(
              id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,pricing_policy_version,
              request_snapshot_redacted,package_snapshot,breakdown,input_hash,status,expires_at)
            VALUES (@quote,@org,@city,@origin,@destination,'SAME_DAY','OCCASIONAL',false,
              1000,0,0,1000,1000,'MXN','ops003-policy-v1','{"city":"synthetic"}',
              '[{"description":"synthetic package","weight_grams":500,"declared_value_cents":1000}]','{}',
              decode(repeat('00',32),'hex'),'ACTIVE',clock_timestamp()+interval '1 day');
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,city_id,origin_location_id,destination_location_id,service_type,pricing_tier,
              consolidated_route,payer_type,status,subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,
              currency,pricing_policy_version,package_snapshot,cod_expected_cents,version)
            SELECT @order,'OPS003-' || replace(@order::text,'-',''),q.id,q.owner_org_id,q.city_id,q.origin_location_id,
              q.destination_location_id,q.service_type,q.pricing_tier,q.consolidated_route,'SENDER','AT_PICKUP',q.subtotal_cents,
              q.discount_cents,q.tax_cents,q.total_cents,q.minimum_total_cents_snapshot,q.currency,q.pricing_policy_version,
              q.package_snapshot,0,1
            FROM pricing.quotes q WHERE q.id=@quote;
            """,
            connection);
        command.Parameters.AddWithValue("org", _organization);
        command.Parameters.AddWithValue("user", _user);
        command.Parameters.AddWithValue("subject", $"oidc|ops003|{_user:N}");
        command.Parameters.AddWithValue("city", city);
        command.Parameters.AddWithValue("origin", origin);
        command.Parameters.AddWithValue("destination", destination);
        command.Parameters.AddWithValue("quote", quote);
        command.Parameters.AddWithValue("order", _order);
        await command.ExecuteNonQueryAsync();
    }

    private Task InsertKeyAsync(string key, string created, string expires) =>
        ExecuteAsync(
            """
            INSERT INTO platform.idempotency_keys(owner_org_id,scope,idempotency_key,request_hash,created_at,expires_at)
            VALUES (@org,'OPS003:WORKER',@key,decode('00','hex'),clock_timestamp()+@created::interval,clock_timestamp()+@expires::interval);
            """,
            ("org", _organization), ("key", key), ("created", created), ("expires", expires));

    private async Task<Guid> InsertSessionAsync(string status, string expires)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO custody.proof_upload_sessions(
              id,order_id,owner_org_id,requested_by,object_key_quarantine,expected_content_type,maximum_bytes,status,expires_at)
            VALUES (@id,@order,@org,@user,@quarantine,'image/jpeg',1024,@status,clock_timestamp()+@expires::interval);
            """,
            ("id", id), ("order", _order), ("org", _organization), ("user", _user),
            ("quarantine", $"quarantine/ops003/{id:N}"), ("status", status), ("expires", expires));
        return id;
    }

    private async Task<string[]> KeysAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT idempotency_key FROM platform.idempotency_keys WHERE scope='OPS003:WORKER' ORDER BY idempotency_key",
            connection);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }

        return [.. keys];
    }

    private async Task<string> StatusAsync(Guid session)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM custody.proof_upload_sessions WHERE id=@id", connection);
        command.Parameters.AddWithValue("id", session);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
