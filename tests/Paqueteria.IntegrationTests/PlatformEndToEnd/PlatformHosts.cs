extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.IntegrationTests.Observability;
using Paqueteria.IntegrationTests.Realtime;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>
/// The assembled API and Worker of one test: the real <c>Program</c> and <c>WorkerProgram</c> compositions in the
/// Testing environment against the fixture's PostgreSQL and storage, each with its own runtime login. The API runs on
/// Kestrel over loopback TLS at the origin the web build targets; the Worker runs its real hosted services (proof
/// validation, the dispatch assignment lifecycle and notifications, as the pilot turns them on).
/// </summary>
/// <remarks>
/// Test-host configuration only, through the hosts' existing validated settings:
/// <list type="bullet">
/// <item>module providers set to PostgreSQL with the synthetic geocoder and PII protector, as OPS-001 does;</item>
/// <item>the realtime dispatchers' waits put back to the values of the API's own <c>appsettings.json</c> (the harness
/// would poll every 50 ms);</item>
/// <item>the export off with a synthetic connection string whose ingestion endpoint is the fixture's loopback capture,
/// as the pilot gives both hosts its connection string with the export off (process environment variables set by
/// <see cref="PlatformEndToEndFixture"/>, which both compositions read before they decide what to register);</item>
/// <item>the ASP.NET Core hosting request lines at Information, so every request writes its log lines;</item>
/// <item>whatever the test adds, such as a lower <c>RateLimits</c> permit limit (validated range 1 to 10000).</item>
/// </list>
/// The harness's CORS policy for the web origin exposes <c>Retry-After</c>: the pilot serves the web and the API on
/// one origin, where the browser reads every response header; here they are two origins.
/// </remarks>
internal sealed class PlatformHosts : IAsyncDisposable
{
    /// <summary>The settings the delivery harness replaces that the API's own <c>appsettings.json</c> sets.</summary>
    private static readonly string[] DeployedDispatcherKeys =
    [
        "Realtime:OutboxDispatcher:Business:PollIntervalMilliseconds",
        "Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds",
        "Realtime:OutboxDispatcher:Location:PollIntervalMilliseconds",
        "Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds",
        "Realtime:OutboxDispatcher:StaleRequeueIntervalSeconds",
    ];

    private PlatformHosts(
        RealtimeKestrelWebApplicationFactory api,
        PlatformWorkerFactory worker,
        JsonConsoleCapture apiLogs,
        Uri apiAddress)
    {
        Api = api;
        Worker = worker;
        ApiLogs = apiLogs;
        ApiAddress = apiAddress;
    }

    internal RealtimeKestrelWebApplicationFactory Api { get; }

    internal WebApplicationFactory<WorkerProgram> Worker { get; }

    /// <summary>The API's log lines, formatted by the JSON console formatter as Log Analytics receives them.</summary>
    internal JsonConsoleCapture ApiLogs { get; }

    internal Uri ApiAddress { get; }

    internal static PlatformHosts Start(
        PlatformEndToEndFixture fixture,
        IReadOnlyDictionary<string, string?>? apiSettings = null)
    {
        var logs = new JsonConsoleCapture();
        var settings = ApiSettings(fixture);
        foreach (var (key, value) in apiSettings ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var api = new RealtimeKestrelWebApplicationFactory(
            fixture.Database.ApplicationConnectionString,
            new RealtimeAuthorizationRecorder(),
            port: fixture.ApiPort,
            realtimeConnectionString: fixture.Database.RealtimeConnectionString,
            logProvider: logs,
            allowedOrigin: fixture.WebOrigin,
            enableDispatch: true,
            enableDriverApiCors: true,
            configurationOverrides: settings,
            useHttps: true,
            corsExposedHeaders: ["Retry-After"]);
        PlatformWorkerFactory? worker = null;
        try
        {
            var address = api.Start();
            Assert.Equal(fixture.ApiOrigin, address.GetLeftPart(UriPartial.Authority));
            worker = new PlatformWorkerFactory(WorkerSettings(fixture));
            // Building the host starts it, with every hosted service of the Worker composition.
            _ = worker.Services.GetRequiredService<IHostApplicationLifetime>();
            return new PlatformHosts(api, worker, logs, address);
        }
        catch
        {
            worker?.Dispose();
            api.Dispose();
            throw;
        }
    }

    /// <summary>The pool each host built for <paramref name="role"/>, as SCL-007 composes it.</summary>
    internal int MaximumPoolSize(RuntimeDatabaseRole role) =>
        (role == RuntimeDatabaseRole.Worker ? Worker.Services : Api.Services)
        .GetRequiredKeyedService<RuntimeDataSource>(role)
        .MaximumPoolSize;

    /// <summary>
    /// OBS-003 with the export off: neither host built a tracer or meter provider, and neither runs an OpenTelemetry
    /// hosted service (<c>AddOpenTelemetry</c> registers one), so no exporter exists in the process.
    /// </summary>
    internal void AssertNoTelemetryExporter()
    {
        foreach (var (name, services) in new[] { ("API", Api.Services), ("Worker", Worker.Services) })
        {
            Assert.True(services.GetService<TracerProvider>() is null, $"The {name} built a tracer provider.");
            Assert.True(services.GetService<MeterProvider>() is null, $"The {name} built a meter provider.");
            var exporting = services.GetServices<IHostedService>()
                .Select(service => service.GetType().FullName ?? string.Empty)
                .Where(type => type.StartsWith("OpenTelemetry", StringComparison.Ordinal) ||
                               type.StartsWith("Azure.Monitor", StringComparison.Ordinal))
                .ToArray();
            Assert.True(exporting.Length == 0, $"The {name} runs {string.Join(", ", exporting)}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Worker.DisposeAsync();
        }
        finally
        {
            await Api.DisposeAsync();
        }
    }

    private static Dictionary<string, string?> ApiSettings(PlatformEndToEndFixture fixture)
    {
        var settings = fixture.Storage.CreateProofStorageConfiguration(fixture.StorageOrigin)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        settings["Locations:Provider"] = "PostgreSql";
        settings["Locations:GeocodingProvider"] = "Mock";
        settings["Locations:PiiProtector"] = "Mock";
        settings["Locations:CommandTimeoutSeconds"] = "30";
        settings["Pricing:Provider"] = "PostgreSql";
        settings["Pricing:CommandTimeoutSeconds"] = "30";
        settings["Pricing:QuoteLifetimeMinutes"] = "30";
        settings["Orders:CommandTimeoutSeconds"] = "30";
        settings["Drivers:Provider"] = "PostgreSql";
        settings["Dispatch:Provider"] = "PostgreSql";
        settings["Realtime:OutboxDispatcher:WorkerId"] = "ops005-api";
        // TRK-002-AUTO-LINK: in Testing the link may be built on the web's loopback origin, so "Ver enlace" gives a
        // link the browser opens as it is.
        settings["PublicTracking:PublicBaseUrl"] = fixture.WebOrigin;
        settings["PublicTracking:AllowedOrigins:0"] = fixture.WebOrigin;
        settings["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information";
        foreach (var vehicle in new[] { "MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER" })
        {
            settings[$"Drivers:Eligibility:RequiredDocumentTypesByVehicleType:{vehicle}:0"] = "IDENTITY";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumPackageCount"] = "100";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumTotalWeightGrams"] = "100000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumSinglePackageWeightGrams"] = "10000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumLengthMillimeters"] = "2000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumWidthMillimeters"] = "2000";
            settings[$"Drivers:Eligibility:VehicleCapacity:{vehicle}:MaximumHeightMillimeters"] = "2000";
        }

        settings["Drivers:Eligibility:NonExpiringDocumentTypes:0"] = "IDENTITY";
        var deployed = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                RepositoryRootLocator.Find(AppContext.BaseDirectory),
                "src",
                "Paqueteria.Api",
                "appsettings.json"))
            .Build();
        foreach (var key in DeployedDispatcherKeys)
        {
            settings[key] = deployed[key] ?? throw new InvalidOperationException($"appsettings.json has no {key}.");
        }

        return settings;
    }

    private static Dictionary<string, string?> WorkerSettings(PlatformEndToEndFixture fixture)
    {
        var settings = fixture.Storage.CreateProofStorageConfiguration(fixture.StorageOrigin)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        settings["ConnectionStrings:PaqueteriaWorker"] = fixture.Database.WorkerConnectionString;
        settings["Drivers:Provider"] = "PostgreSql";
        settings["Dispatch:Provider"] = "PostgreSql";
        settings["Dispatch:AssignmentLifecycle:Provider"] = "PostgreSql";
        settings["Dispatch:AssignmentLifecycle:WorkerId"] = "ops005-worker";
        settings["Notifications:Provider"] = "PostgreSql";
        settings["Notifications:WorkerId"] = "ops005-worker";
        return settings;
    }

    private sealed class PlatformWorkerFactory(IReadOnlyDictionary<string, string?> settings)
        : WebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Host settings reach the composition itself (providers are read while composing), not only the host.
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }
    }
}
