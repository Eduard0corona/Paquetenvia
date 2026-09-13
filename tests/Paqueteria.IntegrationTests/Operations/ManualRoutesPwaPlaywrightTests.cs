using System.Text.Json;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

[Collection(DriverStopsPwaCollection.Name)]
public sealed class ManualRoutesPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string RouteA = "a1000000-0000-0000-0000-000000000001";
    private const string RouteB = "b2000000-0000-0000-0000-000000000001";
    private const string DriverId = "55555555-5555-5555-5555-555555555551";
    private const string CityId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Planner_uses_authoritative_routes_for_mutations_conflicts_and_tenant_switches()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(
            new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await InstallSessionAsync(page);
        await page.RouteAsync("**/hubs/operations/negotiate**", route => route.AbortAsync());

        var state = new RouteApiState();
        await page.RouteAsync("**/api/v1/routes**", async route =>
        {
            var organization = route.Request.Headers.TryGetValue("x-organization-id", out var value)
                ? value
                : string.Empty;
            if (organization == OrganizationB)
            {
                var path = new Uri(route.Request.Url).AbsolutePath;
                await FulfillAsync(route, path == $"/api/v1/routes/{RouteB}"
                    ? Detail(RouteB, 1, [])
                    : Page(Summary(RouteB, 1, 0, 0)));
                return;
            }
            Assert.Equal(OrganizationA, organization);
            await state.HandleAsync(route);
        });

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/ops/routes").AbsoluteUri);
        Assert.NotNull(response);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Rutas manuales", Exact = true }).WaitForAsync();

        await page.GetByLabel("Driver OWN (UUID)").FillAsync(DriverId);
        await page.GetByLabel("Ciudad (UUID)").FillAsync(CityId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Crear DRAFT", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta a1000000", Exact = true }).WaitForAsync();

        foreach (var orderId in RouteApiState.OrderIds)
        {
            await page.GetByLabel("Orden con assignment OWN (UUID)").FillAsync(orderId);
            await page.GetByRole(AriaRole.Button, new() { Name = "Agregar DELIVERY", Exact = true }).ClickAsync();
        }
        await ExpectStopCountAsync(page, 3);

        var stops = page.Locator(".opsRouteStops > li");
        await stops.Nth(0).DispatchEventAsync("dragstart");
        await stops.Nth(2).DispatchEventAsync("dragover");
        await stops.Nth(2).DispatchEventAsync("drop");
        await ExpectSequenceAsync(page, RouteApiState.OrderIds[1], RouteApiState.OrderIds[2], RouteApiState.OrderIds[0]);

        state.FailNextReorderAsStale = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Mover orden 83000000 arriba", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Status).GetByText(
            "La ruta cambio en el servidor. Se recupero la version autoritativa.",
            new() { Exact = true }).WaitForAsync();
        Assert.True(state.DetailReadsAfterConflict > 0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Mover orden 81000000 arriba", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Retirar", Exact = true }).First.ClickAsync();
        await ExpectStopCountAsync(page, 2);
        Assert.True(state.LastMutationVersion == state.Version);

        await page.EvaluateAsync(
            """
            organizationId => {
              window.__paquetenviaOperationsSession = {
                ...window.__paquetenviaOperationsSession,
                organizationId,
              };
              window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
            }
            """,
            OrganizationB);
        await page.Locator(".opsRouteList button").First.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta b2000000", Exact = true }).WaitForAsync();
        Assert.False(await page.GetByText("a1000000", new() { Exact = false }).IsVisibleAsync());
        Assert.DoesNotContain(state.RequestOrganizations, organization =>
            organization != OrganizationA && organization != OrganizationB);
    }

    private static async Task InstallSessionAsync(IPage page) =>
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(OrganizationA)}},
              sessionNamespace: "rte-001-playwright",
              getAccessToken: () => "synthetic-operations-token",
            };
            """);

    private static async Task ExpectStopCountAsync(IPage page, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator(".opsRouteStops > li").CountAsync() == expected) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Expected {expected} authoritative route stops.");
    }

    private static async Task ExpectSequenceAsync(IPage page, params string[] orderIds)
    {
        var expected = orderIds.Select(id => id[..8]).ToArray();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var actual = await page.Locator(".opsRouteStops li strong").AllTextContentsAsync();
            if (actual.Count == expected.Length && expected.Select((id, index) =>
                    actual[index].Contains(id, StringComparison.Ordinal)).All(value => value)) return;
            await Task.Delay(50);
        }
        Assert.Fail("The route stop sequence did not match the authoritative response.");
    }

    private static object Summary(string routeId, int version, long cost, int stopCount) => new
    {
        id = routeId,
        status = "DRAFT",
        version,
        driver_id = DriverId,
        city_id = CityId,
        service_area_id = (string?)null,
        scheduled_for = "2026-08-30",
        assignment_cost_cents_total = cost,
        stop_count = stopCount,
    };

    private static object Page(object route) => new { items = new[] { route }, next_cursor = (string?)null };

    private static object Detail(string routeId, int version, object[] stops) => new
    {
        id = routeId,
        status = "DRAFT",
        version,
        driver_id = DriverId,
        city_id = CityId,
        service_area_id = (string?)null,
        scheduled_for = "2026-08-30",
        assignment_cost_cents_total = 0L,
        stop_count = stops.Length,
        stops,
    };

    private static Task FulfillAsync(IRoute route, object body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(body),
        });

    private sealed class RouteApiState
    {
        internal static readonly string[] OrderIds =
        [
            "81000000-0000-0000-0000-000000000001",
            "82000000-0000-0000-0000-000000000001",
            "83000000-0000-0000-0000-000000000001",
        ];

        private readonly List<(string StopId, string OrderId)> stops = [];
        private bool exists;
        private int reorderCalls;
        internal bool FailNextReorderAsStale { get; set; }
        internal int Version { get; private set; }
        internal int LastMutationVersion { get; private set; }
        internal int DetailReadsAfterConflict { get; private set; }
        internal List<string> RequestOrganizations { get; } = [];

        internal async Task HandleAsync(IRoute route)
        {
            RequestOrganizations.Add(route.Request.Headers["x-organization-id"]);
            var uri = new Uri(route.Request.Url);
            var path = uri.AbsolutePath;
            var method = route.Request.Method;
            if (method == "GET" && path == "/api/v1/routes")
            {
                await FulfillAsync(route, exists ? Page(Summary(RouteA, Version, stops.Count * 1_000L, stops.Count)) :
                    new { items = Array.Empty<object>(), next_cursor = (string?)null });
                return;
            }
            if (method == "POST" && path == "/api/v1/routes")
            {
                exists = true;
                Version = LastMutationVersion = 1;
                await FulfillAsync(route, Summary(RouteA, Version, 0, 0), 201);
                return;
            }
            if (method == "GET" && path == $"/api/v1/routes/{RouteA}")
            {
                if (reorderCalls > 0 && FailNextReorderAsStale is false) DetailReadsAfterConflict++;
                await FulfillAsync(route, Detail());
                return;
            }
            if (method == "POST" && path == $"/api/v1/routes/{RouteA}/stops")
            {
                var body = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
                var orderId = body.GetProperty("order_id").GetString()!;
                stops.Add(($"9{stops.Count + 1}000000-0000-0000-0000-000000000001", orderId));
                Version = LastMutationVersion = checked(Version + 1);
                await FulfillAsync(route, Detail(), 201);
                return;
            }
            if (method == "PUT" && path == $"/api/v1/routes/{RouteA}/stops/order")
            {
                reorderCalls++;
                if (FailNextReorderAsStale)
                {
                    FailNextReorderAsStale = false;
                    Version = checked(Version + 1);
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 409, Body = "{}", ContentType = "application/problem+json" });
                    return;
                }
                var body = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
                var orderedIds = body.GetProperty("stop_ids").EnumerateArray().Select(value => value.GetString()).ToArray();
                stops.Sort((left, right) => Array.IndexOf(orderedIds, left.StopId).CompareTo(Array.IndexOf(orderedIds, right.StopId)));
                Version = LastMutationVersion = checked(Version + 1);
                await FulfillAsync(route, Detail());
                return;
            }
            if (method == "DELETE" && path.StartsWith($"/api/v1/routes/{RouteA}/stops/", StringComparison.Ordinal))
            {
                var stopId = path[(path.LastIndexOf('/') + 1)..];
                stops.RemoveAll(stop => stop.StopId == stopId);
                Version = LastMutationVersion = checked(Version + 1);
                await FulfillAsync(route, Detail());
                return;
            }
            await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Body = "{}", ContentType = "application/problem+json" });
        }

        private object Detail() => new
        {
            id = RouteA,
            status = "DRAFT",
            version = Version,
            driver_id = DriverId,
            city_id = CityId,
            service_area_id = (string?)null,
            scheduled_for = "2026-08-30",
            assignment_cost_cents_total = stops.Count * 1_000L,
            stop_count = stops.Count,
            stops = stops.Select((stop, index) => new
            {
                id = stop.StopId,
                order_id = stop.OrderId,
                sequence = index + 1,
                stop_type = "DELIVERY",
                status = "PENDING",
            }).ToArray(),
        };
    }
}
