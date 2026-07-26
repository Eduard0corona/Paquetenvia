using Identity.Infrastructure.Mock;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Driver;

[Collection(DriverStopsRealtimeCollection.Name)]
public sealed class DriverStopsRealtimePlaywrightTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Real_DriverHub_signal_debounces_and_resynchronizes_from_REST()
    {
        var nextPort = DriverStopsNextServer.ReservePort();
        var nextOrigin = $"http://127.0.0.1:{nextPort}";
        var recorder = new RealtimeAuthorizationRecorder();
        var scenario = await database.CreateDriverAudienceOutboxScenarioAsync(
            available: false);
        await using var api = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            workerConnectionString: database.WorkerConnectionString,
            allowedOrigin: nextOrigin,
            enableDispatch: true,
            enableDriverApiCors: true);
        var apiAddress = api.Start();
        await using var web = await DriverStopsNextServer.StartAsync(
            apiAddress.GetLeftPart(UriPartial.Authority),
            nextPort);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaDriverSession = {
              organizationId: "{{PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId:D}}",
              cacheNamespace: "opaque-real-driver-01",
              getAccessToken: () => "{{MockIdentityProfiles.ActiveDriver}}",
            };
            """);
        var restRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.EndsWith(
                    "/api/v1/driver/me/stops",
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref restRequests);
            }
        };

        await page.GotoAsync(new Uri(web.BaseAddress, "/driver/stops").AbsoluteUri);
        await page.GetByText(
                PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                new() { Exact = true })
            .WaitForAsync();
        Assert.Equal(1, Volatile.Read(ref restRequests));

        await database.MakeBusinessOutboxAvailableAsync(scenario.AssignmentOutboxId);
        await page.WaitForFunctionAsync(
            "() => document.body.textContent.includes('Actualizado')");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (Volatile.Read(ref restRequests) < 2 &&
               !timeout.IsCancellationRequested)
        {
            await Task.Delay(50, timeout.Token);
        }

        Assert.True(
            Volatile.Read(ref restRequests) >= 2,
            "DriverHub did not trigger a REST resynchronization.");
        var body = await page.Locator("body").InnerTextAsync();
        Assert.Contains(
            PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
            body);
        Assert.DoesNotContain(scenario.AssignmentId.ToString("D"), body);
        Assert.DoesNotContain(scenario.DriverId.ToString("D"), body);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverStopsRealtimeCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "Driver stops realtime";
}
