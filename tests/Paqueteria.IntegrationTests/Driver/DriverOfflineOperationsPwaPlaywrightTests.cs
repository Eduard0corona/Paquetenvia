using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;
using Paqueteria.Domain.Tenancy;

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

    /// <summary>Decodable 1x1 PNGs (two colours) for the photo preview.</summary>
    private static readonly byte[] FirstPreviewPng =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xde, 0x00, 0x00, 0x00,
        0x0c, 0x49, 0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0xe0, 0x2f, 0xcb, 0x03,
        0x00, 0x01, 0x8b, 0x00, 0xf4, 0x02, 0x38, 0xf3, 0x86, 0x00, 0x00, 0x00,
        0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
    ];

    private static readonly byte[] SecondPreviewPng =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xde, 0x00, 0x00, 0x00,
        0x0c, 0x49, 0x44, 0x41, 0x54, 0x78, 0xda, 0x63, 0xb8, 0x59, 0xce, 0x06,
        0x00, 0x03, 0x83, 0x01, 0x57, 0x51, 0xa6, 0x6c, 0xcb, 0x00, 0x00, 0x00,
        0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
    ];

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the upgrade from the DRV-002 layout keeps every queued DRV-002
    /// operation and drops the schema 1 snapshot, which carries no stop details; the next REST response rebuilds
    /// it. UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: the database then reaches version 4 and the operation is
    /// rewritten as operation schema 3; DRV-DISPATCH-CONTACT-2026-10-12: it now reaches version 5, which adds the
    /// dispatch contact store.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task IndexedDb_v2_upgrade_keeps_queued_operations_and_drops_detail_less_snapshots()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string cacheNamespace = "opaque-drv002-upgrade";
        const string operationId = "33333333-3333-4333-8333-333333333333";
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
                // The DRV-002 (version 2) layout, exactly as that release created it.
                const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
                open.onupgradeneeded = () => {
                  const database = open.result;
                  database.createObjectStore("snapshots", { keyPath: "partitionKey" });
                  const operations = database.createObjectStore(
                    "operations",
                    { keyPath: ["partitionKey", "id"] });
                  operations.createIndex("by_partition", "partitionKey");
                  operations.createIndex("by_partition_order", ["partitionKey", "orderId"]);
                  operations.createIndex("by_partition_status", ["partitionKey", "status"]);
                  operations.createIndex("by_partition_next_attempt", ["partitionKey", "nextAttemptAt"]);
                  operations.createIndex("by_partition_created", ["partitionKey", "createdAt", "id"]);
                  const blobs = database.createObjectStore(
                    "proof_blobs",
                    { keyPath: ["partitionKey", "operationId"] });
                  blobs.createIndex("by_partition", "partitionKey");
                  database.createObjectStore("sync_leases", { keyPath: "partitionKey" });
                };
                open.onerror = () => reject(open.error);
                open.onsuccess = () => {
                  const database = open.result;
                  const transaction = database.transaction(
                    ["snapshots", "operations"],
                    "readwrite");
                  transaction.objectStore("snapshots").put({
                    partitionKey,
                    schemaVersion: 1,
                    synchronizedAt: "2026-10-11T12:00:00.000Z",
                    stops: [{
                      order_id: value.orderId,
                      aggregate_version: 3,
                      order_public_id: "ORD_DRV002_UPGRADE",
                      stop_type: "PICKUP",
                      status: "ASSIGNED",
                      address_summary: "Zona sintética upgrade",
                    }],
                  });
                  const capturedAt = new Date(Date.now() - 60_000).toISOString();
                  transaction.objectStore("operations").put({
                    schemaVersion: 1,
                    partitionKey,
                    id: value.operationId,
                    orderId: value.orderId,
                    kind: "CHECK_IN",
                    sourceStatus: "ASSIGNED",
                    targetStatus: "AT_PICKUP",
                    expectedVersion: 3,
                    reason: "DRIVER_CHECK_IN",
                    clientOccurredAt: capturedAt,
                    createdAt: capturedAt,
                    transitionIdempotencyKey: `drv2-${value.operationId}-transition`,
                    sessionIdempotencyKey: null,
                    finalizeIdempotencyKey: null,
                    proofType: null,
                    contentType: null,
                    sizeBytes: null,
                    sha256: null,
                    capturedAt: null,
                    status: "PENDING",
                    attemptCount: 0,
                    nextAttemptAt: capturedAt,
                    sessionAttempt: 1,
                    uploadSessionId: null,
                    uploadSessionExpiresAt: null,
                    uploadAccepted: false,
                    proofId: null,
                    safeError: null,
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
                operationId,
            });
        await page.RouteAsync(
            DriverStopsPwaPlaywrightTests.StopsRoute,
            route => route.AbortAsync());

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);

        await ExpectTextAsync(page, "1 acción en el dispositivo");
        Assert.DoesNotContain(
            "ORD_DRV002_UPGRADE",
            await page.Locator("body").InnerTextAsync(),
            StringComparison.Ordinal);
        var evidence = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1");
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const stores = [...database.objectStoreNames].sort();
                const transaction = database.transaction(
                  ["snapshots", "operations"],
                  "readonly");
                const snapshots = transaction.objectStore("snapshots").getAll();
                const operations = transaction.objectStore("operations").getAll();
                transaction.onerror = () => {
                  database.close();
                  reject(transaction.error);
                };
                transaction.oncomplete = () => {
                  const operation = operations.result[0];
                  database.close();
                  resolve(JSON.stringify({
                    version: database.version,
                    stores,
                    snapshotCount: snapshots.result.length,
                    operationCount: operations.result.length,
                    operationId: operation?.id,
                    operationSchema: operation?.schemaVersion,
                    operationKind: operation?.kind,
                    operationStatus: operation?.status,
                    codIsNull: operation?.cod === null,
                    rejectionCodeIsNull: operation?.rejectionCode === null,
                    failedDeliveryIsNull: operation?.failedDelivery === null,
                  }));
                };
              };
            })
            """);
        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("version").GetInt32());
        Assert.Equal(
            ["dispatch_contacts", "operations", "organization_roles", "proof_blobs", "snapshots", "sync_leases"],
            root.GetProperty("stores")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(0, root.GetProperty("snapshotCount").GetInt32());
        Assert.Equal(1, root.GetProperty("operationCount").GetInt32());
        Assert.Equal(operationId, root.GetProperty("operationId").GetString());
        Assert.Equal(3, root.GetProperty("operationSchema").GetInt32());
        Assert.Equal("CHECK_IN", root.GetProperty("operationKind").GetString());
        // The scheduler may already have tried once (the stops request is aborted): never lost, never sent.
        Assert.Contains(root.GetProperty("operationStatus").GetString(), new[] { "PENDING", "RETRY_WAIT" });
        Assert.True(root.GetProperty("codIsNull").GetBoolean());
        Assert.True(root.GetProperty("rejectionCodeIsNull").GetBoolean());
        Assert.True(root.GetProperty("failedDeliveryIsNull").GetBoolean());
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: IndexedDB version 4 rewrites a queued schema 2 operation (the
    /// version 3 layout of UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11) as operation schema 3 in place and keeps the
    /// schema 2 stop snapshot, which the app still shows offline. The same upgrade continues to version 5
    /// (DRV-DISPATCH-CONTACT-2026-10-12).
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task IndexedDb_v3_upgrade_keeps_schema_2_operations_and_the_snapshot()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string cacheNamespace = "opaque-drv-failed-upgrade";
        const string operationId = "44444444-4444-4444-8444-444444444444";
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
                // The UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 (version 3) layout.
                const open = indexedDB.open("paquetenvia-driver-stops-v1", 3);
                open.onupgradeneeded = () => {
                  const database = open.result;
                  database.createObjectStore("snapshots", { keyPath: "partitionKey" });
                  const operations = database.createObjectStore(
                    "operations",
                    { keyPath: ["partitionKey", "id"] });
                  operations.createIndex("by_partition", "partitionKey");
                  operations.createIndex("by_partition_order", ["partitionKey", "orderId"]);
                  operations.createIndex("by_partition_status", ["partitionKey", "status"]);
                  operations.createIndex("by_partition_next_attempt", ["partitionKey", "nextAttemptAt"]);
                  operations.createIndex("by_partition_created", ["partitionKey", "createdAt", "id"]);
                  const blobs = database.createObjectStore(
                    "proof_blobs",
                    { keyPath: ["partitionKey", "operationId"] });
                  blobs.createIndex("by_partition", "partitionKey");
                  database.createObjectStore("sync_leases", { keyPath: "partitionKey" });
                };
                open.onerror = () => reject(open.error);
                open.onsuccess = () => {
                  const database = open.result;
                  const transaction = database.transaction(
                    ["snapshots", "operations"],
                    "readwrite");
                  const now = new Date(Date.now() - 60_000).toISOString();
                  transaction.objectStore("snapshots").put({
                    partitionKey,
                    schemaVersion: 2,
                    synchronizedAt: now,
                    stops: [{
                      order_id: value.orderId,
                      aggregate_version: 7,
                      order_public_id: "ORD_DRV_V3_UPGRADE",
                      stop_type: "DELIVERY",
                      status: "IN_TRANSIT",
                      address_summary: "Zona sintética v3",
                      details_available: false,
                      contact_short_name: null,
                      contact_full_name: null,
                      zone_name: null,
                      service_window: null,
                      cod: null,
                      navigation: null,
                    }],
                    completedToday: 0,
                  });
                  transaction.objectStore("operations").put({
                    schemaVersion: 2,
                    partitionKey,
                    id: value.operationId,
                    orderId: value.orderId,
                    kind: "START_DELIVERY",
                    sourceStatus: "IN_TRANSIT",
                    targetStatus: "DELIVERING",
                    expectedVersion: 7,
                    reason: "DRIVER_DELIVERY_STARTED",
                    clientOccurredAt: now,
                    createdAt: now,
                    transitionIdempotencyKey: `drv2-${value.operationId}-transition`,
                    sessionIdempotencyKey: null,
                    finalizeIdempotencyKey: null,
                    proofType: null,
                    contentType: null,
                    sizeBytes: null,
                    sha256: null,
                    capturedAt: null,
                    status: "PENDING",
                    attemptCount: 0,
                    nextAttemptAt: now,
                    sessionAttempt: 1,
                    uploadSessionId: null,
                    uploadSessionExpiresAt: null,
                    uploadAccepted: false,
                    proofId: null,
                    safeError: null,
                    cod: null,
                    rejectionCode: null,
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
                operationId,
            });
        await page.RouteAsync(
            DriverStopsPwaPlaywrightTests.StopsRoute,
            route => route.AbortAsync());

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);

        await ExpectTextAsync(page, "1 acción en el dispositivo");
        // The kept snapshot: the stop is still shown offline, with the start of the delivery pending.
        await ExpectTextAsync(page, "ORD_DRV_V3_UPGRADE");
        var evidence = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1");
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const transaction = database.transaction(
                  ["snapshots", "operations"],
                  "readonly");
                const snapshots = transaction.objectStore("snapshots").getAll();
                const operations = transaction.objectStore("operations").getAll();
                transaction.onerror = () => {
                  database.close();
                  reject(transaction.error);
                };
                transaction.oncomplete = () => {
                  const operation = operations.result[0];
                  database.close();
                  resolve(JSON.stringify({
                    version: database.version,
                    snapshotCount: snapshots.result.length,
                    operationCount: operations.result.length,
                    operationSchema: operation?.schemaVersion,
                    operationKind: operation?.kind,
                    failedDeliveryIsNull: operation?.failedDelivery === null,
                  }));
                };
              };
            })
            """);
        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("version").GetInt32());
        Assert.Equal(1, root.GetProperty("snapshotCount").GetInt32());
        Assert.Equal(1, root.GetProperty("operationCount").GetInt32());
        Assert.Equal(3, root.GetProperty("operationSchema").GetInt32());
        Assert.Equal("START_DELIVERY", root.GetProperty("operationKind").GetString());
        Assert.True(root.GetProperty("failedDeliveryIsNull").GetBoolean());
    }

    /// <summary>
    /// DRV-DISPATCH-CONTACT-2026-10-12: version 4 already reached main, so the dispatch contact store comes with
    /// IndexedDB version 5. The upgrade from the version 4 layout only adds that store: the queued schema 3 operation,
    /// the schema 2 snapshot (still shown offline) and the saved roles answer stay exactly as they were.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task IndexedDb_v4_upgrade_only_adds_the_dispatch_contact_store()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        const string cacheNamespace = "opaque-drv-dispatch-upgrade";
        const string operationId = "55555555-5555-4555-8555-555555555555";
        await InstallSessionAsync(page, cacheNamespace);
        await page.GotoAsync(server.BaseAddress.AbsoluteUri);
        var seeded = await page.EvaluateAsync<string>(
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
              const now = new Date(Date.now() - 60_000).toISOString();
              const operation = {
                schemaVersion: 3,
                partitionKey,
                id: value.operationId,
                orderId: value.orderId,
                kind: "START_DELIVERY",
                sourceStatus: "IN_TRANSIT",
                targetStatus: "DELIVERING",
                expectedVersion: 7,
                reason: "DRIVER_DELIVERY_STARTED",
                clientOccurredAt: now,
                createdAt: now,
                transitionIdempotencyKey: `drv2-${value.operationId}-transition`,
                sessionIdempotencyKey: null,
                finalizeIdempotencyKey: null,
                proofType: null,
                contentType: null,
                sizeBytes: null,
                sha256: null,
                capturedAt: null,
                status: "PENDING",
                attemptCount: 0,
                nextAttemptAt: now,
                sessionAttempt: 1,
                uploadSessionId: null,
                uploadSessionExpiresAt: null,
                uploadAccepted: false,
                proofId: null,
                safeError: null,
                cod: null,
                rejectionCode: null,
                failedDelivery: null,
              };
              const roles = { partitionKey, schemaVersion: 1, roles: ["DRIVER"] };
              await new Promise((resolve, reject) => {
                // The UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (version 4) layout, as main has it.
                const open = indexedDB.open("paquetenvia-driver-stops-v1", 4);
                open.onupgradeneeded = () => {
                  const database = open.result;
                  database.createObjectStore("snapshots", { keyPath: "partitionKey" });
                  const operations = database.createObjectStore(
                    "operations",
                    { keyPath: ["partitionKey", "id"] });
                  operations.createIndex("by_partition", "partitionKey");
                  operations.createIndex("by_partition_order", ["partitionKey", "orderId"]);
                  operations.createIndex("by_partition_status", ["partitionKey", "status"]);
                  operations.createIndex("by_partition_next_attempt", ["partitionKey", "nextAttemptAt"]);
                  operations.createIndex("by_partition_created", ["partitionKey", "createdAt", "id"]);
                  const blobs = database.createObjectStore(
                    "proof_blobs",
                    { keyPath: ["partitionKey", "operationId"] });
                  blobs.createIndex("by_partition", "partitionKey");
                  database.createObjectStore("sync_leases", { keyPath: "partitionKey" });
                  database.createObjectStore("organization_roles", { keyPath: "partitionKey" });
                };
                open.onerror = () => reject(open.error);
                open.onsuccess = () => {
                  const database = open.result;
                  const transaction = database.transaction(
                    ["snapshots", "operations", "organization_roles"],
                    "readwrite");
                  transaction.objectStore("snapshots").put({
                    partitionKey,
                    schemaVersion: 2,
                    synchronizedAt: now,
                    stops: [{
                      order_id: value.orderId,
                      aggregate_version: 7,
                      order_public_id: "ORD_DRV_V4_UPGRADE",
                      stop_type: "DELIVERY",
                      status: "IN_TRANSIT",
                      address_summary: "Zona sintética v4",
                      details_available: false,
                      contact_short_name: null,
                      contact_full_name: null,
                      zone_name: null,
                      service_window: null,
                      cod: null,
                      navigation: null,
                    }],
                    completedToday: 0,
                  });
                  transaction.objectStore("operations").put(operation);
                  transaction.objectStore("organization_roles").put(roles);
                  transaction.oncomplete = () => {
                    database.close();
                    resolve(undefined);
                  };
                  transaction.onerror = () => reject(transaction.error);
                };
              });
              return JSON.stringify({ operation, roles });
            }
            """,
            new
            {
                cacheNamespace,
                organizationId = OrganizationId,
                orderId = OrderId,
                operationId,
            });
        await page.RouteAsync(
            DriverStopsPwaPlaywrightTests.StopsRoute,
            route => route.AbortAsync());
        // The saved roles answer is the one in use: the read fails, so nothing replaces it.
        await page.RouteAsync(DriverOrganizationRoles.Route, route => route.AbortAsync());
        await page.RouteAsync(DriverDispatchContact.Route, route => route.AbortAsync());

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);

        await ExpectTextAsync(page, "1 acción en el dispositivo");
        await ExpectTextAsync(page, "ORD_DRV_V4_UPGRADE");
        var evidence = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1");
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const stores = [...database.objectStoreNames].sort();
                const transaction = database.transaction(
                  ["snapshots", "operations", "organization_roles", "dispatch_contacts"],
                  "readonly");
                const snapshots = transaction.objectStore("snapshots").getAll();
                const operations = transaction.objectStore("operations").getAll();
                const roles = transaction.objectStore("organization_roles").getAll();
                const contacts = transaction.objectStore("dispatch_contacts").getAll();
                transaction.onerror = () => {
                  database.close();
                  reject(transaction.error);
                };
                transaction.oncomplete = () => {
                  database.close();
                  resolve(JSON.stringify({
                    version: database.version,
                    stores,
                    snapshotCount: snapshots.result.length,
                    operations: operations.result,
                    roles: roles.result,
                    contactCount: contacts.result.length,
                  }));
                };
              };
            })
            """);
        using var before = JsonDocument.Parse(seeded);
        using var document = JsonDocument.Parse(evidence);
        var root = document.RootElement;
        Assert.Equal(5, root.GetProperty("version").GetInt32());
        Assert.Equal(
            ["dispatch_contacts", "operations", "organization_roles", "proof_blobs", "snapshots", "sync_leases"],
            root.GetProperty("stores")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(1, root.GetProperty("snapshotCount").GetInt32());
        var operation = Assert.Single(root.GetProperty("operations").EnumerateArray().ToArray());
        Assert.Equal(operationId, operation.GetProperty("id").GetString());
        Assert.Equal(3, operation.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("START_DELIVERY", operation.GetProperty("kind").GetString());
        // The scheduler may already have tried once (the stops request is aborted): never lost, never sent.
        Assert.Contains(operation.GetProperty("status").GetString(), new[] { "PENDING", "RETRY_WAIT" });
        Assert.Equal(
            before.RootElement.GetProperty("roles").GetRawText(),
            Assert.Single(root.GetProperty("roles").EnumerateArray().ToArray()).GetRawText());
        Assert.Equal(0, root.GetProperty("contactCount").GetInt32());
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 ("Solo confirma el monto"): at the door the driver confirms
    /// "Recibí $1,250.00"; the cash is queued with its own key and a reference of tracking number and phone time,
    /// the delivery photo stays disabled until then, and on sync the cash is recorded before the delivery.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Cash_is_confirmed_at_the_door_and_recorded_before_the_delivery()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-cod-main");
        var backend = new SyntheticOfflineBackend { CodExpectedCents = 125_000 };
        backend.SetOrderState("IN_TRANSIT", 7);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Entregar a M. Sintética");
        await ExpectTextAsync(page, "por cobrar al entregar");

        await context.SetOfflineAsync(true);
        try
        {
            await EnqueueButtonAsync(page, "Llegué al destino");
            await ExpectTextAsync(page, "Registra el cobro antes de confirmar la entrega.");
            Assert.True(await page.Locator("input[type=file]").IsDisabledAsync());

            await page.GetByRole(AriaRole.Button, new() { Name = "Recibí $1,250.00" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "¿Recibiste $1,250.00?" });
            await dialog.WaitForAsync();
            Assert.Contains("$1,250.00", await dialog.InnerTextAsync());
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Sí, recibí $1,250.00" }).ClickAsync();
            await ExpectTextAsync(page, "Cobro guardado en el teléfono; se registrará al sincronizar.");
            await WaitForOperationCountAsync(page, 2);
            Assert.False(await page.Locator("input[type=file]").IsDisabledAsync());

            await EnqueueProofAsync(page, "Confirmar entrega", "delivery-cod.png");
            await WaitForOperationCountAsync(page, 3);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
        await WaitForOperationCountAsync(page, 0);

        Assert.Equal(
            ["transition:DELIVERING", "cod", "transition:DELIVERED"],
            backend.Calls);
        var record = Assert.Single(backend.CodRecords);
        Assert.Equal(125_000, record.AmountCents);
        Assert.Matches(
            "^ORD_DRV002_SYNTHETIC \\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}[+-]\\d{2}:\\d{2}$",
            record.Reference);
        Assert.Matches("^drv2-[0-9a-f-]{36}-cod$", record.Key);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: a delivery the server refuses with an ORD-002 rule code shows that
    /// rule in Spanish instead of the generic "la parada cambió", also after a reload.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Refused_delivery_shows_the_rule_in_spanish()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-cod-rule");
        var backend = new SyntheticOfflineBackend
        {
            CodExpectedCents = 125_000,
            CodRecorded = true,
            RejectDeliveryAsCodNotRecorded = true,
        };
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "El cobro ya está registrado.");
        await EnqueueProofAsync(page, "Confirmar entrega", "delivery-rule.png");

        const string rule =
            "Falta registrar el cobro contra entrega. Registra el cobro antes de confirmar la entrega.";
        await ExpectTextAsync(page, rule);
        await page.ReloadAsync();
        await ExpectTextAsync(page, rule);
        Assert.DoesNotContain(
            "porque la parada cambió",
            await page.Locator("body").InnerTextAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 review: discarding a held cash collection never dead-ends the
    /// delivery. The saved delivery waits for the cash, "Recibí" is offered again and the new collection reaches
    /// the server before the delivery, although the delivery was saved first.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Discarded_cash_keeps_the_delivery_waiting_until_the_cash_is_recorded_again()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-cod-discard");
        var backend = new SyntheticOfflineBackend { CodExpectedCents = 125_000, RejectFirstCod = true };
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "por cobrar al entregar");
        await context.SetOfflineAsync(true);
        try
        {
            await ConfirmCashAsync(page);
            await WaitForOperationCountAsync(page, 1);
            await EnqueueProofAsync(page, "Confirmar entrega", "delivery-discard.png");
            await WaitForOperationCountAsync(page, 2);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
        await ExpectTextAsync(page, "Acción que requiere atención");
        Assert.Equal(["cod"], backend.Calls);

        await page.GetByRole(AriaRole.Button, new() { Name = "Descartar" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirmar", Exact = true }).ClickAsync();
        await ExpectTextAsync(
            page,
            "La entrega ya está guardada en el teléfono. Se enviará después de registrar el cobro.");
        await WaitForOperationCountAsync(page, 1);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
        await ExpectTextAsync(
            page,
            "La entrega ya está guardada en el teléfono. Se enviará después de registrar el cobro.");
        Assert.Equal(["cod"], backend.Calls);
        Assert.Empty(backend.TransitionTargets);

        await ConfirmCashAsync(page);
        await WaitForOperationCountAsync(page, 0);

        Assert.Equal(["cod", "cod", "transition:DELIVERED"], backend.Calls);
        Assert.Single(backend.CodRecords);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: "No se pudo entregar" online. "¿Qué pasó?" offers the closed list
    /// without "No pagó el cobro" (this stop collects no cash), "Otro…" reveals three more reasons, there is no text
    /// field, and "Enviar reporte" waits for a reason and the photo. The report runs the AI-05 sequence once:
    /// DELIVERY_PHOTO session, upload, proof, the incident without severity, description or next_action, and
    /// FAILED_ATTEMPT with that incident; the stop then reads "Intento fallido · El despacho decide".
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Failed_delivery_is_reported_with_reason_and_photo_and_dispatch_decides()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-failed-online");
        await RecordRevokedObjectUrlsAsync(page);
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Entregar a M. Sintética");
        await OpenFailedDeliveryAsync(page);
        await ExpectTextAsync(page, "El despacho decidirá si se reprograma o regresa");
        foreach (var reason in new[] { "No había nadie", "Dirección incorrecta", "Rechazó el paquete", "Otro…" })
        {
            await page.GetByRole(AriaRole.Radio, new() { Name = reason, Exact = true }).WaitForAsync();
            var option = await page.Locator("label", new() { HasText = reason }).BoundingBoxAsync();
            Assert.NotNull(option);
            Assert.True(option.Height >= 44, $"{reason} is {option.Height}px high");
        }
        Assert.Equal(0, await page.GetByRole(AriaRole.Radio, new() { Name = "No pagó el cobro" }).CountAsync());
        Assert.Equal(0, await page.Locator("textarea, input[type=text], input:not([type])").CountAsync());
        var send = page.GetByRole(AriaRole.Button, new() { Name = "Enviar reporte" });
        Assert.True(await send.IsDisabledAsync());

        await page.GetByRole(AriaRole.Radio, new() { Name = "Otro…" }).CheckAsync();
        foreach (var reason in new[] { "Acceso restringido", "Paquete dañado", "Riesgo de seguridad" })
        {
            await page.GetByRole(AriaRole.Radio, new() { Name = reason, Exact = true }).WaitForAsync();
        }
        await page.GetByRole(AriaRole.Radio, new() { Name = "Paquete dañado" }).CheckAsync();
        Assert.True(await send.IsDisabledAsync());

        var previewUrl = await ChooseFailedDeliveryPhotoAsync(page);
        Assert.True(await IsObjectUrlRevokedAsync(page, previewUrl));
        // Previewing and choosing the photo store nothing; only "Enviar reporte" does.
        Assert.Equal(0, await ReadStoreCountAsync(page, "operations"));
        Assert.False(await send.IsDisabledAsync());
        await send.ClickAsync();

        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        await ExpectTextAsync(page, "Intento fallido · El despacho decide");
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());

        Assert.Equal(["incident", "transition:FAILED_ATTEMPT"], backend.Calls);
        var session = Assert.Single(backend.SessionAttempts);
        Assert.Equal("DELIVERY_PHOTO", session.ProofType);
        Assert.Equal(1, backend.UploadCount);
        var proof = Assert.Single(backend.FinalizedProofs);
        Assert.Equal("DELIVERY_PHOTO", proof.ProofType);
        var (incidentKey, incidentBody) = Assert.Single(backend.IncidentRequests);
        Assert.Matches("^drv2-[0-9a-f-]{36}-incident$", incidentKey);
        using (var incident = JsonDocument.Parse(incidentBody))
        {
            var report = incident.RootElement;
            Assert.Equal(
                ["evidence_proof_ids", "occurred_at", "reason_code", "type"],
                report.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("FAILED_DELIVERY_ATTEMPT", report.GetProperty("type").GetString());
            Assert.Equal("PACKAGE_DAMAGED", report.GetProperty("reason_code").GetString());
            Assert.Equal(proof.CapturedAt, report.GetProperty("occurred_at").GetString());
            Assert.Equal(
                "00000000-0000-4000-8000-000000000001",
                Assert.Single(report.GetProperty("evidence_proof_ids").EnumerateArray()).GetString());
        }
        Assert.Equal(["FAILED_ATTEMPT"], backend.TransitionTargets);
        Assert.Equal(Assert.Single(backend.IncidentIds), Assert.Single(backend.TransitionIncidentIds));
        Assert.Equal("DRIVER_DELIVERY_FAILED_REPORTED", Assert.Single(backend.TransitionReasons));
        Assert.Equal(8, Assert.Single(backend.TransitionExpectedVersions));
        Assert.Equal(proof.CapturedAt, Assert.Single(backend.TransitionClientOccurredAt));
        var keys = new[] { session.Key, Assert.Single(backend.FinalizeKeys.Distinct()), incidentKey, Assert.Single(backend.TransitionKeys) };
        Assert.Equal(4, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.StartsWith("drv2-", key, StringComparison.Ordinal));
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: offline, "No pagó el cobro" (the stop collects cash) is saved with
    /// its photo and shown as pending, never as confirmed, also after a reload. When the connection returns it is sent
    /// once, without the cash, and the stop reads "Intento fallido · El despacho decide".
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Failed_delivery_saved_offline_is_sent_once_when_the_connection_returns()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-failed-offline");
        var backend = new SyntheticOfflineBackend { CodExpectedCents = 125_000 };
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "por cobrar al entregar");
        // The reload below is offline, so the Service Worker must control the page and hold the shell first.
        await EnsureServiceWorkerControlAsync(page);
        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "por cobrar al entregar");
        // ORG-MY-ROLES-2026-10-12: the answer of the roles read is saved for the partition, role codes only.
        var saved = Assert.Single(await DriverOrganizationRoles.WaitForSavedAsync(page, 1));
        Assert.Equal(
            ["partitionKey", "roles", "schemaVersion"],
            saved.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["DRIVER"], saved.GetProperty("roles").EnumerateArray().Select(role => role.GetString() ?? string.Empty));
        // A page that went away mid-pass (the navigations above) may leave its sync lease active for 15 seconds; the
        // sync asked for when the connection returns then runs once that lease expired, within the waits below
        // (docs/development/driver-offline-replay-flake.md).
        await context.SetOfflineAsync(true);
        try
        {
            // An offline start reads the roles saved for the partition, so a DRIVER still reports.
            await page.ReloadAsync();
            await ExpectTextAsync(page, "por cobrar al entregar");
            await OpenFailedDeliveryAsync(page);
            await page.GetByRole(AriaRole.Radio, new() { Name = "No pagó el cobro" }).CheckAsync();
            await ChooseFailedDeliveryPhotoAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Enviar reporte" }).ClickAsync();
            await WaitForOperationCountAsync(page, 1);
            await WaitForProofCountAsync(page, 1);
            await ExpectTextAsync(page, "Reporte guardado en el teléfono. Se enviará cuando haya conexión.");
            await AssertPendingFailedAttemptAsync(page);

            await page.ReloadAsync();
            await ExpectTextAsync(page, "1 acción en el dispositivo");
            await ExpectTextAsync(page, "Reporte guardado en el teléfono. Se enviará cuando haya conexión.");
            await AssertPendingFailedAttemptAsync(page);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        await ExpectTextAsync(page, "Intento fallido · El despacho decide");

        Assert.Equal(["incident", "transition:FAILED_ATTEMPT"], backend.Calls);
        Assert.Empty(backend.CodRecords);
        var (_, incidentBody) = Assert.Single(backend.IncidentRequests);
        using var incident = JsonDocument.Parse(incidentBody);
        Assert.Equal("PAYMENT_UNAVAILABLE", incident.RootElement.GetProperty("reason_code").GetString());
        var capturedAt = Assert.Single(backend.SessionClientOccurredAt);
        Assert.Equal(capturedAt, incident.RootElement.GetProperty("occurred_at").GetString());
        Assert.Equal(capturedAt, Assert.Single(backend.TransitionClientOccurredAt));
        Assert.Equal(capturedAt, Assert.Single(backend.FinalizedProofs).CapturedAt);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (owner answer "No, llama a despacho (Recommended)"): with the cash
    /// saved on the phone "No se pudo entregar" is not offered; discarding the saved collection, with the existing
    /// confirmation, brings it back. With the cash recorded on the server the stop asks the driver to call dispatch.
    /// DRV-DISPATCH-CONTACT-2026-10-12: the organization has no dispatch number configured (the API answers
    /// phone_e164 null), so the text stays as it is and no "Llamar a despacho" link is shown.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Saved_cash_hides_the_report_until_discarded_and_recorded_cash_asks_to_call_dispatch()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-failed-cash");
        await DriverDispatchContact.RouteAsync(page, null);
        var backend = new SyntheticOfflineBackend { CodExpectedCents = 125_000 };
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "por cobrar al entregar");
        await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).WaitForAsync();
        await context.SetOfflineAsync(true);
        try
        {
            await ConfirmCashAsync(page);
            await WaitForOperationCountAsync(page, 1);
            await ExpectTextAsync(
                page,
                "Hay un cobro guardado en el teléfono. Si fue un error, descártalo para poder reportar que no se pudo entregar.");
            Assert.Equal(
                0,
                await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());

            await page.GetByRole(AriaRole.Button, new() { Name = "Descartar cobro guardado" }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Confirma la acción" });
            await dialog.WaitForAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Confirmar", Exact = true }).ClickAsync();
            await WaitForOperationCountAsync(page, 0);
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Recibí $1,250.00" }).WaitForAsync();
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }
        Assert.Empty(backend.Calls);

        backend.RecordCodOnServer();
        await page.ReloadAsync();
        await ExpectTextAsync(
            page,
            "El cobro ya está registrado. Si no puedes entregar, llama a despacho: ellos deciden y coordinan la devolución del cobro.");
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());
        // DRV-DISPATCH-CONTACT-2026-10-12: the "none configured" answer is saved like any answer, and no link shows.
        var saved = Assert.Single(await DriverDispatchContact.WaitForSavedAsync(page, 1));
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("phoneE164").ValueKind);
        Assert.Empty(await DriverDispatchContact.LinksAsync(page));
        Assert.DoesNotContain("tel:", await page.ContentAsync(), StringComparison.Ordinal);
        Assert.Empty(backend.Calls);
    }

    /// <summary>
    /// DRV-DISPATCH-CONTACT-2026-10-12 (owner answer "Sí, configurable (Recommended)"): with the organization's
    /// dispatch number configured (synthetic here), the recorded-cash notice of the stop keeps its text and gets
    /// "Llamar a despacho" next to it, a 44 px tel: link to that number. The answer is saved for the partition (the
    /// number only), so the link is still there after an offline reload. Without a number the text stays alone (the
    /// test above).
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Recorded_cash_offers_Llamar_a_despacho_with_the_configured_number_also_offline()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-dispatch-call");
        await DriverDispatchContact.RouteAsync(page, DriverDispatchContact.SyntheticNumber);
        var backend = new SyntheticOfflineBackend { CodExpectedCents = 125_000, CodRecorded = true };
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);
        var stop = new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri;

        await page.GotoAsync(stop);
        await ExpectTextAsync(page, RecordedCashCallDispatchText);
        // The reload below is offline, so the Service Worker must control the page and hold the shell first.
        await EnsureServiceWorkerControlAsync(page);
        await page.GotoAsync(stop);
        await AssertRecordedCashCallsDispatchAsync(page);
        var saved = Assert.Single(await DriverDispatchContact.WaitForSavedAsync(page, 1));
        Assert.Equal(
            ["partitionKey", "phoneE164", "schemaVersion"],
            saved.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(DriverDispatchContact.SyntheticNumber, saved.GetProperty("phoneE164").GetString());
        Assert.Equal(1, saved.GetProperty("schemaVersion").GetInt32());

        await context.SetOfflineAsync(true);
        try
        {
            // An offline start reads the number saved for the partition.
            await page.ReloadAsync();
            await AssertRecordedCashCallsDispatchAsync(page);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());
        Assert.Empty(backend.Calls);
    }

    private const string RecordedCashCallDispatchText =
        "El cobro ya está registrado. Si no puedes entregar, llama a despacho: ellos deciden y coordinan la devolución del cobro.";

    /// <summary>The recorded-cash notice with "Llamar a despacho" in the same section: a 44 px tel: link.</summary>
    private static async Task AssertRecordedCashCallsDispatchAsync(IPage page)
    {
        var section = page.GetByRole(AriaRole.Region, new() { Name = "No se pudo entregar", Exact = true });
        await section.GetByRole(AriaRole.Link, new() { Name = "Llamar a despacho", Exact = true }).WaitForAsync();
        Assert.Contains(RecordedCashCallDispatchText, await section.InnerTextAsync(), StringComparison.Ordinal);
        var (href, width, height) = Assert.Single(await DriverDispatchContact.LinksAsync(page));
        Assert.Equal("tel:" + DriverDispatchContact.SyntheticNumber, href);
        Assert.True(width >= 44 && height >= 44, $"{width}x{height}");
        // The number is dialed, never written on the screen.
        Assert.DoesNotContain(DriverDispatchContact.SyntheticNumber, await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a report saved offline is held, unsent, when the cash was recorded
    /// on the server meanwhile (for example by dispatch): nothing is uploaded or opened, and the stop explains that
    /// the driver calls dispatch; only discarding remains.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Report_saved_offline_is_held_when_the_cash_was_recorded_meanwhile()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-failed-held");
        // DRV-DISPATCH-CONTACT-2026-10-12: the organization has a (synthetic) dispatch number.
        await DriverDispatchContact.RouteAsync(page, DriverDispatchContact.SyntheticNumber);
        var backend = new SyntheticOfflineBackend { CodExpectedCents = 125_000 };
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "por cobrar al entregar");
        await context.SetOfflineAsync(true);
        try
        {
            await OpenFailedDeliveryAsync(page);
            await page.GetByRole(AriaRole.Radio, new() { Name = "Rechazó el paquete" }).CheckAsync();
            await ChooseFailedDeliveryPhotoAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Enviar reporte" }).ClickAsync();
            await WaitForOperationCountAsync(page, 1);
        }
        finally
        {
            backend.RecordCodOnServer();
            await context.SetOfflineAsync(false);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
        await WaitForOperationStatusAsync(page, "NEEDS_ATTENTION");
        await ExpectTextAsync(
            page,
            "El cobro de esta entrega ya está registrado, así que el reporte no se envió. Llama a despacho: ellos deciden y coordinan la devolución del cobro.");
        // DRV-DISPATCH-CONTACT-2026-10-12: "Llamar a despacho" next to the notice, the only one on the screen.
        var notice = page.GetByRole(AriaRole.Region, new() { Name = "Acción que requiere atención", Exact = true });
        await notice.GetByRole(AriaRole.Link, new() { Name = "Llamar a despacho", Exact = true }).WaitForAsync();
        var (href, width, height) = Assert.Single(await DriverDispatchContact.LinksAsync(page));
        Assert.Equal("tel:" + DriverDispatchContact.SyntheticNumber, href);
        Assert.True(width >= 44 && height >= 44, $"{width}x{height}");
        Assert.Empty(backend.Calls);
        Assert.Empty(backend.SessionAttempts);
        Assert.Equal(0, backend.UploadCount);
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "Reintentar la misma acción" }).CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Descartar", Exact = true }).WaitForAsync();
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: a report saved offline is never taken as confirmed by a failed
    /// attempt that dispatch registered on its own. It is held unsent (no photo upload, no incident, no transition),
    /// says so, keeps its photo until the driver discards it, and offers no rebuild for the new version.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Report_saved_offline_is_held_unsent_when_dispatch_registered_the_failed_attempt()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-failed-dispatch-first");
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).WaitForAsync();
        await context.SetOfflineAsync(true);
        try
        {
            await OpenFailedDeliveryAsync(page);
            await page.GetByRole(AriaRole.Radio, new() { Name = "No había nadie" }).CheckAsync();
            await ChooseFailedDeliveryPhotoAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Enviar reporte" }).ClickAsync();
            await WaitForOperationCountAsync(page, 1);
            await WaitForProofCountAsync(page, 1);
        }
        finally
        {
            // Meanwhile dispatch registered the failed attempt with its own incident.
            backend.SetOrderState("FAILED_ATTEMPT", 9);
            await context.SetOfflineAsync(false);
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
        await WaitForOperationStatusAsync(page, "NEEDS_ATTENTION");
        await ExpectTextAsync(
            page,
            "Despacho ya registró el intento fallido de esta entrega, así que tu reporte no se envió. Descártalo.");
        Assert.Empty(backend.Calls);
        Assert.Empty(backend.SessionAttempts);
        Assert.Equal(0, backend.UploadCount);
        Assert.Empty(backend.IncidentRequests);
        await WaitForProofCountAsync(page, 1);
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "Crear acción para versión actual" }).CountAsync());
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "Reintentar la misma acción" }).CountAsync());

        await page.GetByRole(AriaRole.Button, new() { Name = "Descartar", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Confirma la acción" });
        await dialog.WaitForAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Confirmar", Exact = true }).ClickAsync();
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        await ExpectTextAsync(page, "Intento fallido · El despacho decide");
        Assert.Empty(backend.Calls);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 (owner answer "Registrar desde Despacho (Recommended)") with
    /// ORG-MY-ROLES-2026-10-12: a person whose roles read (getMyOrganizationRoles, the API's own answer) lists DRIVER
    /// and DISPATCHER is not offered "No se pudo entregar" and uploads nothing; the stop says to register the attempt
    /// from the order in Despacho.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task A_driver_who_is_also_dispatch_is_told_to_register_the_attempt_in_Despacho()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(
            page,
            "opaque-drv-failed-also-dispatch",
            OrganizationRole.Driver,
            OrganizationRole.Dispatcher);
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(
            page,
            "También eres despacho en esta organización, así que el intento fallido no se reporta desde el teléfono: regístralo desde la orden en Despacho, donde eliges el motivo y el siguiente paso.");
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());
        Assert.Empty(backend.Calls);
        var saved = Assert.Single(await DriverOrganizationRoles.WaitForSavedAsync(page, 1));
        Assert.Equal(
            ["DISPATCHER", "DRIVER"],
            saved.GetProperty("roles").EnumerateArray().Select(role => role.GetString() ?? string.Empty));
    }

    /// <summary>
    /// ORG-MY-ROLES-2026-10-12: this app against an API older than the roles read (404) withholds only "No se pudo
    /// entregar" - it says the role could not be confirmed and saves nothing - while the stop, the delivery and its
    /// sync work as before; no deployment order is needed.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Against_an_older_API_without_the_roles_read_only_the_report_is_withheld()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-drv-failed-older-api");
        // Registered last, so it answers the roles read instead of InstallSessionAsync's route.
        await DriverOrganizationRoles.RouteOlderApiAsync(page);
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("DELIVERING", 8);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(
            page,
            "No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar.");
        Assert.DoesNotContain("Cierra sesión", await page.InnerTextAsync("main"), StringComparison.Ordinal);
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());
        Assert.Empty(await DriverOrganizationRoles.ReadSavedAsync(page));

        await EnqueueProofAsync(page, "Confirmar entrega", "delivery-older-api.png");
        await WaitUntilAsync(() => backend.Calls.Contains("transition:DELIVERED"));
        await WaitForOperationCountAsync(page, 0);
        Assert.Equal(["transition:DELIVERED"], backend.Calls);
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
    public async Task Proof_photo_is_previewed_retaken_and_queued_only_from_Usar_esta_foto()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-ui001-photo-preview");
        await RecordRevokedObjectUrlsAsync(page);
        var backend = new SyntheticOfflineBackend();
        backend.SetOrderState("AT_PICKUP", 4);
        await backend.RouteAsync(page);
        var secondSha256 = Convert.ToHexStringLower(SHA256.HashData(SecondPreviewPng));

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Confirmar recolección");
        await context.SetOfflineAsync(true);
        try
        {
            await page.Locator("input[type=file]").SetInputFilesAsync(
                new FilePayload
                {
                    Name = "first-preview.png",
                    MimeType = "image/png",
                    Buffer = FirstPreviewPng,
                });
            var firstUrl = await WaitForDecodedPreviewAsync(page);
            Assert.StartsWith("blob:", firstUrl, StringComparison.Ordinal);
            Assert.False(await IsObjectUrlRevokedAsync(page, firstUrl));
            await page.WaitForFunctionAsync(
                "() => document.activeElement?.textContent === 'Vista previa de la foto'");
            // Previewing stores nothing.
            Assert.Equal(0, await ReadStoreCountAsync(page, "operations"));
            Assert.Equal(0, await ReadStoreCountAsync(page, "proof_blobs"));

            // "Repetir" reopens the camera picker and revokes the first preview.
            var chooser = await page.RunAndWaitForFileChooserAsync(
                () => page.GetByRole(AriaRole.Button, new() { Name = "Repetir" })
                    .ClickAsync());
            Assert.True(await IsObjectUrlRevokedAsync(page, firstUrl));
            await chooser.SetFilesAsync(
                new FilePayload
                {
                    Name = "second-preview.png",
                    MimeType = "image/png",
                    Buffer = SecondPreviewPng,
                });
            var secondUrl = await WaitForDecodedPreviewAsync(page, firstUrl);
            Assert.Equal(0, await ReadStoreCountAsync(page, "operations"));

            await page.GetByRole(AriaRole.Button, new() { Name = "Usar esta foto" })
                .ClickAsync();
            await WaitForOperationCountAsync(page, 1);
            await WaitForProofCountAsync(page, 1);
            await ExpectTextAsync(page, "Iniciar traslado");
            Assert.True(await IsObjectUrlRevokedAsync(page, secondUrl));
            Assert.Equal(
                $"{SecondPreviewPng.Length}|{secondSha256}|{secondSha256}",
                await ReadSingleProofAsync(page));
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        await page.WaitForFunctionAsync("() => navigator.onLine === true");
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);
        // Only the retaken photo travels: one upload, sessions for its hash only.
        Assert.Equal(1, backend.UploadCount);
        Assert.Equal([secondSha256], backend.SessionSha256.Distinct());
        Assert.Equal(["PICKED_UP"], backend.TransitionTargets);
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

    /// <summary>
    /// SEC-004 (owner answers Q2, "Si se pasa, la app espera lo que indique el servidor y sigue sola", and Q6, "la
    /// cola espera lo que indique el servidor y reintenta sin mensaje"): a 429 on the photo is neither rejected
    /// evidence nor a message; the queue waits the Retry-After and syncs the same photo, with the same key, by itself.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Photo_429_waits_the_server_and_syncs_by_itself_without_a_message()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-sec004-photo-429");
        var backend = new SyntheticOfflineBackend { RateLimitFirstSessionSeconds = 2 };
        backend.SetOrderState("AT_PICKUP", 4);
        await backend.RouteAsync(page);

        await page.GotoAsync(
            new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await EnqueueProofAsync(page, "Confirmar recolección", "rate-limited.png");
        await WaitForOperationStatusAsync(page, "RETRY_WAIT");

        // While it waits: no attention notice, no rejected evidence.
        Assert.Equal(0, await page.GetByText("Acción que requiere atención").CountAsync());
        Assert.Equal(0, await page.GetByText("requiere atención").CountAsync());

        // No "Sincronizar ahora": the queue resumes by itself once the 2 s have passed.
        await WaitForOperationCountAsync(page, 0);
        await WaitForProofCountAsync(page, 0);

        var limitedKey = Assert.Single(backend.RateLimitedSessionKeys);
        Assert.Equal(limitedKey, Assert.Single(backend.SessionAttempts.Select(attempt => attempt.Key).Distinct(StringComparer.Ordinal)));
        Assert.Single(backend.FinalizedProofs);
        Assert.Single(backend.TransitionTargets);
        Assert.Equal(1, backend.UploadCount);
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
        await page.RouteAsync(DriverStopsPwaPlaywrightTests.StopsRoute, async route =>
        {
            var organization =
                await route.Request.HeaderValueAsync("X-Organization-Id");
            var suffix = organization == organizationA ? "A" : "B";
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = DriverStopsPwaPlaywrightTests.StopsBody(
                    route.Request,
                    [
                        DriverStopsPwaPlaywrightTests.Stop(
                            OrderId,
                            3,
                            $"ORD_DRV002_ORG_{suffix}",
                            "PICKUP",
                            "ASSIGNED",
                            $"Zona sintética {suffix}"),
                    ]),
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
                  const cache = await caches.open("paquetenvia-driver-shell-v8");
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

    /// <summary>"No se pudo entregar" opens "¿Qué pasó?".</summary>
    private static async Task OpenFailedDeliveryAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Group, new() { Name = "¿Qué pasó?" }).WaitForAsync();
    }

    /// <summary>
    /// "Foto del lugar (obligatoria)": the photo is previewed and used; returns the revoked preview URL.
    /// </summary>
    private static async Task<string> ChooseFailedDeliveryPhotoAsync(IPage page)
    {
        await page.GetByLabel("Foto del lugar (obligatoria)").SetInputFilesAsync(
            new FilePayload
            {
                Name = "lugar-sintetico.png",
                MimeType = "image/png",
                Buffer = FirstPreviewPng,
            });
        await page.GetByRole(AriaRole.Heading, new() { Name = "Vista previa de la foto" }).WaitForAsync();
        var url = await page.GetByRole(AriaRole.Img, new() { Name = "Foto del lugar tomada" })
            .GetAttributeAsync("src") ?? string.Empty;
        Assert.StartsWith("blob:", url, StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Button, new() { Name = "Usar esta foto" }).ClickAsync();
        await ExpectTextAsync(page, "Foto lista para el reporte.");
        return url;
    }

    /// <summary>A report waiting to sync is pending: the confirmed status is still the delivery.</summary>
    private static async Task AssertPendingFailedAttemptAsync(IPage page)
    {
        var facts = await page.Locator("dl").First.InnerTextAsync();
        Assert.Contains("Estado confirmado", facts, StringComparison.Ordinal);
        Assert.Contains("En reparto", facts, StringComparison.Ordinal);
        Assert.Contains("Estado por sincronizar", facts, StringComparison.Ordinal);
        Assert.Contains("Intento fallido", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("El despacho decide", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Recibí $1,250.00" }).CountAsync());
        Assert.Equal(0, await page.Locator("input[type=file]").CountAsync());
    }

    private static async Task ConfirmCashAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Recibí $1,250.00" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "¿Recibiste $1,250.00?" });
        await dialog.WaitForAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Sí, recibí $1,250.00" }).ClickAsync();
    }

    private static async Task EnqueueButtonAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = name }).ClickAsync();
        await page.WaitForFunctionAsync(
            "label => !document.body.getAttribute('aria-busy') && document.body.innerText.includes(label)",
            NextLabel(name));
    }

    /// <summary>
    /// UI-001 phase 3: the step names the action, the photo is shown in
    /// "Vista previa de la foto" and only "Usar esta foto" queues it.
    /// </summary>
    private static async Task EnqueueProofAsync(
        IPage page,
        string action,
        string fileName)
    {
        await ExpectTextAsync(page, action);
        await page.Locator("input[type=file]").SetInputFilesAsync(
            new FilePayload
            {
                Name = fileName,
                MimeType = "image/png",
                Buffer = SyntheticPng,
            });
        await page.GetByRole(
                AriaRole.Heading,
                new() { Name = "Vista previa de la foto" })
            .WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Usar esta foto" })
            .ClickAsync();
        await page.WaitForTimeoutAsync(100);
    }

    /// <summary>
    /// Waits until the preview shows a decoded image other than
    /// <paramref name="previous"/> and returns its object URL.
    /// </summary>
    private static async Task<string> WaitForDecodedPreviewAsync(
        IPage page,
        string? previous = null)
    {
        await page.WaitForFunctionAsync(
            """
            previous => {
              const image = document.querySelector('img[alt="Foto de evidencia tomada"]');
              return image !== null && image.src !== previous &&
                image.complete && image.naturalWidth > 0;
            }
            """,
            previous);
        return await page.GetByRole(
                AriaRole.Img,
                new() { Name = "Foto de evidencia tomada" })
            .GetAttributeAsync("src") ?? string.Empty;
    }

    /// <summary>
    /// Records every <c>URL.revokeObjectURL</c> call. The page CSP keeps
    /// <c>blob:</c> out of <c>connect-src</c>, so a revoked URL cannot be
    /// probed with <c>fetch</c>.
    /// </summary>
    private static Task RecordRevokedObjectUrlsAsync(IPage page) =>
        page.AddInitScriptAsync(
            """
            (() => {
              const revoked = [];
              const revoke = URL.revokeObjectURL.bind(URL);
              URL.revokeObjectURL = url => {
                revoked.push(String(url));
                revoke(url);
              };
              window.__paquetenviaRevokedObjectUrls = revoked;
            })();
            """);

    private static Task<bool> IsObjectUrlRevokedAsync(IPage page, string url) =>
        page.EvaluateAsync<bool>(
            "url => window.__paquetenviaRevokedObjectUrls.includes(url)",
            url);

    /// <summary>
    /// The stored proof's recorded size and SHA-256, plus the SHA-256 of the
    /// stored Blob bytes themselves.
    /// </summary>
    private static Task<string> ReadSingleProofAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("proof_blobs", "readonly")
                  .objectStore("proof_blobs").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = async () => {
                  database.close();
                  if (request.result.length !== 1) {
                    reject(new Error("expected exactly one stored proof"));
                    return;
                  }
                  const proof = request.result[0];
                  const digest = new Uint8Array(await crypto.subtle.digest(
                    "SHA-256",
                    await proof.blob.arrayBuffer()));
                  const blobSha256 = [...digest]
                    .map(value => value.toString(16).padStart(2, "0"))
                    .join("");
                  resolve(`${proof.sizeBytes}|${proof.sha256}|${blobSha256}`);
                };
              };
            })
            """);

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
              const cache = await caches.open("paquetenvia-driver-shell-v8");
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
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
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
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
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

    /// <summary>
    /// Installs the driver session and answers its roles read (ORG-MY-ROLES-2026-10-12) with the API's own answer for
    /// a person whose memberships hold <paramref name="roles"/> (DRIVER only by default); see
    /// <see cref="DriverOrganizationRoles"/>.
    /// </summary>
    private static async Task InstallSessionAsync(
        IPage page,
        string cacheNamespace,
        params OrganizationRole[] roles)
    {
        await page.RouteAsync("**/hubs/driver/negotiate**", route => route.AbortAsync());
        await DriverOrganizationRoles.RouteAsync(page, roles);
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
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
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
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
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
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
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
        private readonly Dictionary<string, string> incidentsByKey = [];
        private int incidentSequence;
        private string status = "ASSIGNED";
        private int version = 3;
        private bool codRecorded;
        private int sessionSequence;
        private bool firstTransition = true;
        private bool firstUpload = true;
        private bool firstFinalize = true;
        private bool firstCod = true;

        internal bool AbortFirstTransition { get; init; }
        internal bool AbortFirstUpload { get; init; }
        internal bool AbortFirstFinalize { get; init; }

        /// <summary>
        /// SEC-004: answers the first upload session request with the API's 429 (TooManyRequests, Retry-After in
        /// seconds) and records nothing for it, as the rate limiter rejects before the endpoint runs.
        /// </summary>
        internal int? RateLimitFirstSessionSeconds { get; init; }
        internal List<string> RateLimitedSessionKeys { get; } = [];
        internal bool ConflictFirstTransition { get; init; }
        internal bool ExpireTransitions { get; init; }

        /// <summary>UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: cash the DELIVERY stop expects; 0 means none.</summary>
        internal long CodExpectedCents { get; init; }
        internal bool CodRecorded
        {
            get { lock (gate) return codRecorded; }
            init => codRecorded = value;
        }

        /// <summary>Refuses DELIVERED with the ORD-002 rule code COD_NOT_RECORDED, whatever the cash state.</summary>
        internal bool RejectDeliveryAsCodNotRecorded { get; init; }

        /// <summary>Refuses the first cash collection with COD_AMOUNT_MISMATCH, recording nothing.</summary>
        internal bool RejectFirstCod { get; init; }
        internal List<string> Calls { get; } = [];
        internal List<(string Key, long AmountCents, string Reference)> CodRecords { get; } = [];
        internal int? StopsFailureStatus { get; set; }
        internal bool HideOrder { get; set; }
        internal List<string> TransitionTargets { get; } = [];
        internal List<string> TransitionKeys { get; } = [];
        internal List<string?> TransitionClientOccurredAt { get; } = [];
        internal List<string?> SessionClientOccurredAt { get; } = [];
        internal List<string?> SessionSha256 { get; } = [];
        internal List<(string Key, string ProofType)> SessionAttempts { get; } = [];
        internal List<string> FinalizeKeys { get; } = [];
        internal List<(string ProofType, string CapturedAt)> FinalizedProofs { get; } = [];
        internal int UploadCount { get; private set; }

        /// <summary>UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12: every openIncident request, key and exact body.</summary>
        internal List<(string Key, string Body)> IncidentRequests { get; } = [];
        internal List<string> IncidentIds { get; } = [];
        internal List<string?> TransitionIncidentIds { get; } = [];
        internal List<string?> TransitionReasons { get; } = [];
        internal List<int> TransitionExpectedVersions { get; } = [];

        internal void SetOrderState(string nextStatus, int nextVersion)
        {
            lock (gate)
            {
                status = nextStatus;
                version = nextVersion;
            }
        }

        /// <summary>Dispatch records the cash on the server while the phone is offline.</summary>
        internal void RecordCodOnServer()
        {
            lock (gate)
            {
                codRecorded = true;
            }
        }

        internal async Task RouteAsync(IPage page)
        {
            await page.RouteAsync(DriverStopsPwaPlaywrightTests.StopsRoute, RouteStopsAsync);
            await page.RouteAsync("**/api/v1/orders/*/transitions", RouteTransitionAsync);
            await page.RouteAsync(
                "**/api/v1/orders/*/proof-upload-sessions",
                RouteSessionAsync);
            await page.RouteAsync("**/api/v1/orders/*/proofs", RouteFinalizeAsync);
            await page.RouteAsync("**/api/v1/orders/*/cod-records", RouteCodAsync);
            await page.RouteAsync("**/api/v1/orders/*/incidents", RouteIncidentAsync);
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
                var stopType = status is "ASSIGNED" or "AT_PICKUP" ? "PICKUP" : "DELIVERY";
                payload = HideOrder || status == "DELIVERED"
                    ? []
                    :
                    [
                        DriverStopsPwaPlaywrightTests.Stop(
                            OrderId,
                            version,
                            "ORD_DRV002_SYNTHETIC",
                            stopType,
                            status,
                            "Zona sintética DRV-002",
                            contactShortName: stopType == "PICKUP" ? null : "M. Sintética",
                            contactFullName: stopType == "PICKUP" ? "Farmacia Sintética" : null,
                            codExpectedCents: stopType == "DELIVERY" && CodExpectedCents > 0
                                ? CodExpectedCents
                                : null,
                            codRecorded: codRecorded),
                    ];
            }
            return route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json; charset=utf-8",
                Body = DriverStopsPwaPlaywrightTests.StopsBody(route.Request, payload),
            });
        }

        private async Task RouteTransitionAsync(IRoute route)
        {
            var request = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
            var target = request.GetProperty("target_status").GetString()!;
            var key = await route.Request.HeaderValueAsync("Idempotency-Key") ?? string.Empty;
            var abort = false;
            var codMissing = false;
            var incidentMissing = false;
            var incidentId = request.TryGetProperty("metadata", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                metadata.TryGetProperty("incident_id", out var incident)
                    ? incident.GetString()
                    : null;
            lock (gate)
            {
                TransitionTargets.Add(target);
                TransitionKeys.Add(key);
                TransitionClientOccurredAt.Add(ReadClientOccurredAt(request));
                TransitionIncidentIds.Add(incidentId);
                TransitionReasons.Add(request.GetProperty("reason").GetString());
                TransitionExpectedVersions.Add(request.GetProperty("expected_version").GetInt32());
                Calls.Add($"transition:{target}");
                abort = AbortFirstTransition && firstTransition;
                // The DELIVERED guard if_cod_expected_then_cod_status_recorded_or_reconciled.
                codMissing = target == "DELIVERED" &&
                    (RejectDeliveryAsCodNotRecorded || (CodExpectedCents > 0 && !codRecorded));
                // AI-04 attempt_stage_recorded: a FAILED_ATTEMPT names an incident opened for this order.
                incidentMissing = target == "FAILED_ATTEMPT" &&
                    (incidentId is null || !IncidentIds.Contains(incidentId));
                if (!(ConflictFirstTransition && firstTransition) && !ExpireTransitions && !codMissing &&
                    !incidentMissing)
                {
                    status = target;
                    version += 1;
                }
                firstTransition = false;
            }
            if (incidentMissing)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Conflict.","status":409,"code":"INCIDENT_REQUIRED"}""",
                });
                return;
            }
            if (codMissing)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Conflict.","status":409,"code":"COD_NOT_RECORDED"}""",
                });
                return;
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
                    // ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02: the API always returns the order window.
                    service_window = (object?)null,
                },
                200));
        }

        private async Task RouteSessionAsync(IRoute route)
        {
            var request = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
            var proofType = request.GetProperty("proof_type").GetString()!;
            var key = await route.Request.HeaderValueAsync("Idempotency-Key") ?? string.Empty;
            int? rateLimited = null;
            lock (gate)
            {
                if (RateLimitFirstSessionSeconds is int seconds && RateLimitedSessionKeys.Count == 0)
                {
                    RateLimitedSessionKeys.Add(key);
                    rateLimited = seconds;
                }
            }
            if (rateLimited is int retryAfter)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 429,
                    ContentType = "application/problem+json",
                    Headers = new Dictionary<string, string>
                    {
                        ["Retry-After"] = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["Cache-Control"] = "no-store",
                    },
                    Body = """{"type":"about:blank","title":"Too Many Requests","status":429,"traceId":"0123456789abcdef0123456789abcdef"}""",
                });
                return;
            }

            string sessionId;
            lock (gate)
            {
                SessionAttempts.Add((key, proofType));
                SessionClientOccurredAt.Add(ReadClientOccurredAt(request));
                SessionSha256.Add(
                    request.TryGetProperty("sha256", out var sha256)
                        ? sha256.GetString()
                        : null);
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

        private async Task RouteCodAsync(IRoute route)
        {
            var request = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
            var key = await route.Request.HeaderValueAsync("Idempotency-Key") ?? string.Empty;
            var amount = request.GetProperty("amount_cents").GetInt64();
            var reference = request.GetProperty("reference").GetString() ?? string.Empty;
            bool reject;
            lock (gate)
            {
                Calls.Add("cod");
                reject = RejectFirstCod && firstCod;
                firstCod = false;
                if (!reject)
                {
                    CodRecords.Add((key, amount, reference));
                    codRecorded = amount == CodExpectedCents;
                }
            }
            if (reject)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Conflict.","status":409,"code":"COD_AMOUNT_MISMATCH"}""",
                });
                return;
            }
            await route.FulfillAsync(Json(
                new
                {
                    id = Guid.Parse("abababab-abab-4bab-8bab-abababababab"),
                    order_id = OrderId,
                    amount_cents = amount,
                    status = "RECORDED",
                    recorded_at = "2026-10-11T20:05:43+00:00",
                    reconciled_at = (string?)null,
                },
                201));
        }

        /// <summary>
        /// AI-05 openIncident for a DRIVER report (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11): severity,
        /// description or next_action in the body is 409 INVALID_REQUEST; the same key replays the same incident.
        /// </summary>
        private async Task RouteIncidentAsync(IRoute route)
        {
            var body = route.Request.PostData ?? "{}";
            var request = JsonDocument.Parse(body).RootElement.Clone();
            var key = await route.Request.HeaderValueAsync("Idempotency-Key") ?? string.Empty;
            var driverShape =
                !request.TryGetProperty("severity", out _) &&
                !request.TryGetProperty("description", out _) &&
                !request.TryGetProperty("next_action", out _);
            string incidentId;
            lock (gate)
            {
                Calls.Add("incident");
                IncidentRequests.Add((key, body));
                if (!incidentsByKey.TryGetValue(key, out var existing))
                {
                    incidentSequence += 1;
                    existing = $"00000000-0000-4000-8000-{(900 + incidentSequence).ToString().PadLeft(12, '0')}";
                    if (driverShape)
                    {
                        incidentsByKey.Add(key, existing);
                        IncidentIds.Add(existing);
                    }
                }
                incidentId = existing;
            }
            if (!driverShape)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 409,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Conflict.","status":409,"code":"INVALID_REQUEST"}""",
                });
                return;
            }
            var reason = request.GetProperty("reason_code").GetString()!;
            var occurredAt = request.GetProperty("occurred_at").GetString()!;
            var severity = reason is "PACKAGE_DAMAGED" or "SECURITY_RISK" ? "HIGH" : "MEDIUM";
            await route.FulfillAsync(Json(
                new
                {
                    id = incidentId,
                    order_id = OrderId,
                    status = "OPEN",
                    severity,
                    reason_code = reason,
                    next_action = (string?)null,
                    custody_acquired = true,
                    occurred_at = occurredAt,
                    sla_due_at = DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture)
                        .AddHours(severity == "HIGH" ? 8 : 24)
                        .ToUniversalTime()
                        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
                    evidence_proof_ids = request.GetProperty("evidence_proof_ids")
                        .EnumerateArray()
                        .Select(value => value.GetString())
                        .ToArray(),
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
