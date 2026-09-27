using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

[Collection(DriverStopsPwaCollection.Name)]
public sealed class DriverOfflineOperationsPwaPlaywrightTests(
    DriverStopsNextServerFixture server)
{
    private const string OrderId = "22222222-2222-4222-8222-222222222222";
    private const string OrganizationId = "11111111-1111-4111-8111-111111111111";

    /// <summary>
    /// Reserved synthetic signed-upload origin, fulfilled by Playwright before
    /// any network access. The deployable artifact only accepts HTTPS signed
    /// upload URLs, exactly as it does against real object storage.
    /// </summary>
    private const string SyntheticStorageOrigin = DriverStopsNextServerFixture.TestStorageOrigin;
    private static readonly byte[] SyntheticPng =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task IndexedDb_v1_snapshot_survives_the_atomic_v2_upgrade()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string cacheNamespace = "opaque-drv002-upgrade";
        await InstallSessionAsync(page, cacheNamespace);
        await page.GotoAsync(server.BaseAddress.AbsoluteUri);
        await page.EvaluateAsync(
            """
            async value => {
              const input = new TextEncoder().encode(
                `${value.cacheNamespace}\0${value.organizationId}`);
              const digest = new Uint8Array(
                await crypto.subtle.digest("SHA-256", input));
              let binary = "";
              for (const byte of digest) binary += String.fromCharCode(byte);
              const partitionKey = btoa(binary)
                .replaceAll("+", "-")
                .replaceAll("/", "_")
                .replaceAll("=", "");
              await new Promise((resolve, reject) => {
                const open = indexedDB.open("paquetenvia-driver-stops-v1", 1);
                open.onupgradeneeded = () =>
                  open.result.createObjectStore(
                    "snapshots",
                    { keyPath: "partitionKey" });
                open.onerror = () => reject(open.error);
                open.onsuccess = () => {
                  const database = open.result;
                  const transaction = database.transaction(
                    "snapshots",
                    "readwrite");
                  transaction.objectStore("snapshots").put({
                    partitionKey,
                    schemaVersion: 1,
                    synchronizedAt: "2026-07-26T12:00:00.000Z",
                    stops: [{
                      order_id: value.orderId,
                      aggregate_version: 3,
                      order_public_id: "ORD_DRV002_UPGRADE",
                      stop_type: "PICKUP",
                      status: "ASSIGNED",
                      address_summary: "Zona sintética upgrade",
                    }],
                  });
                  transaction.oncomplete = () => {
                    database.close();
                    resolve(undefined);
                  };
                  transaction.onerror = () => reject(transaction.error);
                };
              });
            }
            """,
            new
            {
                cacheNamespace,
                organizationId = OrganizationId,
                orderId = OrderId,
            });
        await page.RouteAsync(
            "**/api/v1/driver/me/stops",
            route => route.AbortAsync());

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);

        await ExpectTextAsync(page, "ORD_DRV002_UPGRADE");
        var evidence = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const stores = [...database.objectStoreNames].sort();
                const request = database.transaction("snapshots", "readonly")
                  .objectStore("snapshots").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  const snapshots = request.result;
                  database.close();
                  resolve(JSON.stringify({
                    stores,
                    snapshotCount: snapshots.length,
                    publicId: snapshots[0]?.stops?.[0]?.order_public_id,
                    schemaVersion: snapshots[0]?.schemaVersion,
                  }));
                };
              };
            })
            """);
        using var document = JsonDocument.Parse(evidence);
        Assert.Equal(
            ["operations", "proof_blobs", "snapshots", "sync_leases"],
            document.RootElement
                .GetProperty("stores")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(1, document.RootElement.GetProperty("snapshotCount").GetInt32());
        Assert.Equal(
            "ORD_DRV002_UPGRADE",
            document.RootElement.GetProperty("publicId").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Five_actions_two_proofs_survive_offline_reload_and_sync_once()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-main");
        var backend = new SyntheticOfflineBackend();
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");
        await EnsureServiceWorkerControlAsync(page);
        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");

        await context.SetOfflineAsync(true);
        try
        {
            await EnqueueButtonAsync(page, "Llegué a recolección");
            await EnqueueProofAsync(
                page,
                "Confirmar recolección",
                "pickup-synthetic.png");
            await EnqueueButtonAsync(page, "Iniciar traslado");
            await EnqueueButtonAsync(page, "Llegué al destino");
            await EnqueueProofAsync(
                page,
                "Confirmar entrega",
                "delivery-synthetic.png");
            await WaitForOperationCountAsync(page, 5);
            await WaitForProofCountAsync(page, 2);

            await page.ReloadAsync();
            await ExpectTextAsync(page, "5 acciones en el dispositivo");
            await ExpectTextAsync(page, "Entregada");
            Assert.DoesNotContain(
                OrderId,
                await page.Locator("body").InnerTextAsync());
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        await page.GetByRole(
                AriaRole.Button,
                new() { Name = "Sincronizar ahora" })
            .ClickAsync();
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        await ExpectTextAsync(page, "Parada no disponible");
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "No tienes paradas activas.");

        Assert.Equal(
            [
                "AT_PICKUP",
                "PICKED_UP",
                "IN_TRANSIT",
                "DELIVERING",
                "DELIVERED",
            ],
            backend.TransitionTargets);
        Assert.Equal(5, backend.TransitionKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, backend.FinalizedProofs.Count);
        Assert.All(
            backend.FinalizedProofs,
            proof => Assert.EndsWith("Z", proof.CapturedAt, StringComparison.Ordinal));
        // OPS-003-PWA-CLIENT-OCCURRED-AT: every transition and upload session
        // carries the instant the action was captured offline, and a proof
        // operation uses that same instant as its captured_at.
        Assert.Equal(5, backend.TransitionClientOccurredAt.Count);
        Assert.All(
            backend.TransitionClientOccurredAt,
            value => Assert.EndsWith("Z", value, StringComparison.Ordinal));
        Assert.Equal(
            5,
            backend.TransitionClientOccurredAt.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            backend.SessionClientOccurredAt,
            value => Assert.Contains(
                value,
                backend.FinalizedProofs.Select(proof => proof.CapturedAt)));
        Assert.All(
            backend.FinalizedProofs,
            proof => Assert.Contains(proof.CapturedAt, backend.TransitionClientOccurredAt));
        Assert.All(
            backend.SessionAttempts,
            attempt => Assert.StartsWith("drv2-", attempt.Key, StringComparison.Ordinal));
        Assert.Equal(2, backend.UploadCount);
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Lost_transition_response_is_confirmed_by_rest_before_resend()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-lost-response");
        var backend = new SyntheticOfflineBackend { AbortFirstTransition = true };
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Llegué a recolección");
        await page.GetByRole(AriaRole.Button, new() { Name = "Llegué a recolección" })
            .ClickAsync();

        await WaitUntilAsync(() => backend.TransitionTargets.Count == 1);
        await page.WaitForFunctionAsync(
            "() => document.body.innerText.includes('En punto de recolección')");
        await WaitForOperationCountAsync(page, 0);
        Assert.Single(backend.TransitionTargets);
        Assert.Single(backend.TransitionKeys);
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Lost_put_response_replays_the_same_session_and_blob_once()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-lost-put");
        var backend = new SyntheticOfflineBackend { AbortFirstUpload = true };
        backend.SetOrderState("AT_PICKUP", 4);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await EnqueueProofAsync(page, "Confirmar recolección", "lost-put.png");
        await WaitForOperationStatusAsync(page, "RETRY_WAIT");

        await page.ReloadAsync();
        await page.GetByRole(
                AriaRole.Button,
                new() { Name = "Sincronizar ahora" })
            .ClickAsync();
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);

        Assert.Equal(2, backend.UploadCount);
        Assert.True(backend.SessionAttempts.Count >= 2);
        Assert.Single(
            backend.SessionAttempts
                .Select(attempt => attempt.Key)
                .Distinct(StringComparer.Ordinal));
        var capturedAt = Assert.Single(
            backend.SessionClientOccurredAt.Distinct(StringComparer.Ordinal));
        Assert.NotNull(capturedAt);
        Assert.Single(backend.FinalizedProofs);
        Assert.Equal(capturedAt, backend.FinalizedProofs[0].CapturedAt);
        Assert.Equal([capturedAt], backend.TransitionClientOccurredAt);
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Lost_proof_201_replays_the_same_finalize_key_without_duplicates()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-lost-proof");
        var backend = new SyntheticOfflineBackend { AbortFirstFinalize = true };
        backend.SetOrderState("AT_PICKUP", 4);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await EnqueueProofAsync(page, "Confirmar recolección", "lost-proof.png");
        await WaitForOperationStatusAsync(page, "RETRY_WAIT");

        await page.ReloadAsync();
        await page.GetByRole(
                AriaRole.Button,
                new() { Name = "Sincronizar ahora" })
            .ClickAsync();
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);

        Assert.Single(backend.FinalizedProofs);
        Assert.True(backend.FinalizeKeys.Count >= 2);
        Assert.Single(backend.FinalizeKeys.Distinct(StringComparer.Ordinal));
        Assert.Single(backend.TransitionTargets);
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Visible_conflict_requires_an_explicit_new_operation()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-visible-conflict");
        var backend = new SyntheticOfflineBackend { ConflictFirstTransition = true };
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Llegué a recolección");
        await page.GetByRole(AriaRole.Button, new() { Name = "Llegué a recolección" })
            .ClickAsync();

        await WaitUntilAsync(() => backend.TransitionKeys.Count == 1);
        await ExpectTextAsync(page, "Acción que requiere atención");
        Assert.Single(backend.TransitionKeys);
        var conflictedKey = backend.TransitionKeys[0];
        await page.WaitForTimeoutAsync(1_100);
        Assert.Single(backend.TransitionKeys);

        await page.GetByRole(
                AriaRole.Button,
                new() { Name = "Crear acción para versión actual" })
            .ClickAsync();
        await page.GetByRole(
                AriaRole.Button,
                new() { Name = "Confirmar", Exact = true })
            .ClickAsync();
        await WaitUntilAsync(() => backend.TransitionKeys.Count == 2);
        await WaitForOperationCountAsync(page, 0);

        Assert.Equal(2, backend.TransitionKeys.Count);
        Assert.NotEqual(conflictedKey, backend.TransitionKeys[1]);
        await ExpectTextAsync(page, "En punto de recolección");
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Expired_offline_operation_is_dropped_once_and_the_driver_is_told()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-ops003-expired");
        var backend = new SyntheticOfflineBackend { ExpireTransitions = true };
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Llegué a recolección");
        await EnsureServiceWorkerControlAsync(page);
        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Llegué a recolección");
        string capturedAt;
        await context.SetOfflineAsync(true);
        try
        {
            await EnqueueButtonAsync(page, "Llegué a recolección");
            await WaitForOperationCountAsync(page, 1);
            capturedAt = await ReadSingleClientOccurredAtAsync(page);
            await page.ReloadAsync();
            await ExpectTextAsync(page, "1 acción en el dispositivo");
            Assert.Equal(capturedAt, await ReadSingleClientOccurredAtAsync(page));
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        // Coming back online replays the queue on its own.
        await page.WaitForFunctionAsync("() => navigator.onLine === true");
        await WaitForOperationCountAsync(page, 0);
        await ExpectTextAsync(
            page,
            "La acción «Llegué a recolección» venció: pasaron más de 72 horas sin conexión");
        await ExpectTextAsync(page, "Vuelve a registrarla o repórtala a despacho.");
        Assert.Equal([capturedAt], backend.TransitionClientOccurredAt);

        // Never retried: no backoff timer, reload or manual sync resends it.
        await page.WaitForTimeoutAsync(1_100);
        await page.ReloadAsync();
        await ExpectTextAsync(page, "Llegué a recolección");
        await page.WaitForTimeoutAsync(500);
        Assert.Single(backend.TransitionKeys);
        Assert.Equal(0, await ReadStoreCountAsync(page, "operations"));
    }

    [Theory]
    [InlineData(401, "Sesión no válida")]
    [InlineData(403, "Acceso no disponible")]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Revocation_clears_snapshot_operation_and_blob_without_mutation(
        int responseStatus,
        string expectedTitle)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, $"opaque-drv002-revoked-{responseStatus}");
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("AT_PICKUP", 4);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Confirmar recolección");
        await context.SetOfflineAsync(true);
        await EnqueueProofAsync(page, "Confirmar recolección", "revoked.png");
        await WaitForOperationCountAsync(page, 1);
        await WaitForProofCountAsync(page, 1);

        backend.StopsFailureStatus = responseStatus;
        await context.SetOfflineAsync(false);
        await page.WaitForFunctionAsync("() => navigator.onLine === true");

        await ExpectTextAsync(page, expectedTitle);
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        Assert.Empty(backend.SessionAttempts);
        Assert.Empty(backend.TransitionTargets);
        Assert.DoesNotContain(
            "Zona sintética DRV-002",
            await page.Locator("body").InnerTextAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Missing_order_purges_its_operation_and_blob_without_order_probe()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-missing-order");
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("AT_PICKUP", 4);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await context.SetOfflineAsync(true);
        await EnqueueProofAsync(page, "Confirmar recolección", "missing.png");
        await WaitForOperationCountAsync(page, 1);
        await WaitForProofCountAsync(page, 1);

        backend.HideOrder = true;
        await context.SetOfflineAsync(false);
        await page.WaitForFunctionAsync("() => navigator.onLine === true");

        await ExpectTextAsync(page, "Parada no disponible");
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        Assert.Empty(backend.SessionAttempts);
        Assert.Empty(backend.TransitionTargets);
    }

    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Offline_operations_remain_isolated_across_organizations()
    {
        const string organizationA = OrganizationId;
        const string organizationB = "55555555-5555-4555-8555-555555555555";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv002-shared-session");
        await page.RouteAsync("**/api/v1/driver/me/stops", async route =>
        {
            var organization =
                await route.Request.HeaderValueAsync("X-Organization-Id");
            var suffix = organization == organizationA ? "A" : "B";
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        order_id = OrderId,
                        aggregate_version = 3,
                        order_public_id = $"ORD_DRV002_ORG_{suffix}",
                        stop_type = "PICKUP",
                        status = "ASSIGNED",
                        address_summary = $"Zona sintética {suffix}",
                    },
                }),
            });
        });

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_DRV002_ORG_A");
        await ChangeOrganizationAsync(
            page,
            organizationB,
            "opaque-drv002-shared-session");
        await ExpectTextAsync(page, "ORD_DRV002_ORG_B");
        await ChangeOrganizationAsync(
            page,
            organizationA,
            "opaque-drv002-shared-session");
        await ExpectTextAsync(page, "ORD_DRV002_ORG_A");

        await context.SetOfflineAsync(true);
        try
        {
            await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Llegué a recolección" })
                .ClickAsync();
            await WaitForOperationCountAsync(page, 1);

            await ChangeOrganizationAsync(
                page,
                organizationB,
                "opaque-drv002-shared-session");
            await ExpectTextAsync(page, "ORD_DRV002_ORG_B");
            Assert.DoesNotContain(
                "ORD_DRV002_ORG_A",
                await page.Locator("body").InnerTextAsync(),
                StringComparison.Ordinal);
            await page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Llegué a recolección" })
                .ClickAsync();
            await WaitForOperationCountAsync(page, 2);
            await ExpectTextAsync(page, "1 acción en el dispositivo");
            Assert.Equal(2, await ReadDistinctOperationPartitionsAsync(page));
            Assert.Equal(2, await ReadStoreCountAsync(page, "operations"));
            Assert.Equal(2, await ReadDistinctOperationPartitionsAsync(page));
            var shellContainsData = await page.EvaluateAsync<bool>(
                """
                async () => {
                  const cache = await caches.open("paquetenvia-driver-shell-v5");
                  const response = await cache.match("/driver/stops");
                  const text = response ? await response.text() : "";
                  return text.includes("ORD_DRV002_ORG_A") ||
                    text.includes("ORD_DRV002_ORG_B") ||
                    text.includes("Zona sintética");
                }
                """);
            Assert.False(shellContainsData);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }
    }

    private static async Task EnqueueButtonAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = name }).ClickAsync();
        await page.WaitForFunctionAsync(
            "label => !document.body.getAttribute('aria-busy') && document.body.innerText.includes(label)",
            NextLabel(name));
    }

    private static async Task EnqueueProofAsync(
        IPage page,
        string button,
        string fileName)
    {
        await page.Locator("input[type=file]").SetInputFilesAsync(
            new FilePayload
            {
                Name = fileName,
                MimeType = "image/png",
                Buffer = SyntheticPng,
            });
        await page.GetByRole(AriaRole.Button, new() { Name = button }).ClickAsync();
        await page.WaitForTimeoutAsync(100);
    }

    private static string NextLabel(string current) => current switch
    {
        "Llegué a recolección" => "Confirmar recolección",
        "Iniciar traslado" => "Llegué al destino",
        "Llegué al destino" => "Confirmar entrega",
        _ => current,
    };

    private static async Task EnsureServiceWorkerControlAsync(IPage page)
    {
        await page.EvaluateAsync(
            "() => navigator.serviceWorker.ready.then(() => undefined)");
        await page.ReloadAsync();
        await page.WaitForFunctionAsync(
            "() => navigator.serviceWorker.controller !== null");
        await WaitForBrowserConditionAsync(
            page,
            """
            async () => {
              const cache = await caches.open("paquetenvia-driver-shell-v5");
              return (await cache.keys()).some(
                key => new URL(key.url).pathname.startsWith("/_next/static/"));
            }
            """,
            "the driver shell cache does not hold static assets");
    }

    private static Task WaitForOperationCountAsync(IPage page, int count) =>
        WaitForStoreCountAsync(page, "operations", count);

    private static Task WaitForProofCountAsync(IPage page, int count) =>
        WaitForStoreCountAsync(page, "proof_blobs", count);

    private static Task WaitForOperationStatusAsync(
        IPage page,
        string status) =>
        WaitForBrowserConditionAsync(
            page,
            """
            status => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("operations", "readonly")
                  .objectStore("operations").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(request.result.some(value => value.status === status));
                };
              };
            })
            """,
            $"no queued operation reached {status}",
            status);

    /// <summary>
    /// Polls an asynchronous browser predicate. Playwright's
    /// <c>WaitForFunctionAsync</c> treats a returned <c>Promise</c> as a truthy
    /// value on its first evaluation, so IndexedDB and Cache Storage checks
    /// must be re-evaluated here until they resolve to <c>true</c>.
    /// </summary>
    private static async Task WaitForBrowserConditionAsync(
        IPage page,
        string predicate,
        string failure,
        object? argument = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            if (await page.EvaluateAsync<bool>(predicate, argument))
            {
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out: {failure}. Queue: {await ReadOperationsAsync(page)}");
            }
            await Task.Delay(100);
        }
    }

    private static async Task WaitForStoreCountAsync(
        IPage page,
        string storeName,
        int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var actual = await ReadStoreCountAsync(page, storeName);
            if (actual == expected)
            {
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Expected {expected} entries in {storeName} but found {actual}. " +
                    $"Queue: {await ReadOperationsAsync(page)}");
            }
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Queue diagnostics without proof bytes, tokens or signed URLs.
    /// </summary>
    private static Task<string> ReadOperationsAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("operations", "readonly")
                  .objectStore("operations").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(JSON.stringify(request.result.map(operation => ({
                    kind: operation.kind,
                    status: operation.status,
                    safeError: operation.safeError,
                    attemptCount: operation.attemptCount,
                    expectedVersion: operation.expectedVersion,
                    uploadAccepted: operation.uploadAccepted,
                    hasSession: operation.uploadSessionId !== null,
                    hasProof: operation.proofId !== null,
                  }))));
                };
              };
            })
            """);

    private static async Task InstallSessionAsync(
        IPage page,
        string cacheNamespace)
    {
        await page.RouteAsync("**/hubs/driver/negotiate**", route => route.AbortAsync());
        var configuration = JsonSerializer.Serialize(new
        {
            organizationId = OrganizationId,
            cacheNamespace,
            token = "synthetic-driver-token",
        });
        await page.AddInitScriptAsync(
            $$"""
            (() => {
              const value = {{configuration}};
              window.__paquetenviaDriverSession = {
                organizationId: value.organizationId,
                cacheNamespace: value.cacheNamespace,
                getAccessToken: () => value.token,
              };
            })();
            """);
    }

    private static Task ChangeOrganizationAsync(
        IPage page,
        string organizationId,
        string cacheNamespace) =>
        page.EvaluateAsync(
            """
            value => {
              window.__paquetenviaDriverSession = {
                organizationId: value.organizationId,
                cacheNamespace: value.cacheNamespace,
                getAccessToken: () => "synthetic-driver-token",
              };
              window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
            }
            """,
            new { organizationId, cacheNamespace });

    private static Task<string> ReadSingleClientOccurredAtAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("operations", "readonly")
                  .objectStore("operations").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  if (request.result.length !== 1) {
                    reject(new Error("expected exactly one queued operation"));
                    return;
                  }
                  resolve(request.result[0].clientOccurredAt);
                };
              };
            })
            """);

    private static Task<int> ReadStoreCountAsync(IPage page, string storeName) =>
        page.EvaluateAsync<int>(
            """
            storeName => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction(storeName, "readonly")
                  .objectStore(storeName).count();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(request.result);
                };
              };
            })
            """,
            storeName);

    private static Task<int> ReadDistinctOperationPartitionsAsync(IPage page) =>
        page.EvaluateAsync<int>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("operations", "readonly")
                  .objectStore("operations").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(new Set(request.result.map(value => value.partitionKey)).size);
                };
              };
            })
            """);

    private static async Task ExpectTextAsync(IPage page, string text) =>
        await page.GetByText(text, new() { Exact = false }).First.WaitForAsync();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class SyntheticOfflineBackend
    {
        private readonly object gate = new();
        private readonly ConcurrentDictionary<string, int> finalizeAttempts = new();
        private readonly Dictionary<string, string> sessionsByKey = [];
        private readonly Dictionary<string, FinalizedProof> proofsByKey = [];
        private string status = "ASSIGNED";
        private int version = 3;
        private int sessionSequence;
        private bool firstTransition = true;
        private bool firstUpload = true;
        private bool firstFinalize = true;

        internal bool AbortFirstTransition { get; init; }
        internal bool AbortFirstUpload { get; init; }
        internal bool AbortFirstFinalize { get; init; }
        internal bool ConflictFirstTransition { get; init; }
        internal bool ExpireTransitions { get; init; }
        internal int? StopsFailureStatus { get; set; }
        internal bool HideOrder { get; set; }
        internal List<string> TransitionTargets { get; } = [];
        internal List<string> TransitionKeys { get; } = [];
        internal List<string?> TransitionClientOccurredAt { get; } = [];
        internal List<string?> SessionClientOccurredAt { get; } = [];
        internal List<(string Key, string ProofType)> SessionAttempts { get; } = [];
        internal List<string> FinalizeKeys { get; } = [];
        internal List<(string ProofType, string CapturedAt)> FinalizedProofs { get; } = [];
        internal int UploadCount { get; private set; }

        internal void SetOrderState(string nextStatus, int nextVersion)
        {
            lock (gate)
            {
                status = nextStatus;
                version = nextVersion;
            }
        }

        internal async Task RouteAsync(IPage page)
        {
            await page.RouteAsync("**/api/v1/driver/me/stops", RouteStopsAsync);
            await page.RouteAsync("**/api/v1/orders/*/transitions", RouteTransitionAsync);
            await page.RouteAsync(
                "**/api/v1/orders/*/proof-upload-sessions",
                RouteSessionAsync);
            await page.RouteAsync("**/api/v1/orders/*/proofs", RouteFinalizeAsync);
            await page.RouteAsync($"{SyntheticStorageOrigin}/**", async route =>
            {
                bool abort;
                lock (gate)
                {
                    UploadCount += 1;
                    abort = AbortFirstUpload && firstUpload;
                    firstUpload = false;
                }
                if (abort)
                {
                    await route.AbortAsync();
                    return;
                }
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    Headers = new Dictionary<string, string>
                    {
                        ["Access-Control-Allow-Origin"] = "*",
                    },
                });
            });
        }

        private Task RouteStopsAsync(IRoute route)
        {
            if (StopsFailureStatus is int failure)
            {
                return route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = failure,
                    ContentType = "application/problem+json",
                    Body = """{"code":"ACCESS_UNAVAILABLE"}""",
                });
            }
            object[] payload;
            lock (gate)
            {
                payload = HideOrder || status == "DELIVERED"
                    ? []
                    :
                    [
                        new
                        {
                            order_id = OrderId,
                            aggregate_version = version,
                            order_public_id = "ORD_DRV002_SYNTHETIC",
                            stop_type = status is "ASSIGNED" or "AT_PICKUP"
                                ? "PICKUP"
                                : "DELIVERY",
                            status,
                            address_summary = "Zona sintética DRV-002",
                        },
                    ];
            }
            return route.FulfillAsync(Json(payload, 200));
        }

        private async Task RouteTransitionAsync(IRoute route)
        {
            var request = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
            var target = request.GetProperty("target_status").GetString()!;
            var key = await route.Request.HeaderValueAsync("Idempotency-Key") ?? string.Empty;
            var abort = false;
            lock (gate)
            {
                TransitionTargets.Add(target);
                TransitionKeys.Add(key);
                TransitionClientOccurredAt.Add(ReadClientOccurredAt(request));
                abort = AbortFirstTransition && firstTransition;
                if (!(ConflictFirstTransition && firstTransition) && !ExpireTransitions)
                {
                    status = target;
                    version += 1;
                }
                firstTransition = false;
            }
            if (ConflictFirstTransition && TransitionTargets.Count == 1)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"code":"ORDER_VERSION_CONFLICT"}""",
                });
                return;
            }
            if (ExpireTransitions)
            {
                // AI-05 TransitionConflictProblem for OPS-003-SERVER-72H-REJECTION.
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Conflict","status":409,"code":"OFFLINE_OPERATION_EXPIRED"}""",
                });
                return;
            }
            if (abort)
            {
                await route.AbortAsync();
                return;
            }
            await route.FulfillAsync(Json(
                new
                {
                    id = OrderId,
                    public_id = "ORD_DRV002_SYNTHETIC",
                    owner_org_id = OrganizationId,
                    operator_org_id = OrganizationId,
                    status = target,
                    price_net = new { currency = "MXN", amount_cents = 10000 },
                    version,
                    origin_location_id = Guid.Parse("66666666-6666-4666-8666-666666666666"),
                    destination_location_id = Guid.Parse("77777777-7777-4777-8777-777777777777"),
                    service_type = "LOCAL",
                    quote_id = Guid.Parse("88888888-8888-4888-8888-888888888888"),
                    city_id = Guid.Parse("99999999-9999-4999-8999-999999999999"),
                    service_area_id = (Guid?)null,
                    pricing_tier = "STANDARD",
                    total = new { currency = "MXN", amount_cents = 11600 },
                    claim_window_ends_at = (DateTimeOffset?)null,
                    finalized_at = target == "DELIVERED"
                        ? DateTimeOffset.Parse("2026-07-26T12:00:00.000Z")
                        : (DateTimeOffset?)null,
                },
                200));
        }

        private async Task RouteSessionAsync(IRoute route)
        {
            var request = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
            var proofType = request.GetProperty("proof_type").GetString()!;
            var key = await route.Request.HeaderValueAsync("Idempotency-Key") ?? string.Empty;
            string sessionId;
            lock (gate)
            {
                SessionAttempts.Add((key, proofType));
                SessionClientOccurredAt.Add(ReadClientOccurredAt(request));
                if (!sessionsByKey.TryGetValue(key, out var existingSessionId))
                {
                    sessionSequence += 1;
                    sessionId =
                        $"00000000-0000-4000-8000-{sessionSequence.ToString().PadLeft(12, '0')}";
                    sessionsByKey.Add(key, sessionId);
                }
                else
                {
                    sessionId = existingSessionId;
                }
            }
            await route.FulfillAsync(Json(
                new
                {
                    id = sessionId,
                    status = "CREATED",
                    upload_url =
                        $"{SyntheticStorageOrigin}/quarantine/{sessionId}?X-Amz-Signature=synthetic",
                    object_key = $"quarantine/private/{sessionId}",
                    expires_at = "2099-01-01T00:00:00.000Z",
                    required_headers = new Dictionary<string, string>
                    {
                        ["Content-Type"] = "image/png",
                    },
                },
                201));
        }

        private async Task RouteFinalizeAsync(IRoute route)
        {
            var request = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
            var sessionId = request.GetProperty("upload_session_id").GetString()!;
            var key = await route.Request.HeaderValueAsync("Idempotency-Key")
                ?? string.Empty;
            lock (gate) FinalizeKeys.Add(key);
            var attempt = finalizeAttempts.AddOrUpdate(sessionId, 1, (_, current) => current + 1);
            if (attempt == 1)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"code":"PROOF_OBJECT_NOT_READY"}""",
                });
                return;
            }
            var proofType = request.GetProperty("proof_type").GetString()!;
            var capturedAt = request.GetProperty("captured_at").GetString()!;
            var sha = request.GetProperty("sha256").GetString()!;
            FinalizedProof proof;
            bool abort;
            lock (gate)
            {
                if (!proofsByKey.TryGetValue(key, out var existingProof))
                {
                    proof = new FinalizedProof(sessionId, proofType, capturedAt, sha);
                    proofsByKey.Add(key, proof);
                    FinalizedProofs.Add((proofType, capturedAt));
                }
                else
                {
                    proof = existingProof;
                }
                abort = AbortFirstFinalize && firstFinalize;
                firstFinalize = false;
            }
            if (abort)
            {
                await route.AbortAsync();
                return;
            }
            await route.FulfillAsync(Json(
                new
                {
                    id = proof.Id,
                    proof_type = proof.ProofType,
                    sha256 = proof.Sha256,
                    captured_at = proof.CapturedAt,
                },
                201));
        }

        private static string? ReadClientOccurredAt(JsonElement request) =>
            request.TryGetProperty("client_occurred_at", out var value)
                ? value.GetString()
                : null;

        private static RouteFulfillOptions Json(object value, int status) => new()
        {
            Status = status,
            ContentType = "application/json; charset=utf-8",
            Body = JsonSerializer.Serialize(value),
        };

        private sealed record FinalizedProof(
            string Id,
            string ProofType,
            string CapturedAt,
            string Sha256);
    }
}
