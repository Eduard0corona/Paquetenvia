using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// Z15 ("Ambos, panel fijo (Recommended)") and AI-08 UI-001 "tenant switch clears prior state" in a real browser
/// against the production Next build, while the previous organization's OperationsHub is slow to close: its SignalR
/// negotiation is still pending when the organization changes, so closing it waits for that answer. The inbox
/// (<c>/ops/inbox</c>, AI-07 work_inbox) and the operations dashboard (<c>/ops/dashboard</c>) must drop the previous
/// organization's orders, counts, selection and "Asignar" panel as soon as the change arrives, and send nothing of it
/// under the new organization (the embedded picker no longer restarts by itself); the new organization is read once
/// the previous connection closed. Z17 ("PR aparte después de #339 (Recommended)") does the same for the order detail
/// (<c>/ops/orders/:id</c>), whose panels no longer restart by themselves for the URL's order, and for Rutas
/// (<c>/ops/routes</c>). The API is fulfilled in the browser: the dashboard read model (OBS-001, ADR-OBS-001), the AI-05
/// shapes of getOperationsQueueCounts, listAssignableDrivers, getOrder, listRoutes, getRoute and
/// /me/organization-contexts.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed class OrganizationSwitchSlowHubPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationC = "33333333-3333-3333-3333-333333333333";
    private const string OrganizationBName = "Mensajería Sintética Norte";
    private const string OrganizationCName = "Paquetería Sintética Sur";

    private static readonly SwitchOrder ReadyOfB = new("a1000000-0000-4000-8000-000000000001", "ORD_BandejaSintetica000001", "READY_FOR_PICKUP", "2026-11-02T06:20:00Z");
    private static readonly SwitchOrder RescheduledOfB = new("a1000000-0000-4000-8000-000000000002", "ORD_BandejaSintetica000002", "RESCHEDULED", "2026-11-02T06:10:00Z");
    private static readonly SwitchOrder FailedOfB = new("a1000000-0000-4000-8000-000000000003", "ORD_BandejaSintetica000003", "FAILED_ATTEMPT", "2026-11-02T06:00:00Z");
    private static readonly SwitchOrder ReadyOfC = new("c1000000-0000-4000-8000-000000000001", "ORD_BandejaSinteticaSur01", "READY_FOR_PICKUP", "2026-11-02T06:05:00Z");

    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/operations/dashboard$"),
        new("^GET /api/v1/operations/queue-counts$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/assignable-drivers$"),
        new("^POST /hubs/operations/negotiate$"),
    ];

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Inbox_drops_the_previous_organization_and_its_Asignar_panel_before_the_slow_hub_closes()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationB, OrganizationBName, "DISPATCHER"), (OrganizationC, OrganizationCName, "PLATFORM_ADMIN"));
        api.Seed(OrganizationB, ReadyOfB, RescheduledOfB);
        api.Seed(OrganizationC, ReadyOfC);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectGuidesAsync(page, "table.opsInboxTable a.opsInboxGuide", ReadyOfB.PublicId, RescheduledOfB.PublicId);
        await WaitForAsync(() => api.Negotiations == 1, "B's hub never negotiated");
        // A selection and an open "Asignar" panel of B, its drivers listed.
        await page.GetByRole(AriaRole.Checkbox, new() { Name = $"Seleccionar la orden {ReadyOfB.PublicId}", Exact = true }).CheckAsync();
        await page.Locator($"#inbox-assign-{RescheduledOfB.Id}").ClickAsync();
        await page.Locator("main section.opsInboxAssign ul.opsDriverChoices > li").First.WaitForAsync();

        // The change arrives while B's hub still negotiates: B's orders, counts, selection and panel go at once.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationCName });
        await WaitForAsync(async () => !(await page.Locator("main").InnerTextAsync()).Contains(ReadyOfB.PublicId, StringComparison.Ordinal),
            "B's orders were still shown under C while B's hub closed", seconds: 1);
        var during = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(RescheduledOfB.PublicId, during, StringComparison.Ordinal);
        Assert.DoesNotContain("seleccionadas", during, StringComparison.Ordinal);
        Assert.DoesNotContain("2 órdenes", during, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("main section.opsInboxAssign").CountAsync());
        await Task.Delay(1_000);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationC);
        Assert.DoesNotContain(calls.Api, call => call.Path.Contains(RescheduledOfB.Id, StringComparison.Ordinal) && call.Organization == OrganizationC);

        // Once B's hub closed, C is read: its own order only, and nothing of B under C.
        api.ReleaseNegotiation();
        await ExpectGuidesAsync(page, "table.opsInboxTable a.opsInboxGuide", ReadyOfC.PublicId);
        await page.Locator("main .opsInboxFooter p.opsSummaryNote").First
            .GetByText("Mostrando 1 de 1 sin asignar · seleccionadas: 0", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.Locator("main section.opsInboxAssign").CountAsync());
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationC && call.Path.Contains("/orders/", StringComparison.Ordinal));
        Assert.DoesNotContain(calls.Api, call => call.Organization == OrganizationC && call.Path.Contains(OrganizationBOrderPrefix, StringComparison.Ordinal));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Dashboard_drops_the_previous_organization_before_the_slow_hub_closes()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationB, OrganizationBName, "DISPATCHER"), (OrganizationC, OrganizationCName, "DISPATCHER"));
        api.Seed(OrganizationB, ReadyOfB, RescheduledOfB);
        api.Seed(OrganizationC, ReadyOfC);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/dashboard").AbsoluteUri);
        await WaitForAsync(async () => (await page.Locator("main").InnerTextAsync()).Contains(RescheduledOfB.PublicId, StringComparison.Ordinal),
            "B's orders were not shown");
        await WaitForAsync(() => api.Negotiations == 1, "B's hub never negotiated");
        var readsOfB = api.AllCalls.Count(call => call.Organization == OrganizationB);

        // The change arrives while B's hub still negotiates: B's orders and counts go at once and nothing is read.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationCName });
        await WaitForAsync(async () => !(await page.Locator("main").InnerTextAsync()).Contains(ReadyOfB.PublicId, StringComparison.Ordinal),
            "B's orders were still shown under C while B's hub closed", seconds: 1);
        Assert.DoesNotContain(RescheduledOfB.PublicId, await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
        await Task.Delay(1_000);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationC);
        Assert.Equal(readsOfB, api.AllCalls.Count(call => call.Organization == OrganizationB));

        // Once B's hub closed, C is read and shows its own order only.
        api.ReleaseNegotiation();
        await WaitForAsync(async () => (await page.Locator("main").InnerTextAsync()).Contains(ReadyOfC.PublicId, StringComparison.Ordinal),
            "C's orders were not shown");
        var after = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(ReadyOfB.PublicId, after, StringComparison.Ordinal);
        Assert.DoesNotContain(RescheduledOfB.PublicId, after, StringComparison.Ordinal);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// Z17: the order detail of an order of B, with the panel that reads that order by itself: "Asignar repartidor" with
    /// B's drivers (READY_FOR_PICKUP), or "Intento fallido" with its reason read from listIncidents (FAILED_ATTEMPT). The
    /// change to C arrives while B's hub still negotiates: the order and every panel go at once, and nothing is read
    /// under C, least of all for B's order. Once B's hub closed, the screen reads the URL's order under C, which RLS does
    /// not show; none of its panels reads it.
    /// </summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("READY_FOR_PICKUP")]
    [InlineData("FAILED_ATTEMPT")]
    public async Task Order_detail_drops_the_previous_organization_and_its_panels_before_the_slow_hub_closes(string status)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationB, OrganizationBName, "DISPATCHER"), (OrganizationC, OrganizationCName, "DISPATCHER"));
        api.Seed(OrganizationB, ReadyOfB, RescheduledOfB, FailedOfB);
        api.Seed(OrganizationC, ReadyOfC);
        var order = status == "FAILED_ATTEMPT" ? FailedOfB : ReadyOfB;

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        await WaitForAsync(async () => (await page.Locator("main").InnerTextAsync()).Contains(order.PublicId, StringComparison.Ordinal),
            "B's order was not shown");
        if (order == ReadyOfB)
        {
            await page.Locator("main section.opsDriverAssignment ul.opsDriverChoices > li").First.WaitForAsync();
        }
        else
        {
            // listIncidents answers 404 here, so the reason stays hidden; the read went under B.
            await WaitForAsync(
                () => calls.Api.Any(call => call.Organization == OrganizationB && call.PathAndQuery == $"/api/v1/incidents?order_id={order.Id}"),
                "the failed attempt was never read");
        }

        await WaitForAsync(() => api.Negotiations == 1, "B's hub never negotiated");

        // The change arrives while B's hub still negotiates: the order and its panels go at once.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationCName });
        await WaitForAsync(async () => !(await page.Locator("main").InnerTextAsync()).Contains(order.PublicId, StringComparison.Ordinal),
            "B's order was still shown under C while B's hub closed", seconds: 1);
        Assert.Equal(0, await page.Locator("main section.opsDriverAssignment").CountAsync());
        Assert.DoesNotContain("DRV-1a2b3c4d", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
        await Task.Delay(1_000);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationC);
        Assert.DoesNotContain(calls.Api, call => call.Organization == OrganizationC && call.PathAndQuery.Contains(order.Id, StringComparison.Ordinal));

        // Once B's hub closed, the screen reads the URL's order under C, and RLS does not show it to C.
        api.ReleaseNegotiation();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Orden no disponible", Exact = true }).WaitForAsync();
        await Task.Delay(500);
        // Only the screen's own reads of the URL's order went under C; none of its panels read it.
        Assert.Equal(
            new[] { $"GET /api/v1/operations/dashboard?order_id={order.Id}", $"GET /api/v1/orders/{order.Id}" },
            calls.Api
                .Where(call => call.Organization == OrganizationC && call.PathAndQuery.Contains(order.Id, StringComparison.Ordinal))
                .Select(call => $"{call.Method} {call.PathAndQuery}")
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain(order.PublicId, await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress, OrderDetailCalls);
        trace.Passed();
    }

    /// <summary>
    /// Z17: Rutas shows B's route of today with its stop named by its guía. The change to C arrives while B's hub still
    /// negotiates: the route list and the route shown go at once and no route or order is read under C; once B's hub
    /// closed, C's own route is listed and nothing of B is read under C.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Routes_drop_the_previous_organization_before_the_slow_hub_closes()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationB, OrganizationBName, "DISPATCHER"), (OrganizationC, OrganizationCName, "DISPATCHER"));
        api.Seed(OrganizationB, ReadyOfB, RescheduledOfB);
        api.Seed(OrganizationC, ReadyOfC);
        api.SeedRoute(OrganizationB, new SwitchRoute(RouteOfB, "2026-11-02", ReadyOfB.Id));
        api.SeedRoute(OrganizationC, new SwitchRoute(RouteOfC, "2026-11-03"));

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/routes?routeId={RouteOfB}").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta del 2 de noviembre de 2026", Exact = true }).WaitForAsync();
        await page.Locator(".opsRouteStops li strong").GetByText(ReadyOfB.PublicId, new() { Exact = true }).WaitForAsync();
        await WaitForAsync(() => api.Negotiations == 1, "B's hub never negotiated");

        // The change arrives while B's hub still negotiates: B's routes, the route shown and its guía go at once.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationCName });
        await WaitForAsync(async () => !(await page.Locator("main").InnerTextAsync()).Contains(ReadyOfB.PublicId, StringComparison.Ordinal),
            "B's route was still shown under C while B's hub closed", seconds: 1);
        var during = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("2 de noviembre", during, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator(".opsRouteList button").CountAsync());
        await Task.Delay(1_000);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationC);

        // Once B's hub closed, C's own route is listed, and nothing of B was read under C.
        api.ReleaseNegotiation();
        await page.Locator(".opsRouteList button strong").GetByText("Ruta del 3 de noviembre de 2026", new() { Exact = false }).WaitForAsync();
        var after = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("2 de noviembre", after, StringComparison.Ordinal);
        Assert.DoesNotContain(ReadyOfB.PublicId, after, StringComparison.Ordinal);
        Assert.DoesNotContain(calls.Api, call => call.Organization == OrganizationC &&
            (call.PathAndQuery.Contains(RouteOfB, StringComparison.Ordinal) || call.PathAndQuery.Contains(OrganizationBOrderPrefix, StringComparison.Ordinal)));
        Assert.Equal(1, api.AllCalls.Count(call => call.Organization == OrganizationC && call.Path == "/api/v1/routes"));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress, RoutesCalls);
        trace.Passed();
    }

    private const string RouteOfB = "7b000000-0000-4000-8000-000000000001";
    private const string RouteOfC = "7c000000-0000-4000-8000-000000000001";

    /// <summary>The order detail's own reads and those of its panels that answer 404 here (recipient notices, incidents).</summary>
    private static readonly Regex[] OrderDetailCalls =
    [
        new("^GET /api/v1/orders/[0-9a-f-]{36}$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/recipient-notices$"),
        new("^GET /api/v1/incidents$"),
    ];

    /// <summary>Rutas: the routes, a route, each stop's guía (getOrder) and the lists of "Crear ruta".</summary>
    private static readonly Regex[] RoutesCalls =
    [
        new("^GET /api/v1/routes$"),
        new("^GET /api/v1/routes/[0-9a-f-]{36}$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}$"),
        new("^GET /api/v1/drivers$"),
        new("^GET /api/v1/cities$"),
        new("^GET /api/v1/service-areas$"),
    ];

    /// <summary>Every synthetic order id of organization B starts with it.</summary>
    private const string OrganizationBOrderPrefix = "a1000000-";

    private static async Task ExpectGuidesAsync(IPage page, string selector, params string[] guides)
    {
        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = [.. (await page.Locator(selector).AllInnerTextsAsync()).Select(text => text.Trim())];
            if (actual.SequenceEqual(guides, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The listed orders did not match: {string.Join(" | ", actual)}");
    }

    private static async Task AssertNothingStoredAsync(IPage page)
    {
        var findings = await page.EvaluateAsync<string[]>(
            """
            async () => {
              const findings = [];
              if (localStorage.length > 0) findings.push(`localStorage:${localStorage.length}`);
              if (sessionStorage.length > 0) findings.push(`sessionStorage:${sessionStorage.length}`);
              if (document.cookie !== "") findings.push("cookie");
              for (const database of await indexedDB.databases()) findings.push(`indexedDB:${database.name}`);
              for (const name of await caches.keys()) {
                for (const request of await (await caches.open(name)).keys()) {
                  const path = new URL(request.url).pathname;
                  if (!path.startsWith("/_next/static/") && path !== "/manifest.webmanifest" && !path.startsWith("/icons/")) {
                    findings.push(`cache:${path}`);
                  }
                }
              }
              return findings;
            }
            """);
        Assert.Empty(findings);
    }

    private static void AssertOnlyAllowedCalls(CallRecorder calls, Uri web, params Regex[] alsoAllowed)
    {
        Assert.All(calls.Api, call => Assert.Contains(AllowedCalls.Concat(alsoAllowed), pattern => pattern.IsMatch($"{call.Method} {call.Path}")));
        var webOrigin = web.GetLeftPart(UriPartial.Authority) + "/";
        Assert.DoesNotContain(calls.Foreign, url => !url.StartsWith(webOrigin, StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure, int seconds = 20) =>
        await WaitForAsync(() => Task.FromResult(condition()), failure, seconds);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string failure, int seconds = 20)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail(failure);
    }

    private static ConcurrentQueue<string> CaptureConsole(IPage page)
    {
        var messages = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            if (!message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
            {
                messages.Enqueue($"{message.Type}:{message.Text}");
            }
        };
        page.PageError += (_, error) => messages.Enqueue($"page:{error}");
        return messages;
    }

    private static CallRecorder RecordCalls(IPage page)
    {
        var calls = new CallRecorder();
        page.Request += (_, request) =>
        {
            if (!request.Url.StartsWith("http", StringComparison.Ordinal)) return;
            if (request.Url.StartsWith(DriverStopsNextServerFixture.TestApiOrigin + "/", StringComparison.Ordinal))
            {
                var uri = new Uri(request.Url);
                calls.Api.Enqueue((
                    request.Method,
                    uri.AbsolutePath,
                    request.Headers.TryGetValue("x-organization-id", out var organization) ? organization : string.Empty,
                    uri.PathAndQuery));
                return;
            }

            calls.Foreign.Enqueue(request.Url);
        };
        return calls;
    }

    /// <summary>
    /// The in-memory operations session with the shell's organization switch (Mock mode, as the other panel suites).
    /// The first hub negotiation (organization B's) is held until <see cref="SwitchApi.ReleaseNegotiation"/>, then every
    /// negotiation is refused, so the screens keep working by REST.
    /// </summary>
    private static async Task<SwitchApi> InstallAsync(IPage page, params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-organization-switch-slow-hub-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        var api = new SwitchApi(organizations);
        await page.RouteAsync("**/api/v1/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 404,
            ContentType = "application/problem+json",
            Body = """{"type":"about:blank","title":"Problem.","status":404}""",
        }));
        await page.RouteAsync("**/hubs/**", api.HandleHubAsync);
        await page.RouteAsync("**/api/v1/me/organization-contexts**", api.HandleRoleReadAsync);
        await page.RouteAsync("**/api/v1/operations/**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/orders/*/assignable-drivers**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/orders/*", api.HandleAsync);
        await page.RouteAsync("**/api/v1/routes**", api.HandleAsync);
        return api;
    }

    private static Task FulfillAsync(IRoute route, object body) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Body = JsonSerializer.Serialize(body) });

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path, string Organization, string PathAndQuery)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    private sealed record SwitchOrder(string Id, string PublicId, string Status, string UpdatedAt)
    {
        internal object Json(string organization, string organizationName) => new
        {
            order_id = Id,
            aggregate_version = 3,
            public_id = PublicId,
            owner = new { organization_id = organization, display_name = organizationName },
            @operator = (object?)null,
            client = (object?)null,
            status = Status,
            created_at = "2026-11-01T18:00:00Z",
            updated_at = UpdatedAt,
            service_type = "SAME_DAY",
            pickup_window = (object?)null,
            delivery_window = (object?)null,
            delivery_zone = (object?)null,
            assignment = (object?)null,
            latest_driver_location = (object?)null,
            total = new { currency = "MXN", amount_cents = 25_000L },
            cost_warning = (string?)null,
            unassigned_alert = true,
        };

        /// <summary>AI-05 Order as getOrder returns it.</summary>
        internal object Detail(string organization) => new
        {
            id = Id,
            public_id = PublicId,
            owner_org_id = organization,
            operator_org_id = (string?)null,
            status = Status,
            price_net = new { currency = "MXN", amount_cents = 25_000L },
            version = 3,
            origin_location_id = "61000000-0000-4000-8000-000000000001",
            destination_location_id = "62000000-0000-4000-8000-000000000001",
            service_type = "SAME_DAY",
            quote_id = "63000000-0000-4000-8000-000000000001",
            city_id = "64000000-0000-4000-8000-000000000001",
            service_area_id = (string?)null,
            pricing_tier = "OCCASIONAL",
            total = new { currency = "MXN", amount_cents = 25_000L },
            claim_window_ends_at = (string?)null,
            finalized_at = (string?)null,
            service_window = (object?)null,
            timeline = Array.Empty<object>(),
            allowed_transitions = Array.Empty<object>(),
        };
    }

    /// <summary>A DRAFT route of the organization, its stops by order id.</summary>
    private sealed record SwitchRoute(string Id, string ScheduledFor, params string[] OrderIds)
    {
        internal object Summary() => new
        {
            id = Id,
            status = "DRAFT",
            version = 2,
            driver_id = "d1000000-0000-4000-8000-000000000001",
            city_id = "64000000-0000-4000-8000-000000000001",
            service_area_id = (string?)null,
            scheduled_for = ScheduledFor,
            assignment_cost_cents_total = OrderIds.Length * 1_000L,
            stop_count = OrderIds.Length,
        };

        internal object Detail() => new
        {
            id = Id,
            status = "DRAFT",
            version = 2,
            driver_id = "d1000000-0000-4000-8000-000000000001",
            city_id = "64000000-0000-4000-8000-000000000001",
            service_area_id = (string?)null,
            scheduled_for = ScheduledFor,
            assignment_cost_cents_total = OrderIds.Length * 1_000L,
            stop_count = OrderIds.Length,
            stops = OrderIds.Select((orderId, index) => new
            {
                id = $"e{index + 1:x7}-0000-4000-8000-{orderId[^12..]}",
                order_id = orderId,
                sequence = index + 1,
                stop_type = "DELIVERY",
                status = "PENDING",
            }).ToArray(),
        };
    }

    private sealed record SwitchCall(string Organization, string Method, string Path, string Query);

    /// <summary>The dashboard read model and its counts, the driver list and a slow OperationsHub negotiation.</summary>
    private sealed class SwitchApi((string Id, string Name, string Role)[] organizations)
    {
        private static readonly string[] OrderStatuses =
        [
            "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING",
            "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED", "DELIVERED", "CLOSED", "CLAIM_OPEN",
            "CLAIM_RESOLVED", "CANCELLED",
        ];

        private readonly object _gate = new();
        private readonly List<SwitchCall> _calls = [];
        private readonly Dictionary<string, SwitchOrder[]> _orders = [];
        private readonly Dictionary<string, SwitchRoute[]> _routes = [];
        private readonly TaskCompletionSource _negotiation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _negotiations;

        internal int Negotiations => Volatile.Read(ref _negotiations);

        internal IReadOnlyList<SwitchCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal void Seed(string organization, params SwitchOrder[] orders)
        {
            lock (_gate) _orders[organization] = orders;
        }

        internal void SeedRoute(string organization, params SwitchRoute[] routes)
        {
            lock (_gate) _routes[organization] = routes;
        }

        /// <summary>Organization B's hub negotiation answers (refused) now; the connection closes.</summary>
        internal void ReleaseNegotiation() => _negotiation.TrySetResult();

        internal async Task HandleHubAsync(IRoute route)
        {
            if (Interlocked.Increment(ref _negotiations) == 1) await _negotiation.Task;
            await route.AbortAsync();
        }

        internal Task HandleRoleReadAsync(IRoute route) =>
            FulfillAsync(route, organizations.Select((organization, index) => new
            {
                organization_id = organization.Id,
                display_name = organization.Name,
                role = organization.Role,
                is_default = index == 0,
            }).ToArray());

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var path = uri.AbsolutePath;
            var query = uri.Query.TrimStart('?');
            var organization = (await request.AllHeadersAsync()).GetValueOrDefault("x-organization-id") ?? string.Empty;
            lock (_gate) _calls.Add(new SwitchCall(organization, request.Method, path, query));
            var name = organizations.First(entry => entry.Id == organization).Name;
            SwitchOrder[] orders;
            SwitchRoute[] routes;
            lock (_gate)
            {
                orders = _orders.GetValueOrDefault(organization, []);
                routes = _routes.GetValueOrDefault(organization, []);
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path == "/api/v1/operations/dashboard")
            {
                // The order detail reads its own row with order_id; RLS shows only the organization's orders.
                var orderId = System.Web.HttpUtility.ParseQueryString(query)["order_id"];
                await FulfillAsync(route, new
                {
                    generated_at = "2026-11-02T06:30:00Z",
                    items = orders.Where(order => orderId is null || order.Id == orderId).Select(order => order.Json(organization, name)).ToArray(),
                    next_cursor = (string?)null,
                });
            }
            else if (segments is ["api", "v1", "orders", var orderId] && request.Method == "GET")
            {
                var order = orders.FirstOrDefault(item => item.Id == orderId);
                if (order is null) await NotFoundAsync(route);
                else await FulfillAsync(route, order.Detail(organization));
            }
            else if (path == "/api/v1/routes" && request.Method == "GET")
            {
                await FulfillAsync(route, new { items = routes.Select(item => item.Summary()).ToArray(), next_cursor = (string?)null });
            }
            else if (segments is ["api", "v1", "routes", var routeId] && request.Method == "GET")
            {
                var found = routes.FirstOrDefault(item => item.Id == routeId);
                if (found is null) await NotFoundAsync(route);
                else await FulfillAsync(route, found.Detail());
            }
            else if (path == "/api/v1/operations/queue-counts")
            {
                var byStatus = OrderStatuses.ToDictionary(status => status, status => orders.Count(order => order.Status == status));
                await FulfillAsync(route, new
                {
                    generated_at = "2026-11-02T06:30:00Z",
                    total = orders.Length,
                    by_status = byStatus,
                    queues = new
                    {
                        unassigned = orders.Length,
                        needs_attention = byStatus["RESCHEDULED"],
                        price_review = 0,
                        delivered_not_closed = 0,
                        en_route = 0,
                    },
                });
            }
            else if (path.EndsWith("/assignable-drivers", StringComparison.Ordinal))
            {
                // An order of another organization is the uniform 404, as RLS answers it.
                if (!orders.Any(order => path.Contains(order.Id, StringComparison.Ordinal)))
                {
                    await NotFoundAsync(route);
                    return;
                }

                await FulfillAsync(route, new
                {
                    items = new[]
                    {
                        new
                        {
                            driver_id = "d1000000-0000-4000-8000-000000000001",
                            driver_reference = "DRV-1a2b3c4d",
                            vehicle_type = "MOTORCYCLE",
                            eligible = true,
                            ineligibility_reasons = Array.Empty<string>(),
                            active_assignment_count = 0,
                        },
                    },
                    next_cursor = (string?)null,
                });
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
            }
        }

        private static Task NotFoundAsync(IRoute route) =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 404,
                ContentType = "application/problem+json",
                Body = """{"type":"about:blank","title":"Problem.","status":404}""",
            });
    }
}
