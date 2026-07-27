using System.Collections.Concurrent;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.Realtime;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Tracking;

[Collection(PublicTrackingPwaCollection.Name)]
public sealed class PublicTrackingPwaPlaywrightTests(
    PostgreSqlSecurityWebApplicationFactory database)
{
    [Fact]
    [Trait("Category", "PublicTrackingPwa")]
    public async Task Real_api_signalr_next_and_chromium_render_without_persisting_or_logging_token()
    {
        var nextPort = DriverStopsNextServer.ReservePort();
        var nextOrigin = $"http://127.0.0.1:{nextPort}";
        var recorder = new RealtimeAuthorizationRecorder();
        await using var api = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            recorder,
            allowedOrigin: nextOrigin,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["PublicTracking:AllowedOrigins:0"] = nextOrigin,
                ["PublicTracking:LookupPermitLimit"] = "1000",
            });
        var apiAddress = api.Start();
        await using var web = await DriverStopsNextServer.StartAsync(
            apiAddress.GetLeftPart(UriPartial.Authority),
            nextPort);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        foreach (var viewport in new[]
                 {
                     new ViewportSize { Width = 320, Height = 568 },
                     new ViewportSize { Width = 430, Height = 932 },
                 })
        {
            await using var context = await browser.NewContextAsync(
                new BrowserNewContextOptions { ViewportSize = viewport });
            var page = await context.NewPageAsync();
            var observedRequests = new ConcurrentQueue<IRequest>();
            var hubResponses = new ConcurrentQueue<string>();
            var browserDiagnostics = new ConcurrentQueue<string>();
            page.Request += (_, request) => observedRequests.Enqueue(request);
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    browserDiagnostics.Enqueue(Redact(message.Text));
                }
            };
            page.PageError += (_, error) =>
                browserDiagnostics.Enqueue(Redact(error));
            page.Response += (_, browserResponse) =>
            {
                var uri = new Uri(browserResponse.Url);
                if (uri.AbsolutePath.StartsWith(
                        "/hubs/tracking",
                        StringComparison.Ordinal))
                {
                    hubResponses.Enqueue(
                        $"{browserResponse.Status}:{uri.AbsolutePath}");
                }
            };

            var response = await page.GotoAsync(
                new Uri(
                    web.BaseAddress,
                    $"/track/{PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken}")
                .AbsoluteUri);
            Assert.NotNull(response);
            await page.GetByText(
                    PostgreSqlSecurityWebApplicationFactory.ValidPublicOrderId,
                    new() { Exact = true })
                .WaitForAsync();
            await page.Locator(".trackingStatus").GetByText(
                    "En reparto",
                    new() { Exact = true })
                .WaitForAsync();
            if (!await recorder.WaitForNextTrackingAcceptedAsync(
                    TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException(
                    $"Tracking did not connect. authorizations={recorder.TrackingCount}; " +
                    $"exception={recorder.TrackingException?.GetType().Name ?? "none"}; " +
                    $"responses={string.Join(',', hubResponses)}; " +
                    $"browser={string.Join(" | ", browserDiagnostics)}; " +
                    $"csp={(await response.AllHeadersAsync())["content-security-policy"]}; " +
                    $"ui={await page.Locator(".trackingUpdate").InnerTextAsync()}");
            }

            Assert.Equal(
                "no-referrer",
                (await response.AllHeadersAsync())["referrer-policy"]);
            Assert.Contains(
                "no-cache",
                (await response.AllHeadersAsync())["cache-control"]);
            Assert.Equal(
                "noindex, nofollow, noarchive",
                (await response.AllHeadersAsync())["x-robots-tag"]);
            Assert.False(await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth"));

            var apiRequest = observedRequests.Single(request =>
                request.Url.Contains("/api/v1/tracking/", StringComparison.Ordinal));
            var apiHeaders = await apiRequest.AllHeadersAsync();
            Assert.False(apiHeaders.ContainsKey("authorization"));
            Assert.False(apiHeaders.ContainsKey("cookie"));
            Assert.False(apiHeaders.ContainsKey("x-organization-id"));
            Assert.Equal(
                string.Empty,
                await page.EvaluateAsync<string>("() => document.referrer"));

            var persistence = await page.EvaluateAsync<PersistenceEvidence>(
                """
                async () => {
                  const cached = [];
                  for (const key of await caches.keys()) {
                    const cache = await caches.open(key);
                    for (const request of await cache.keys()) cached.push(request.url);
                  }
                  return {
                    local: Object.keys(localStorage)
                      .filter(key => !key.startsWith("__next_")),
                    session: Object.keys(sessionStorage)
                      .filter(key => !key.startsWith("__next_")),
                    databases: indexedDB.databases
                      ? (await indexedDB.databases())
                          .map(value => value.name ?? "")
                          .filter(name => !name.startsWith("__next_"))
                      : [],
                    cached,
                  };
                }
                """);
            Assert.Empty(persistence.Local);
            Assert.Empty(persistence.Session);
            Assert.DoesNotContain(
                persistence.Cached,
                value =>
                    value.Contains("/track", StringComparison.Ordinal) ||
                    value.Contains("/api/v1/tracking", StringComparison.Ordinal) ||
                    value.Contains("/hubs/tracking", StringComparison.Ordinal));

            var apiCount = observedRequests.Count(request =>
                request.Url.Contains("/api/v1/tracking/", StringComparison.Ordinal));
            await page.GotoAsync(new Uri(web.BaseAddress, "/track/not-valid").AbsoluteUri);
            await page.GetByText(
                    "No pudimos encontrar este seguimiento.",
                    new() { Exact = true })
                .WaitForAsync();
            Assert.Equal(
                apiCount,
                observedRequests.Count(request =>
                    request.Url.Contains("/api/v1/tracking/", StringComparison.Ordinal)));
        }

        Assert.False(web.OutputContains(
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken));
    }

    private sealed class PersistenceEvidence
    {
        public string[] Local { get; set; } = [];
        public string[] Session { get; set; } = [];
        public string[] Databases { get; set; } = [];
        public string[] Cached { get; set; } = [];
    }

    private static string Redact(string value) =>
        value.Replace(
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
            "[redacted]",
            StringComparison.Ordinal);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PublicTrackingPwaCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "PublicTrackingPwa";
}
