using System.Net.Http.Json;
using Identity.Infrastructure.Mock;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Dispatching;

namespace Paqueteria.IntegrationTests.Realtime;

[Collection(RealtimePostgreSqlKestrelCollection.Name)]
public sealed class RealtimeOutboxPlaywrightTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    private static readonly Guid OrderId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    [Trait("Category", "OutboxSignalRDelivery")]
    public async Task Browser_receives_outbox_delivery_deduplicates_isolates_and_resynchronizes()
    {
        var recorder = new RealtimeAuthorizationRecorder();
        var failureInjector = new OneShotRealtimeOutboxFailureInjector(
            RealtimeOutboxLane.Business);
        var initialHost = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString,
            failureInjector: failureInjector);
        var baseAddress = initialHost.Start();
        using (var healthClient = new HttpClient { BaseAddress = baseAddress })
        using (var healthResponse = await healthClient.GetAsync("/health/ready"))
        {
            healthResponse.EnsureSuccessStatusCode();
            var health = await healthResponse.Content.ReadFromJsonAsync<HealthResponse>();
            Assert.Equal("healthy", health?.Status);
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        await page.GotoAsync(baseAddress.AbsoluteUri);
        await page.SetContentAsync(
            """
            <!doctype html>
            <html>
              <body>
                <output id="operations-status">snapshot</output>
                <output id="operations-version">0</output>
                <output id="operations-applied">0</output>
                <output id="operations-deliveries">0</output>
                <output id="operations-sync-count">0</output>
                <output id="tracking-status">snapshot</output>
                <output id="tracking-version">0</output>
                <output id="tracking-applied">0</output>
                <output id="tracking-deliveries">0</output>
                <output id="lifecycle"></output>
              </body>
            </html>
            """);
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Path = FindSignalRBrowserBundle(),
        });

        await page.EvaluateAsync(
            """
            async config => {
              const seenOperations = new Set();
              const seenTracking = new Set();
              const state = {
                operationsVersions: new Map(),
                trackingVersion: 0,
                operationsEventIds: [],
                trackingEventIds: [],
              };
              const text = (id, value) => {
                document.getElementById(id).textContent = String(value);
              };
              const increment = id => {
                const element = document.getElementById(id);
                element.textContent = String(Number(element.textContent) + 1);
              };
              const synchronizeOperations = async () => {
                const response = await fetch(`${config.baseUrl}/api/v1/orders`, {
                  cache: "no-store",
                  headers: {
                    Authorization: `Bearer ${config.operationsToken}`,
                    "X-Organization-Id": config.organizationId,
                  },
                });
                if (!response.ok) {
                  throw new Error(`Operations REST sync failed: ${response.status}`);
                }
                const snapshot = await response.json();
                const replacement = new Map(
                  snapshot.items.map(order => [order.id, order.version]));
                state.operationsVersions = replacement;
                const order = snapshot.items.find(value => value.id === config.orderId);
                if (!order) throw new Error("Operations snapshot omitted the target order.");
                text("operations-version", order.version);
                text("operations-status", order.status);
                increment("operations-sync-count");
              };
              const synchronizeTracking = async () => {
                const response = await fetch(
                  `${config.baseUrl}/__tests/tracking/${config.trackingToken}`,
                  { cache: "no-store" });
                if (!response.ok) throw new Error(`REST sync failed: ${response.status}`);
                const snapshot = await response.json();
                state.trackingVersion = snapshot.aggregate_version;
                text("tracking-version", snapshot.aggregate_version);
                text("tracking-status", snapshot.public_status);
              };
              await Promise.all([synchronizeOperations(), synchronizeTracking()]);

              const operations = new signalR.HubConnectionBuilder()
                .withUrl(
                  `${config.baseUrl}/hubs/operations?organization_id=${config.organizationId}`,
                  {
                    accessTokenFactory: () => config.operationsToken,
                    transport: signalR.HttpTransportType.WebSockets,
                    skipNegotiation: true,
                  })
                .withAutomaticReconnect([0, 250, 1000, 2000, 5000, 10000])
                .build();
              operations.onreconnecting(() => text("lifecycle", "Reconnecting"));
              operations.onreconnected(async () => {
                text("lifecycle", "Reconnecting,Reconnected");
                await Promise.all([synchronizeOperations(), synchronizeTracking()]);
              });
              operations.on("OrderStatusChanged", message => {
                increment("operations-deliveries");
                state.operationsEventIds.push(message.event_id);
                const knownVersion =
                  state.operationsVersions.get(message.aggregate_id) ?? 0;
                if (seenOperations.has(message.event_id) ||
                    message.aggregate_version <= knownVersion) return;
                seenOperations.add(message.event_id);
                state.operationsVersions.set(
                  message.aggregate_id,
                  message.aggregate_version);
                text("operations-version", message.aggregate_version);
                text("operations-status", message.payload.new_status);
                increment("operations-applied");
              });

              const tracking = new signalR.HubConnectionBuilder()
                .withUrl(`${config.baseUrl}/hubs/tracking`, {
                  accessTokenFactory: () => config.trackingToken,
                  transport: signalR.HttpTransportType.WebSockets,
                  skipNegotiation: true,
                })
                .withAutomaticReconnect([0, 250, 1000, 2000, 5000, 10000])
                .build();
              tracking.on("PublicOrderStatusChanged", message => {
                increment("tracking-deliveries");
                state.trackingEventIds.push(message.event_id);
                if (seenTracking.has(message.event_id) ||
                    message.aggregate_version <= state.trackingVersion) return;
                seenTracking.add(message.event_id);
                state.trackingVersion = message.aggregate_version;
                text("tracking-version", message.aggregate_version);
                text("tracking-status", message.payload.public_status);
                increment("tracking-applied");
              });

              window.rtm002 = {
                operations,
                tracking,
                state,
                synchronizeOperations,
                synchronizeTracking,
              };
              await operations.start();
              await tracking.start();
            }
            """,
            new
            {
                baseUrl = baseAddress.GetLeftPart(UriPartial.Authority),
                organizationId =
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"),
                orderId = OrderId.ToString("D"),
                operationsToken = MockIdentityProfiles.ActivePlatformAdminMfa,
                trackingToken = PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
            });

        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextTrackingAcceptedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("1", await page.Locator("#operations-sync-count").TextContentAsync());

        var publicEvent = await database.EnqueueRealtimeStatusAsync(available: false);
        failureInjector.Arm(publicEvent.OutboxId);
        await database.MakeBusinessOutboxAvailableAsync(publicEvent.OutboxId);
        await page.WaitForFunctionAsync(
            "() => Number(document.querySelector('#operations-deliveries').textContent) === 1");
        await failureInjector.WaitForInterruptionAsync(TimeSpan.FromSeconds(10));
        var interrupted = await database.ReadOutboxDeliveryStateAsync(publicEvent.OutboxId);
        Assert.NotNull(interrupted);
        Assert.Equal("PROCESSING", interrupted.Status);
        Assert.Equal(1, interrupted.Attempts);
        Assert.NotNull(interrupted.LeaseToken);

        await page.WaitForFunctionAsync(
            "() => Number(document.querySelector('#operations-deliveries').textContent) === 2",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#operations-version').textContent) === version",
            publicEvent.AggregateVersion);
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#tracking-version').textContent) === version",
            publicEvent.AggregateVersion);
        Assert.Equal("DELIVERING", await page.Locator("#operations-status").TextContentAsync());
        Assert.Equal("OUT_FOR_DELIVERY", await page.Locator("#tracking-status").TextContentAsync());
        Assert.Equal("1", await page.Locator("#operations-applied").TextContentAsync());
        Assert.Equal("1", await page.Locator("#tracking-applied").TextContentAsync());
        Assert.Equal("2", await page.Locator("#operations-deliveries").TextContentAsync());
        Assert.Equal("2", await page.Locator("#tracking-deliveries").TextContentAsync());
        var operationsEventIds = await page.EvaluateAsync<string[]>(
            "() => window.rtm002.state.operationsEventIds");
        var trackingEventIds = await page.EvaluateAsync<string[]>(
            "() => window.rtm002.state.trackingEventIds");
        Assert.Equal(2, operationsEventIds.Length);
        Assert.Single(operationsEventIds.Distinct(StringComparer.Ordinal));
        Assert.All(
            operationsEventIds,
            value => Assert.Equal(publicEvent.OutboxId.ToString("D"), value));
        Assert.Equal(operationsEventIds, trackingEventIds);
        var processedAfterRedelivery = await WaitForProcessedStateAsync(publicEvent.OutboxId);
        Assert.Equal(2, processedAfterRedelivery.Attempts);
        Assert.Null(processedAfterRedelivery.LeaseToken);

        var privateEvent = await database.EnqueueRealtimeStatusAsync(isPublic: false);
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#operations-version').textContent) === version",
            privateEvent.AggregateVersion);
        await Task.Delay(300);
        Assert.Equal("CLOSED", await page.Locator("#operations-status").TextContentAsync());
        Assert.Equal("2", await page.Locator("#tracking-deliveries").TextContentAsync());
        Assert.DoesNotContain(
            "lat",
            await page.Locator("body").TextContentAsync() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var port = baseAddress.Port;
        await initialHost.DisposeAsync();
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#lifecycle').textContent === 'Reconnecting'");
        var missedEvent = await database.EnqueueRealtimeStatusAsync();

        await using var restoredHost = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            port,
            database.WorkerConnectionString);
        Assert.Equal(baseAddress, restoredHost.Start());
        await page.WaitForFunctionAsync(
            "() => document.querySelector('#lifecycle').textContent === 'Reconnecting,Reconnected'",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#operations-version').textContent) === version",
            missedEvent.AggregateVersion);
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#tracking-version').textContent) === version",
            missedEvent.AggregateVersion);
        var operationsSyncCount = int.Parse(
            await page.Locator("#operations-sync-count").TextContentAsync() ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(
            operationsSyncCount >= 2,
            $"Operations REST synchronized only {operationsSyncCount} time(s).");
        Assert.Equal(
            missedEvent.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await page.Locator("#operations-version").TextContentAsync());
        Assert.Equal(
            missedEvent.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await page.Locator("#tracking-version").TextContentAsync());
        Assert.Equal("DELIVERING", await page.Locator("#operations-status").TextContentAsync());
        Assert.Equal("OUT_FOR_DELIVERY", await page.Locator("#tracking-status").TextContentAsync());
        Assert.Equal("1", await page.Locator("#tracking-applied").TextContentAsync());
        Assert.Equal("PROCESSED", await WaitForProcessedAsync(missedEvent.OutboxId));

        var appliedBeforeDelayed = await page.Locator("#operations-applied").TextContentAsync();
        var deliveriesBeforeDelayed = int.Parse(
            await page.Locator("#operations-deliveries").TextContentAsync() ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        var delayedOutboxId = await database.EnqueueDelayedStatusFromEvidenceAsync(
            publicEvent.OrderEventId);
        Assert.Equal("PROCESSED", await WaitForProcessedAsync(delayedOutboxId));
        await page.WaitForFunctionAsync(
            "count => Number(document.querySelector('#operations-deliveries').textContent) > count",
            deliveriesBeforeDelayed);
        Assert.Equal(
            appliedBeforeDelayed,
            await page.Locator("#operations-applied").TextContentAsync());
        Assert.Equal(
            missedEvent.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await page.Locator("#operations-version").TextContentAsync());

        await page.EvaluateAsync(
            "async () => Promise.all([window.rtm002.operations.stop(), window.rtm002.tracking.stop()])");
    }

    private async Task<string?> WaitForProcessedAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var status = await database.ReadOutboxStatusAsync(id);
            if (status == "PROCESSED")
            {
                return status;
            }

            await Task.Delay(50, timeout.Token);
        }

        return await database.ReadOutboxStatusAsync(id);
    }

    private async Task<PostgreSqlSecurityWebApplicationFactory.OutboxDeliveryState>
        WaitForProcessedStateAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var state = await database.ReadOutboxDeliveryStateAsync(id);
            if (state?.Status == "PROCESSED")
            {
                return state;
            }

            await Task.Delay(50, timeout.Token);
        }

        throw new TimeoutException("The redelivered outbox row did not reach PROCESSED.");
    }

    private static string FindSignalRBrowserBundle()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Paqueteria.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ??
            throw new InvalidOperationException("Repository root was not found.");
        var path = Path.Combine(
            root,
            "apps",
            "web",
            "node_modules",
            "@microsoft",
            "signalr",
            "dist",
            "browser",
            "signalr.js");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "Install the frozen web workspace before running Playwright.",
                path);
    }

    private sealed record HealthResponse(string Status);
}
