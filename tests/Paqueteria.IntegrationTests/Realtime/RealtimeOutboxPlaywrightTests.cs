using System.Net.Http.Json;
using Identity.Infrastructure.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Security;
using Realtime.Application.Events;
using Realtime.Application.Publishing;

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
        var initialHost = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString);
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
                <output id="tracking-status">snapshot</output>
                <output id="tracking-version">0</output>
                <output id="tracking-applied">0</output>
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
              const state = { operationsVersion: 0, trackingVersion: 0 };
              const text = (id, value) => {
                document.getElementById(id).textContent = String(value);
              };
              const increment = id => {
                const element = document.getElementById(id);
                element.textContent = String(Number(element.textContent) + 1);
              };
              const synchronize = async () => {
                const response = await fetch(
                  `${config.baseUrl}/__tests/tracking/${config.trackingToken}`,
                  { cache: "no-store" });
                if (!response.ok) throw new Error(`REST sync failed: ${response.status}`);
                const snapshot = await response.json();
                state.trackingVersion = snapshot.aggregate_version;
                text("tracking-version", snapshot.aggregate_version);
                text("tracking-status", snapshot.public_status);
              };
              await synchronize();

              const operations = new signalR.HubConnectionBuilder()
                .withUrl(
                  `${config.baseUrl}/hubs/operations?organization_id=${config.organizationId}`,
                  {
                    accessTokenFactory: () => config.operationsToken,
                    transport: signalR.HttpTransportType.WebSockets,
                    skipNegotiation: true,
                  })
                .withAutomaticReconnect([0, 100, 250, 500, 1000])
                .build();
              operations.onreconnecting(() => text("lifecycle", "Reconnecting"));
              operations.onreconnected(async () => {
                text("lifecycle", "Reconnecting,Reconnected");
                await synchronize();
              });
              operations.on("OrderStatusChanged", message => {
                if (seenOperations.has(message.event_id) ||
                    message.aggregate_version <= state.operationsVersion) return;
                seenOperations.add(message.event_id);
                state.operationsVersion = message.aggregate_version;
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
                .withAutomaticReconnect([0, 100, 250, 500, 1000])
                .build();
              tracking.on("PublicOrderStatusChanged", message => {
                if (seenTracking.has(message.event_id) ||
                    message.aggregate_version <= state.trackingVersion) return;
                seenTracking.add(message.event_id);
                state.trackingVersion = message.aggregate_version;
                text("tracking-version", message.aggregate_version);
                text("tracking-status", message.payload.public_status);
                increment("tracking-applied");
              });

              window.rtm002 = { operations, tracking, state, synchronize };
              await operations.start();
              await tracking.start();
            }
            """,
            new
            {
                baseUrl = baseAddress.GetLeftPart(UriPartial.Authority),
                organizationId =
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"),
                operationsToken = MockIdentityProfiles.ActivePlatformAdminMfa,
                trackingToken = PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
            });

        Assert.True(await recorder.WaitForNextOperationsAcceptedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await recorder.WaitForNextTrackingAcceptedAsync(TimeSpan.FromSeconds(5)));

        var publicEvent = await database.EnqueueRealtimeStatusAsync();
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

        await PublishOperationsAsync(
            initialHost,
            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
            publicEvent.OutboxId,
            publicEvent.AggregateVersion);
        await PublishOperationsAsync(
            initialHost,
            PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
            Guid.NewGuid(),
            publicEvent.AggregateVersion + 100);
        await Task.Delay(300);
        Assert.Equal("1", await page.Locator("#operations-applied").TextContentAsync());

        var privateEvent = await database.EnqueueRealtimeStatusAsync(isPublic: false);
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#operations-version').textContent) === version",
            privateEvent.AggregateVersion);
        await Task.Delay(300);
        Assert.Equal("CLOSED", await page.Locator("#operations-status").TextContentAsync());
        Assert.Equal(
            publicEvent.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await page.Locator("#tracking-version").TextContentAsync());
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
            "() => document.querySelector('#lifecycle').textContent === 'Reconnecting,Reconnected'");
        await page.WaitForFunctionAsync(
            "version => Number(document.querySelector('#tracking-version').textContent) === version",
            missedEvent.AggregateVersion);
        Assert.Equal(
            missedEvent.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await page.Locator("#tracking-version").TextContentAsync());
        Assert.Equal("OUT_FOR_DELIVERY", await page.Locator("#tracking-status").TextContentAsync());
        Assert.Equal("1", await page.Locator("#tracking-applied").TextContentAsync());
        Assert.Equal("PROCESSED", await WaitForProcessedAsync(missedEvent.OutboxId));

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

    private static Task PublishOperationsAsync(
        RealtimeKestrelWebApplicationFactory host,
        Guid organizationId,
        Guid eventId,
        long version) =>
        host.Services.GetRequiredService<IRealtimePublisher>()
            .PublishOperationsOrderStatusChangedAsync(
                OperationsAudience.ForOrganization(organizationId),
                new RealtimeEnvelope<OrderStatusChangedPayload>(
                    eventId,
                    RealtimeEventTypes.OrderStatusChanged,
                    new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero),
                    OrderId,
                    version,
                    null,
                    new OrderStatusChangedPayload(
                        OrderId,
                        "IN_TRANSIT",
                        "DELIVERING",
                        new DateTimeOffset(2026, 7, 25, 3, 0, 0, TimeSpan.Zero))),
                default);

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
