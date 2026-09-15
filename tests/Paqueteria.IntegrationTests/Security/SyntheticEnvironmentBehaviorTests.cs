extern alias WorkerHost;

using System.Diagnostics;
using System.Net;
using Custody.Infrastructure.ProofStorage;
using Identity.Application.Authentication;
using Locations.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Database.Baseline;
using Realtime.Application.Configuration;
using Realtime.Infrastructure;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Security;

[Collection(SyntheticEnvironmentPostgreSqlCollection.Name)]
[Trait("Category", "PublicTrackingPostgreSql")]
public sealed class SyntheticEnvironmentBehaviorTests(
    PostgreSqlSecurityWebApplicationFactory database,
    DevSyntheticSeedPostgreSqlFactory seedDatabase)
{
    private const string ExternalRealtimeOrigin = "https://runtime.synthetic.local";

    [Fact]
    public async Task DevSynthetic_seed_executes_real_command_without_login_or_tracking_side_effects()
    {
        var before = await ReadSeedEvidenceAsync();
        Assert.Equal(0, before.LocalRuntimeLoginCount);
        Assert.Equal(0, before.OrganizationCount);
        Assert.Equal(0, before.UserCount);
        Assert.Equal(0, before.MembershipCount);
        Assert.Equal(0, before.CityCount);
        Assert.Equal(0, before.ServiceAreaCount);
        Assert.Equal(0, before.OperatingZoneCount);
        Assert.Equal(0, before.TariffCount);
        Assert.Equal(0, before.DriverProfileCount);
        Assert.Equal(0, before.DriverServiceAreaCount);
        Assert.Equal(0, before.DriverDocumentCount);

        var result = await RunDevSeedAsync();

        Assert.True(
            result.ExitCode == 0,
            $"DevSeed exited with {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");
        Assert.Contains(
            "DevSynthetic prerequisites ready: synthetic identities=3, organizations=2.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardError));

        var after = await ReadSeedEvidenceAsync();
        Assert.Equal(before.LoginCount, after.LoginCount);
        Assert.Equal(0, after.LocalRuntimeLoginCount);
        Assert.Equal(before.TrackingTokenCount, after.TrackingTokenCount);
        Assert.Equal(before.TrackingAuditCount, after.TrackingAuditCount);
        Assert.Equal(2, after.OrganizationCount);
        Assert.Equal(3, after.UserCount);
        Assert.Equal(3, after.MembershipCount);
        Assert.Equal(1, after.CityCount);
        Assert.Equal(1, after.ServiceAreaCount);
        Assert.Equal(1, after.OperatingZoneCount);
        Assert.Equal(1, after.TariffCount);
        Assert.Equal(2, after.DriverProfileCount);
        Assert.Equal(2, after.DriverServiceAreaCount);
        Assert.Equal(2, after.DriverDocumentCount);
    }

    [Fact]
    public async Task Api_starts_through_real_DevSynthetic_composition_without_Development_or_Testing_surfaces()
    {
        using var environment = new ProcessEnvironmentScope(ApiEnvironment());
        using var factory = new DevSyntheticApiWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await AssertHealthyAsync(client, "/health/live");
        await AssertHealthyAsync(client, "/health/ready");

        var hostEnvironment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        AssertDevSyntheticEnvironment(hostEnvironment);
        AssertNoDevelopmentConfiguration(factory.Services, hostEnvironment);

        var authentication = factory.Services
            .GetRequiredService<IOptions<IdentityAuthenticationOptions>>()
            .Value;
        Assert.Equal(IdentityProviderKind.Mock, authentication.Provider);
        var locations = factory.Services.GetRequiredService<IOptions<LocationsOptions>>().Value;
        Assert.Equal(GeocodingProviderKind.Mock, locations.GeocodingProvider);
        Assert.Equal(LocationPiiProtectorKind.Mock, locations.PiiProtector);
        var realtime = factory.Services.GetRequiredService<IOptions<RealtimeOptions>>().Value;
        Assert.Equal(RealtimeProviderKind.Disabled, realtime.Provider);
        Assert.Equal(RealtimeBackplaneKind.InProcess, realtime.Backplane);
        Assert.Equal([ExternalRealtimeOrigin], realtime.AllowedOrigins);

        AssertDevelopmentAndTestingRoutesAbsent(factory.Services);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            "/openapi/v1.json",
            CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            "/__tests/tracking/synthetic",
            CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Worker_starts_through_real_DevSynthetic_composition_with_external_configuration()
    {
        using var environment = new ProcessEnvironmentScope(WorkerEnvironment());
        using var factory = new DevSyntheticWorkerWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await AssertHealthyAsync(client, "/health/live");
        await AssertHealthyAsync(client, "/health/ready");

        var hostEnvironment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        AssertDevSyntheticEnvironment(hostEnvironment);
        AssertNoDevelopmentConfiguration(factory.Services, hostEnvironment);
        var proofStorage = factory.Services.GetRequiredService<IOptions<ProofStorageOptions>>().Value;
        Assert.Equal(ProofStorageProvider.Disabled, proofStorage.Provider);
        Assert.Equal(ProofThreatScannerProvider.Disabled, proofStorage.ThreatScanner);

        AssertDevelopmentAndTestingRoutesAbsent(factory.Services);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            "/openapi/v1.json",
            CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(
            "/__tests/security/authenticated",
            CancellationToken.None)).StatusCode);
    }

    private Dictionary<string, string?> ApiEnvironment() => new(StringComparer.Ordinal)
    {
        ["DOTNET_ENVIRONMENT"] = SyntheticEnvironmentPolicy.EnvironmentName,
        ["ASPNETCORE_ENVIRONMENT"] = SyntheticEnvironmentPolicy.EnvironmentName,
        [SyntheticEnvironmentPolicy.DeploymentClassVariable] = SyntheticEnvironmentPolicy.DeploymentClass,
        ["ConnectionStrings__Paqueteria"] = database.ApplicationConnectionString,
        ["Authentication__Provider"] = "Mock",
        ["IdentityBootstrap__Provider"] = "Disabled",
        ["Locations__Provider"] = "Disabled",
        ["Locations__GeocodingProvider"] = "Mock",
        ["Locations__PiiProtector"] = "Mock",
        ["Realtime__Provider"] = "Disabled",
        ["Realtime__Backplane"] = "InProcess",
        ["Realtime__AllowedOrigins__0"] = ExternalRealtimeOrigin,
        ["ProofStorage__Provider"] = "Disabled",
        ["ProofStorage__ThreatScanner"] = "Disabled",
    };

    private Dictionary<string, string?> WorkerEnvironment() => new(StringComparer.Ordinal)
    {
        ["DOTNET_ENVIRONMENT"] = SyntheticEnvironmentPolicy.EnvironmentName,
        ["ASPNETCORE_ENVIRONMENT"] = SyntheticEnvironmentPolicy.EnvironmentName,
        [SyntheticEnvironmentPolicy.DeploymentClassVariable] = SyntheticEnvironmentPolicy.DeploymentClass,
        ["ConnectionStrings__Paqueteria"] = database.WorkerConnectionString,
        ["ProofStorage__Provider"] = "Disabled",
        ["ProofStorage__ThreatScanner"] = "Disabled",
        ["Notifications__Provider"] = "Disabled",
    };

    private static void AssertDevSyntheticEnvironment(IHostEnvironment environment)
    {
        Assert.Equal(SyntheticEnvironmentPolicy.EnvironmentName, environment.EnvironmentName);
        Assert.False(environment.IsDevelopment());
        Assert.False(environment.IsEnvironment("Testing"));
        Assert.True(SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName));
        Assert.Equal(
            SyntheticEnvironmentPolicy.EnvironmentName,
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"));
        Assert.Equal(
            SyntheticEnvironmentPolicy.EnvironmentName,
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
    }

    private static void AssertNoDevelopmentConfiguration(
        IServiceProvider services,
        IHostEnvironment environment)
    {
        Assert.False(File.Exists(Path.Combine(
            environment.ContentRootPath,
            "appsettings.DevSynthetic.json")));
        var configuration = Assert.IsAssignableFrom<IConfigurationRoot>(
            services.GetRequiredService<IConfiguration>());
        var jsonPaths = configuration.Providers
            .OfType<JsonConfigurationProvider>()
            .Select(static provider => provider.Source.Path ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(
            jsonPaths,
            static path => path.EndsWith("appsettings.Development.json", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertDevelopmentAndTestingRoutesAbsent(IServiceProvider services)
    {
        var patterns = services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(
            patterns,
            static pattern => pattern.TrimStart('/').StartsWith("__tests/", StringComparison.Ordinal));
        Assert.DoesNotContain(patterns, static pattern => pattern.Contains("openapi", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertHealthyAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<SeedEvidence> ReadSeedEvidenceAsync()
    {
        await using var connection = new NpgsqlConnection(seedDatabase.AdminConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            """
            SELECT
              (SELECT count(*)::integer FROM pg_catalog.pg_roles WHERE rolcanlogin),
              (SELECT count(*)::integer FROM pg_catalog.pg_roles
                WHERE rolname IN ('paqueteria_local_app','paqueteria_local_worker') AND rolcanlogin),
              (SELECT count(*)::integer FROM orders.public_tracking_tokens),
              (SELECT count(*)::integer FROM platform.audit_logs WHERE action LIKE 'TRACKING_TOKEN_%'),
              (SELECT count(*)::integer FROM organizations.organizations
                WHERE id IN ('11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333')),
              (SELECT count(*)::integer FROM identity.users
                WHERE (id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa20' AND identity_subject='local-subject-dispatcher-mfa')
                   OR (id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa11' AND identity_subject='mock-subject-active-driver')
                   OR (id='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa13' AND identity_subject='mock-subject-external-driver')),
              (SELECT count(*)::integer FROM organizations.organization_memberships
                WHERE id IN ('dddddddd-dddd-dddd-dddd-dddddddddd20','dddddddd-dddd-dddd-dddd-dddddddddd11','dddddddd-dddd-dddd-dddd-dddddddddd13')),
              (SELECT count(*)::integer FROM locations.cities WHERE id='44444444-4444-4444-4444-444444444441'),
              (SELECT count(*)::integer FROM locations.service_areas WHERE id='44444444-4444-4444-4444-444444444442'),
              (SELECT count(*)::integer FROM locations.operating_zones WHERE id='44444444-4444-4444-4444-444444444443'),
              (SELECT count(*)::integer FROM pricing.tariff_rules WHERE id='44444444-4444-4444-4444-444444444444'),
              (SELECT count(*)::integer FROM drivers.driver_profiles
                WHERE id IN ('55555555-5555-5555-5555-555555555551','55555555-5555-5555-5555-555555555553')),
              (SELECT count(*)::integer FROM drivers.driver_service_areas
                WHERE driver_id IN ('55555555-5555-5555-5555-555555555551','55555555-5555-5555-5555-555555555553')),
              (SELECT count(*)::integer FROM drivers.driver_documents
                WHERE id IN ('55555555-5555-5555-5555-555555555552','55555555-5555-5555-5555-555555555554'));
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return new SeedEvidence(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13));
    }

    private async Task<ProcessResult> RunDevSeedAsync()
    {
        var repositoryRoot = RepositoryRootLocator.Find(AppContext.BaseDirectory);
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))?.Name
            ?? throw new InvalidOperationException("The test build configuration could not be resolved.");
        var executable = Path.Combine(
            repositoryRoot,
            "tools",
            "Paqueteria.DevSeed",
            "bin",
            configuration,
            "net10.0",
            OperatingSystem.IsWindows() ? "Paqueteria.DevSeed.exe" : "Paqueteria.DevSeed");
        Assert.True(File.Exists(executable), $"DevSeed apphost was not built: {executable}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            },
        };
        process.StartInfo.ArgumentList.Add("seed");
        foreach (var name in new[]
                 {
                     "DOTNET_ENVIRONMENT",
                     "ASPNETCORE_ENVIRONMENT",
                     SyntheticEnvironmentPolicy.DeploymentClassVariable,
                     "PAQUETERIA_SYNTHETIC_SEED_ENABLED",
                     "PAQUETERIA_LOCAL_DEV_SEED_ENABLED",
                     "PAQUETERIA_LOCAL_ADMIN_CONNECTION",
                     "PAQUETERIA_LOCAL_APP_CONNECTION",
                     "PAQUETERIA_LOCAL_APP_PASSWORD",
                     "PAQUETERIA_LOCAL_WORKER_PASSWORD",
                 })
        {
            process.StartInfo.Environment.Remove(name);
        }
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = SyntheticEnvironmentPolicy.EnvironmentName;
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = SyntheticEnvironmentPolicy.EnvironmentName;
        process.StartInfo.Environment[SyntheticEnvironmentPolicy.DeploymentClassVariable] =
            SyntheticEnvironmentPolicy.DeploymentClass;
        process.StartInfo.Environment["PAQUETERIA_SYNTHETIC_SEED_ENABLED"] = "true";
        process.StartInfo.Environment["PAQUETERIA_LOCAL_ADMIN_CONNECTION"] = seedDatabase.AdminConnectionString;

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record SeedEvidence(
        int LoginCount,
        int LocalRuntimeLoginCount,
        int TrackingTokenCount,
        int TrackingAuditCount,
        int OrganizationCount,
        int UserCount,
        int MembershipCount,
        int CityCount,
        int ServiceAreaCount,
        int OperatingZoneCount,
        int TariffCount,
        int DriverProfileCount,
        int DriverServiceAreaCount,
        int DriverDocumentCount);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SyntheticEnvironmentPostgreSqlCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>,
      ICollectionFixture<DevSyntheticSeedPostgreSqlFactory>
{
    public const string Name = "SyntheticEnvironmentPostgreSql";
}

public sealed class DevSyntheticSeedPostgreSqlFactory : PostgreSqlSecurityWebApplicationFactory
{
    public DevSyntheticSeedPostgreSqlFactory()
        : base(seedSyntheticData: false)
    {
    }
}

internal sealed class DevSyntheticApiWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment(SyntheticEnvironmentPolicy.EnvironmentName);
}

internal sealed class DevSyntheticWorkerWebApplicationFactory : WebApplicationFactory<WorkerProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment(SyntheticEnvironmentPolicy.EnvironmentName);
}

internal sealed class ProcessEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> originalValues = new(StringComparer.Ordinal);

    public ProcessEnvironmentScope(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            originalValues[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
