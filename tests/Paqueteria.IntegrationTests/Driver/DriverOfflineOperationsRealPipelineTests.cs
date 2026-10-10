using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.S3.Model;
using Custody.Application.ProofUploads;
using Custody.Infrastructure;
using Identity.Infrastructure.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Npgsql;
using Paqueteria.IntegrationTests.Custody;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Driver;

[Collection(DriverOfflineOperationsRealCollection.Name)]
public sealed class DriverOfflineOperationsRealPipelineTests(
    PostgreSqlSecurityWebApplicationFactory database,
    CorsCapableS3Fixture storage)
{
    private static readonly byte[] SyntheticPng =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] SyntheticJpeg =
        [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46];

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Real_postgres_s3_api_worker_and_browser_complete_the_offline_chain()
    {
        var assignment = await database.CreateDriverAudienceOutboxScenarioAsync(
            available: false);
        var nextPort = DriverStopsNextServer.ReservePort();
        var nextOrigin = $"http://127.0.0.1:{nextPort}";
        // The deployable artifact requires an HTTPS API origin and HTTPS signed
        // upload URLs. The API listens on loopback TLS; the storage container
        // speaks plain HTTP, so browser uploads are presigned for a loopback
        // TLS tunnel that relays them, byte for byte, to the real bucket.
        await using var storageTls = LoopbackTlsTunnel.Start(
            new Uri(storage.Endpoint),
            RealtimeKestrelWebApplicationFactory.LoopbackCertificate);
        var storageOrigin = storageTls.Origin.GetLeftPart(UriPartial.Authority);
        var proofSettings = storage.CreateProofStorageConfiguration(storageOrigin);
        await using var api = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            new RealtimeAuthorizationRecorder(),
            workerConnectionString: database.WorkerConnectionString,
            allowedOrigin: nextOrigin,
            enableDispatch: true,
            enableDriverApiCors: true,
            configurationOverrides: proofSettings,
            useHttps: true);
        var apiAddress = api.Start();
        await using var web = await DriverStopsNextServer.StartProductionAsync(
            apiAddress.GetLeftPart(UriPartial.Authority),
            nextPort,
            connectSources: storageOrigin);
        await using var worker = BuildProofWorkerProvider(
            database.WorkerConnectionString,
            proofSettings);

        var allowedOrigin = new Uri(nextOrigin);
        await storage.ConfigureBrowserCorsAsync(allowedOrigin);
        try
        {
            CorsCapableS3Fixture.AssertExactBrowserCorsPolicy(
                Assert.IsType<CORSConfiguration>(
                    await storage.ReadBrowserCorsAsync()),
                allowedOrigin);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = true });
            await using var context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    // Scoped to this context only: the API and the storage
                    // tunnel listen on loopback TLS with a certificate
                    // generated for this test process.
                    IgnoreHTTPSErrors = true,
                });
            var page = await context.NewPageAsync();
            var requests = new ConcurrentQueue<BrowserRequestObservation>();
            var responses = new ConcurrentQueue<BrowserResponseObservation>();
            var requestsById =
                new ConcurrentDictionary<string, BrowserRequestObservation>();
            var cdp = await context.NewCDPSessionAsync(page);
            cdp.Event("Network.requestWillBeSent").OnEvent += (_, payload) =>
                ObserveCdpRequest(payload, requestsById, requests);
            cdp.Event("Network.responseReceived").OnEvent += (_, payload) =>
                ObserveCdpResponse(payload, requestsById, responses);
            await cdp.SendAsync("Network.enable");
            await page.AddInitScriptAsync(
                $$"""
            window.__paquetenviaDriverSession = {
              organizationId: "{{PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}}",
              cacheNamespace: "opaque-drv002-real-pipeline",
              getAccessToken: () => "{{MockIdentityProfiles.ActiveDriver}}",
            };
            """);

            var orderId = await ReadAssignedOrderIdAsync();
            await page.GotoAsync(
                new Uri(web.BaseAddress, $"/driver/stops/{orderId:D}").AbsoluteUri);
            await page.GetByText(
                    PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync();
            await EnsureServiceWorkerControlAsync(page);
            await context.SetOfflineAsync(true);
            try
            {
                await AssertOfflineOriginsAsync(
                    page,
                    new Uri(apiAddress, "/health/live").AbsoluteUri,
                    new Uri(web.BaseAddress, "/not-a-driver-shell").AbsoluteUri,
                    storageOrigin,
                    storage.Endpoint);
                await page.GetByRole(
                        AriaRole.Button,
                        new() { Name = "Llegué a recolección" })
                    .ClickAsync();
                await EnqueueProofAsync(
                    page,
                    "Confirmar recolección",
                    "pickup-real-synthetic.png");
                await page.GetByRole(
                        AriaRole.Button,
                        new() { Name = "Iniciar traslado" })
                    .ClickAsync();
                await page.GetByRole(
                        AriaRole.Button,
                        new() { Name = "Llegué al destino" })
                    .ClickAsync();
                await EnqueueProofAsync(
                    page,
                    "Confirmar entrega",
                    "delivery-real-synthetic.jpg",
                    "image/jpeg",
                    SyntheticJpeg);
                await WaitForOperationCountAsync(page, 5);
                await WaitForProofCountAsync(page, 2);
                var beforeReload = await ReadOperationIdentityAsync(page);
                await page.ReloadAsync();
                await page.GetByText("5 acciones en el dispositivo", new() { Exact = false })
                    .WaitForAsync();
                Assert.Equal(beforeReload, await ReadOperationIdentityAsync(page));
                await WaitForProofCountAsync(page, 2);
            }
            finally
            {
                await context.SetOfflineAsync(false);
            }
            await page.WaitForFunctionAsync("() => navigator.onLine === true");

            await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Sincronizar ahora" })
                .ClickAsync();
            await WaitForOperationStatusAsync(page, "WAITING_VALIDATION");

            using var workerCancellation = new CancellationTokenSource();
            var workerLoop = ProcessProofsUntilCancelledAsync(
                worker,
                workerCancellation.Token);
            try
            {
                await page.GetByRole(
                        AriaRole.Button,
                        new() { Name = "Sincronizar ahora" })
                    .ClickAsync();
                await WaitForOperationCountAsync(page, 0);
                await WaitForProofCountAsync(page, 0);
            }
            finally
            {
                workerCancellation.Cancel();
                await workerLoop;
            }

            var evidence = await ReadPipelineEvidenceAsync(
                orderId,
                assignment.AggregateVersion);
            Assert.Equal("DELIVERED", evidence.Status);
            Assert.Equal(2, evidence.Proofs);
            Assert.Equal(2, evidence.ConsumedSessions);
            Assert.Equal(5, evidence.TransitionEvents);
            Assert.Equal(9, evidence.IdempotencyRows);
            Assert.Equal(0, evidence.DuplicateIdempotencyRows);
            Assert.Equal(
                [
                    "AT_PICKUP",
                "PICKED_UP",
                "IN_TRANSIT",
                "DELIVERING",
                "DELIVERED",
            ],
                evidence.TargetStatuses);
            Assert.Equal(2, evidence.ClientCaptureTimes.Count);
            Assert.All(
                evidence.ClientCaptureTimes,
                timestamp => Assert.Equal(TimeSpan.Zero, timestamp.Offset));

            var apiOrigin = apiAddress.GetLeftPart(UriPartial.Authority);
            var successfulPreflights = responses
                .Where(response =>
                    response.TargetOrigin == storageOrigin
                    && response.Method == "OPTIONS"
                    && response.Status is >= 200 and < 300)
                .ToArray();
            var successfulUploads = responses
                .Where(response =>
                    response.TargetOrigin == storageOrigin
                    && response.Method == "PUT"
                    && response.Status is >= 200 and < 300)
                .ToArray();
            Assert.NotEmpty(successfulPreflights);
            Assert.NotEmpty(successfulUploads);
            Assert.All(
                successfulPreflights,
                response => Assert.Equal(nextOrigin, response.BrowserOrigin));
            Assert.Contains(
                responses,
                response =>
                    response.TargetOrigin == apiOrigin
                    && response.Status is >= 200 and < 300);
            Assert.Contains(
                requests,
                request =>
                    request.TargetOrigin == storageOrigin
                    && request.Method == "OPTIONS"
                    && request.BrowserOrigin == nextOrigin);
            Console.WriteLine(
                "Browser CORS evidence: OPTIONS 2xx={0}; PUT 2xx={1}",
                successfulPreflights.Length,
                successfulUploads.Length);
        }
        finally
        {
            await storage.ClearBrowserCorsAsync();
        }

        Assert.Null(await storage.ReadBrowserCorsAsync());
    }

    private async Task<Guid> ReadAssignedOrderIdAsync()
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id
            FROM orders.orders
            WHERE public_id='ORD_abcdefghijklmnopqrstuv'
              AND status='ASSIGNED'
            """,
            connection);
        return (Guid)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Assigned synthetic order missing."));
    }

    private async Task<PipelineEvidence> ReadPipelineEvidenceAsync(
        Guid orderId,
        int initialVersion)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
              o.status,
              (SELECT count(*)::integer FROM custody.proofs p WHERE p.order_id=o.id),
              (SELECT count(*)::integer FROM custody.proof_upload_sessions s
               WHERE s.order_id=o.id AND s.status='CONSUMED'),
              (SELECT count(*)::integer FROM orders.order_events e
               WHERE e.order_id=o.id AND e.aggregate_version > @initial_version
                 AND e.event_type='ORDER_STATUS_CHANGED'),
              (SELECT count(*)::integer FROM platform.idempotency_keys i
               WHERE i.owner_org_id=o.owner_org_id AND i.idempotency_key LIKE 'drv2-%'),
              (SELECT count(*)::integer - count(DISTINCT i.idempotency_key)::integer
               FROM platform.idempotency_keys i
               WHERE i.owner_org_id=o.owner_org_id AND i.idempotency_key LIKE 'drv2-%'),
              ARRAY(
                SELECT e.payload->>'new_status'
                FROM orders.order_events e
                WHERE e.order_id=o.id AND e.aggregate_version > @initial_version
                  AND e.event_type='ORDER_STATUS_CHANGED'
                ORDER BY e.aggregate_version),
              ARRAY(
                SELECT p.captured_at
                FROM custody.proofs p
                WHERE p.order_id=o.id
                ORDER BY p.captured_at)
            FROM orders.orders o
            WHERE o.id=@order_id
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("initial_version", initialVersion);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PipelineEvidence(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetFieldValue<string[]>(6),
            reader.GetFieldValue<DateTimeOffset[]>(7));
    }

    private static async Task ProcessProofsUntilCancelledAsync(
        ServiceProvider worker,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = worker.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<IProofValidationProcessor>()
                    .ProcessAvailableAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (NullReferenceException)
            {
                // The existing S3 adapter returns a null object collection for
                // an empty MinIO listing. DRV-002 owns no backend fix; retry
                // once the browser has uploaded quarantine evidence.
            }
            await Task.Delay(100, cancellationToken)
                .ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private static ServiceProvider BuildProofWorkerProvider(
        string connectionString,
        IReadOnlyDictionary<string, string?> proofSettings)
    {
        var settings = new Dictionary<string, string?>(proofSettings)
        {
            ["ConnectionStrings:Paqueteria"] = connectionString,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddCustodyInfrastructure(configuration, new TestHostEnvironment());
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>
    /// UI-001 phase 3: the step names the action, the photo is shown in
    /// "Vista previa de la foto" and only "Usar esta foto" queues it.
    /// </summary>
    private static async Task EnqueueProofAsync(
        IPage page,
        string action,
        string fileName,
        string mimeType = "image/png",
        byte[]? bytes = null)
    {
        await page.GetByText(action, new() { Exact = true }).First.WaitForAsync();
        await page.Locator("input[type=file]").SetInputFilesAsync(
            new FilePayload
            {
                Name = fileName,
                MimeType = mimeType,
                Buffer = bytes ?? SyntheticPng,
            });
        await page.GetByRole(
                AriaRole.Heading,
                new() { Name = "Vista previa de la foto" })
            .WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Usar esta foto" })
            .ClickAsync();
        await page.WaitForTimeoutAsync(100);
    }

    private static async Task EnsureServiceWorkerControlAsync(IPage page)
    {
        await page.EvaluateAsync(
            "() => navigator.serviceWorker.ready.then(() => undefined)");
        await page.ReloadAsync();
        await page.WaitForFunctionAsync(
            "() => navigator.serviceWorker.controller !== null");
    }

    private static async Task WaitForOperationCountAsync(IPage page, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            var actual = await page.EvaluateAsync<int>(
                """
                () => new Promise((resolve, reject) => {
                  const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
                  open.onerror = () => reject(open.error);
                  open.onsuccess = () => {
                    const db = open.result;
                    const request = db.transaction("operations", "readonly")
                      .objectStore("operations").count();
                    request.onsuccess = () => {
                      db.close();
                      resolve(request.result);
                    };
                    request.onerror = () => {
                      db.close();
                      reject(request.error);
                    };
                  };
                })
                """);
            if (actual == expected)
            {
                return;
            }
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        var diagnostics = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const db = open.result;
                const request = db.transaction("operations", "readonly")
                  .objectStore("operations").getAll();
                request.onsuccess = () => {
                  db.close();
                  resolve(JSON.stringify(request.result));
                };
                request.onerror = () => {
                  db.close();
                  reject(request.error);
                };
              };
            })
            """);
        throw new TimeoutException(
            $"Expected {expected} queued driver operations. Actual: {diagnostics}");
    }

    private static async Task WaitForProofCountAsync(IPage page, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            var actual = await ReadStoreCountAsync(page, "proof_blobs");
            if (actual == expected)
            {
                return;
            }
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException($"Expected {expected} queued proof blobs.");
    }

    /// <summary>
    /// Polls IndexedDB until an operation reaches the status. Playwright's
    /// <c>WaitForFunctionAsync</c> treats a returned <c>Promise</c> as truthy
    /// on its first evaluation, so asynchronous predicates must be re-run here.
    /// </summary>
    private static async Task WaitForOperationStatusAsync(
        IPage page,
        string expectedStatus)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            var reached = await page.EvaluateAsync<bool>(
                """
                expected => new Promise((resolve, reject) => {
                  const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
                  open.onerror = () => reject(open.error);
                  open.onsuccess = () => {
                    const db = open.result;
                    const request = db.transaction("operations", "readonly")
                      .objectStore("operations").getAll();
                    request.onsuccess = () => {
                      db.close();
                      resolve(request.result.some(value => value.status === expected));
                    };
                    request.onerror = () => {
                      db.close();
                      reject(request.error);
                    };
                  };
                })
                """,
                expectedStatus);
            if (reached)
            {
                return;
            }
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException(
            $"No queued driver operation reached {expectedStatus}.");
    }

    private static Task<int> ReadStoreCountAsync(IPage page, string store) =>
        page.EvaluateAsync<int>(
            """
            storeName => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const db = open.result;
                const request = db.transaction(storeName, "readonly")
                  .objectStore(storeName).count();
                request.onsuccess = () => {
                  db.close();
                  resolve(request.result);
                };
                request.onerror = () => {
                  db.close();
                  reject(request.error);
                };
              };
            })
            """,
            store);

    private static Task<string> ReadOperationIdentityAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const db = open.result;
                const request = db.transaction("operations", "readonly")
                  .objectStore("operations").getAll();
                request.onsuccess = () => {
                  db.close();
                  resolve(JSON.stringify(request.result
                    .map(({ id, clientOccurredAt, createdAt, kind }) =>
                      ({ id, clientOccurredAt, createdAt, kind }))
                    .sort((left, right) => left.createdAt.localeCompare(right.createdAt))));
                };
                request.onerror = () => {
                  db.close();
                  reject(request.error);
                };
              };
            })
            """);

    private static async Task AssertOfflineOriginsAsync(
        IPage page,
        params string[] urls)
    {
        var inaccessible = await page.EvaluateAsync<bool[]>(
            """
            urls => Promise.all(urls.map(
              url => fetch(url, { cache: "no-store" })
                .then(() => false)
                .catch(() => true)))
            """,
            urls);
        Assert.All(inaccessible, Assert.True);
    }

    private static void ObserveCdpRequest(
        JsonElement? payload,
        ConcurrentDictionary<string, BrowserRequestObservation> requestsById,
        ConcurrentQueue<BrowserRequestObservation> observations)
    {
        if (payload is not { } value ||
            !value.TryGetProperty("requestId", out var requestIdElement) ||
            !value.TryGetProperty("request", out var request) ||
            !request.TryGetProperty("url", out var urlElement) ||
            !request.TryGetProperty("method", out var methodElement))
        {
            return;
        }

        if (!TryReadObservedOrigins(
                urlElement.GetString(),
                request.TryGetProperty("headers", out var headers) ? headers : null,
                out var targetOrigin,
                out var browserOrigin))
        {
            return;
        }

        var observation = new BrowserRequestObservation(
            methodElement.GetString() ?? string.Empty,
            targetOrigin,
            browserOrigin);
        var requestId = requestIdElement.GetString();
        if (!string.IsNullOrEmpty(requestId))
        {
            requestsById[requestId] = observation;
        }
        observations.Enqueue(observation);
    }

    private static void ObserveCdpResponse(
        JsonElement? payload,
        ConcurrentDictionary<string, BrowserRequestObservation> requestsById,
        ConcurrentQueue<BrowserResponseObservation> observations)
    {
        if (payload is not { } value ||
            !value.TryGetProperty("requestId", out var requestIdElement) ||
            !value.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("status", out var statusElement) ||
            requestIdElement.GetString() is not { } requestId ||
            !requestsById.TryGetValue(requestId, out var request))
        {
            return;
        }

        observations.Enqueue(
            new BrowserResponseObservation(
                request.Method,
                request.TargetOrigin,
                request.BrowserOrigin,
                statusElement.GetInt32()));
    }

    private static bool TryReadObservedOrigins(
        string? url,
        JsonElement? headers,
        out string targetOrigin,
        out string? browserOrigin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            targetOrigin = string.Empty;
            browserOrigin = null;
            return false;
        }

        targetOrigin = uri.GetLeftPart(UriPartial.Authority);
        browserOrigin = headers is { ValueKind: JsonValueKind.Object } headerObject
            ? headerObject
                .EnumerateObject()
                .Where(header => string.Equals(
                    header.Name,
                    "origin",
                    StringComparison.OrdinalIgnoreCase))
                .Select(header => header.Value.GetString())
                .SingleOrDefault()
            : null;
        return true;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = nameof(DriverOfflineOperationsRealPipelineTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed record PipelineEvidence(
        string Status,
        int Proofs,
        int ConsumedSessions,
        int TransitionEvents,
        int IdempotencyRows,
        int DuplicateIdempotencyRows,
        IReadOnlyList<string> TargetStatuses,
        IReadOnlyList<DateTimeOffset> ClientCaptureTimes);

    private sealed record BrowserRequestObservation(
        string Method,
        string TargetOrigin,
        string? BrowserOrigin);

    private sealed record BrowserResponseObservation(
        string Method,
        string TargetOrigin,
        string? BrowserOrigin,
        int Status);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverOfflineOperationsRealCollection :
    ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>,
    ICollectionFixture<CorsCapableS3Fixture>
{
    public const string Name = "Driver offline operations real pipeline";
}
