using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// SEC-004-PANEL-SCREENS-2026-10-13 in a real browser against the production Next build, the operations dashboard
/// side. Owner answers Q11 "Reintenta sola" and Q14 "Se pausan": the dashboard's first read meets the limit (429,
/// Retry-After: 2); while the wait lasts nothing is sent and "Actualizar" is disabled, but the filters stay usable and
/// a status chosen meanwhile is kept; when the wait ends the dashboard starts once, by itself, and that read carries
/// the chosen status (AI-07 /ops/dashboard rate_limit.at_zero, "with the filters on screen").
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed class OperationsDashboardRateLimitPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task A_status_chosen_during_the_wait_is_read_when_the_dashboard_starts_at_zero()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            if (!message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
            {
                console.Enqueue($"{message.Type}:{message.Text}");
            }
        };
        page.PageError += (_, error) => console.Enqueue($"page:{error}");

        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(OrganizationA)}},
              sessionNamespace: "sec-004-dashboard-playwright",
              getAccessToken: () => "synthetic-operations-token",
            };
            """);
        // Playwright runs the last matching route first: the catch-all is registered before the specific ones.
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", route => FulfillAsync(route, new[]
        {
            new { organization_id = OrganizationA, display_name = "Logística Sintética", role = "DISPATCHER", is_default = true },
        }));
        var dashboard = new DashboardApi { TooManyRequests = 1 };
        await page.RouteAsync("**/api/v1/operations/dashboard**", dashboard.HandleAsync);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/dashboard").AbsoluteUri);
        var countdown = page.Locator("main").GetByRole(AriaRole.Timer);
        await countdown.GetByText(
            "Demasiadas solicitudes. Espera 2 segundos e intenta de nuevo.",
            new() { Exact = true }).WaitForAsync();
        Assert.Equal(new[] { string.Empty }, dashboard.Queries);
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).IsDisabledAsync());

        // The filters stay usable: a status chosen during the wait is kept and nothing is sent.
        var status = page.Locator("fieldset.opsFilters select").First;
        Assert.False(await status.IsDisabledAsync());
        await status.SelectOptionAsync(new SelectOptionValue { Value = "DELIVERED" });
        Assert.Equal("DELIVERED", await status.InputValueAsync());
        await countdown.GetByText(
            "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.",
            new() { Exact = true }).WaitForAsync();
        Assert.Single(dashboard.Queries);

        // At 0 the dashboard starts once, by itself, reading the status on screen; the message goes.
        await WaitForAsync(() => dashboard.Queries.Count >= 2, "the dashboard never read again after the wait");
        Assert.Equal("status=DELIVERED", dashboard.Queries[1]);
        await page.Locator("main").GetByRole(AriaRole.Timer).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Equal("DELIVERED", await status.InputValueAsync());
        await Task.Delay(2_500);
        Assert.Equal(2, dashboard.Queries.Count);
        Assert.Empty(console);
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail(failure);
    }

    private static Task FulfillAsync(IRoute route, object body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(body),
        });

    private static Task ProblemAsync(IRoute route, int status) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/problem+json",
            Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status }),
        });

    /// <summary>The operations dashboard read model (ADR-OBS-001): 429 first, then an empty page; every query is kept.</summary>
    private sealed class DashboardApi
    {
        private readonly object _gate = new();
        private readonly List<string> _queries = [];

        internal int TooManyRequests { get; set; }

        internal IReadOnlyList<string> Queries
        {
            get
            {
                lock (_gate) return [.. _queries];
            }
        }

        internal async Task HandleAsync(IRoute route)
        {
            var uri = new Uri(route.Request.Url);
            if (uri.AbsolutePath != "/api/v1/operations/dashboard")
            {
                await ProblemAsync(route, 404);
                return;
            }

            bool limited;
            lock (_gate)
            {
                _queries.Add(uri.Query.TrimStart('?'));
                limited = TooManyRequests > 0;
                if (limited) TooManyRequests -= 1;
            }

            if (limited)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 429,
                    ContentType = "application/problem+json",
                    // The test API is another origin, so it exposes Retry-After as a cross-origin API would have to.
                    Headers = new Dictionary<string, string>
                    {
                        ["Retry-After"] = "2",
                        ["Cache-Control"] = "no-store",
                        ["Access-Control-Expose-Headers"] = "Retry-After",
                    },
                    Body = JsonSerializer.Serialize(new
                    {
                        type = "about:blank",
                        title = "Too Many Requests",
                        status = 429,
                        traceId = new string('0', 32),
                    }),
                });
                return;
            }

            await FulfillAsync(route, new { generated_at = "2026-10-13T12:00:00Z", items = Array.Empty<object>(), next_cursor = (string?)null });
        }
    }
}
