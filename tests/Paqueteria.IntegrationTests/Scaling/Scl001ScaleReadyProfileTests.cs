extern alias WorkerHost;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.DataProtection;
using Testcontainers.PostgreSql;
using DataProtectionOptions = Paqueteria.Infrastructure.DataProtection.DataProtectionOptions;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Scaling;

/// <summary>
/// SCL-001 keeps <c>DataProtection:Provider=Disabled</c> as the single-instance default and adds
/// one versioned, reproducible profile that actually turns the shared PostgreSQL key ring on for
/// both hosts. The profile is a validation profile: an external key-encryption protector is
/// required before productive distributed Data Protection activation.
/// </summary>
[Trait("Category", "PostgreSqlIntegration")]
public sealed class Scl001ScaleReadyProfileTests : IAsyncLifetime
{
    private const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    private const string AppLogin = "paqueteria_scl002_app";
    private const string WorkerLogin = "paqueteria_scl002_worker";

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer _container = null!;
    private string _adminConnectionString = string.Empty;
    private string _appConnectionString = string.Empty;
    private string _workerConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_scl001_profile")
            .WithUsername("postgres")
            .WithPassword(_adminPassword)
            .WithCleanUp(true)
            .Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, _adminConnectionString);

        // The canonical migrator owning this lane is asserted by the SCL-001 contract tests; this
        // fixture only needs the resulting schema to exercise the hosts.
        await ApplyDataProtectionMigrationAsync();
        await ExecuteAsync($$"""
            CREATE ROLE {{AppLogin}} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
              NOREPLICATION NOBYPASSRLS PASSWORD '{{_appPassword}}';
            GRANT paqueteria_app TO {{AppLogin}};
            CREATE ROLE {{WorkerLogin}} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE
              NOREPLICATION NOBYPASSRLS PASSWORD '{{_workerPassword}}';
            GRANT paqueteria_worker TO {{WorkerLogin}};
            """);
        _appConnectionString = RuntimeConnectionString(AppLogin, _appPassword);
        _workerConnectionString = RuntimeConnectionString(WorkerLogin, _workerPassword);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Scale_ready_profile_enables_the_shared_key_ring_for_the_api_and_the_worker()
    {
        await using var api = CreateApi();
        await using var worker = CreateWorker();

        var apiOptions = Options<Program>(api);
        var workerOptions = Options<WorkerProgram>(worker);

        Assert.Equal(DataProtectionProviderKind.PostgreSql, apiOptions.Provider);
        Assert.Equal(DataProtectionProviderKind.PostgreSql, workerOptions.Provider);
        Assert.Equal(DataProtectionRuntimeRoles.Application, apiOptions.RuntimeRole);
        Assert.Equal(DataProtectionRuntimeRoles.Worker, workerOptions.RuntimeRole);

        // One key ring: both hosts have to resolve the same discriminator.
        Assert.Equal(apiOptions.ApplicationName, workerOptions.ApplicationName);
    }

    [Fact]
    public async Task Normal_production_configuration_never_enables_distributed_mode()
    {
        await using var api = CreateApi(environment: "Production");
        await using var worker = CreateWorker(environment: "Production");

        Assert.Equal(DataProtectionProviderKind.Disabled, Options<Program>(api).Provider);
        Assert.Equal(DataProtectionProviderKind.Disabled, Options<WorkerProgram>(worker).Provider);
    }

    [Fact]
    public async Task Two_api_replicas_under_the_profile_share_the_same_data_protection_material()
    {
        await using var replicaA = CreateApi();
        await using var replicaB = CreateApi();

        var protectedByA = Protector<Program>(replicaA).Protect("session-issued-by-replica-a");
        var protectedByB = Protector<Program>(replicaB).Protect("session-issued-by-replica-b");

        Assert.Equal("session-issued-by-replica-a", Protector<Program>(replicaB).Unprotect(protectedByA));
        Assert.Equal("session-issued-by-replica-b", Protector<Program>(replicaA).Unprotect(protectedByB));

        // A Worker replica under the same profile reads what the API replicas protected.
        await using var worker = CreateWorker();
        Assert.Equal(
            "session-issued-by-replica-a",
            Protector<WorkerProgram>(worker).Unprotect(protectedByA));
    }

    [Fact]
    public async Task Readiness_reports_the_shared_key_ring_while_it_is_reachable()
    {
        await using var api = CreateApi();

        var keyRing = await KeyRingReadinessAsync(api);

        Assert.Equal(HealthStatus.Healthy, keyRing.Status);
        Assert.Equal("data_protection_shared_keyring", keyRing.Description);
    }

    [Theory]
    [InlineData("DROP TABLE platform.data_protection_keys")]
    [InlineData("REVOKE SELECT ON platform.data_protection_keys FROM paqueteria_app")]
    [InlineData("REVOKE INSERT ON platform.data_protection_keys FROM paqueteria_app")]
    [InlineData("REVOKE paqueteria_app FROM " + AppLogin)]
    public async Task Readiness_fails_while_the_shared_key_ring_is_unavailable(string breakage)
    {
        var restore = breakage.StartsWith("REVOKE", StringComparison.Ordinal)
            ? breakage.Replace("REVOKE", "GRANT", StringComparison.Ordinal)
                .Replace(" FROM ", " TO ", StringComparison.Ordinal)
            : null;
        // Applied as the deployment superuser: revoking a role membership is not the migrator's
        // privilege, and the case is about the state the replica meets, not about who caused it.
        await ExecuteAsync(breakage + ";");
        try
        {
            await using var api = CreateApi();

            Assert.Equal(HealthStatus.Unhealthy, (await KeyRingReadinessAsync(api)).Status);

            // The endpoint an orchestrator polls before routing traffic to the replica.
            Assert.Equal("unhealthy", await ReadinessAsync(api));
        }
        finally
        {
            if (restore is not null)
            {
                await ExecuteAsync(restore + ";");
            }
            else
            {
                await ApplyDataProtectionMigrationAsync(reset: true);
            }
        }
    }

    private static async Task<HealthReportEntry> KeyRingReadinessAsync(
        WebApplicationFactory<Program> api)
    {
        var report = await api.Services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "data_protection_keyring");
        return Assert.Contains("data_protection_keyring", report.Entries);
    }

    private static async Task<string?> ReadinessAsync(WebApplicationFactory<Program> api)
    {
        using var client = api.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync("/health/ready", cancellation.Token);
        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
        var body = await response.Content.ReadFromJsonAsync<ReadinessResponse>(cancellation.Token);
        return body?.Status;
    }

    private ScaleReadyApiFactory CreateApi(string? environment = null) =>
        new(environment ?? DataProtectionProfiles.ScaleReadyEnvironmentName, _appConnectionString);

    private ScaleReadyWorkerFactory CreateWorker(string? environment = null) =>
        new(environment ?? DataProtectionProfiles.ScaleReadyEnvironmentName, _workerConnectionString);

    private static DataProtectionOptions Options<THost>(WebApplicationFactory<THost> host)
        where THost : class =>
        host.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;

    private static IDataProtector Protector<THost>(WebApplicationFactory<THost> host)
        where THost : class =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("scl001.session");

    private string RuntimeConnectionString(string login, string password) =>
        new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = login,
            Password = password,
            Pooling = true,
            MaxPoolSize = 8,
            ApplicationName = "Paqueteria.SCL001.ScaleReady",
        }.ConnectionString;

    private async Task ApplyDataProtectionMigrationAsync(bool reset = false)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand(
            reset
                ? """
                  SET ROLE paqueteria_migrator;
                  DELETE FROM platform.__ef_migrations_history_platform;
                  """
                : "SET ROLE paqueteria_migrator",
            connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<PlatformDataProtectionDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PlatformDataProtectionDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable(
                    PlatformDataProtectionSchema.MigrationsHistoryTable,
                    PlatformDataProtectionSchema.Schema);
            }).Options;
        await using var context = new PlatformDataProtectionDbContext(options);
        await context.Database.MigrateAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ReadinessResponse(string Status);

    private sealed class ScaleReadyApiFactory(string environment, string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Paqueteria"] = connectionString,
                }));
        }
    }

    private sealed class ScaleReadyWorkerFactory(string environment, string connectionString)
        : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PaqueteriaWorker"] = connectionString,
                }));
        }
    }
}
