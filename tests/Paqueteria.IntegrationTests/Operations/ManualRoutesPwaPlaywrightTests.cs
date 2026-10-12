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
    private const string SuspendedDriverId = "55555555-5555-5555-5555-555555555552";
    private const string CityId = "33333333-3333-3333-3333-333333333333";
    private const string ServiceAreaId = "34343434-3434-3434-3434-343434343434";
    private const string RetiredServiceAreaId = "35353535-3535-3535-3535-353535353535";

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
        // UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11: "Crear ruta" picks the driver, the city and the area from lists.
        var selectorRequests = new List<(string Organization, string PathAndQuery)>();
        await page.RouteAsync("**/api/v1/drivers**", async route =>
        {
            selectorRequests.Add(SelectorRequest(route));
            await FulfillAsync(route, new
            {
                items = new object[]
                {
                    Driver(DriverId, "DRV-55555555", "ACTIVE"),
                    Driver(SuspendedDriverId, "DRV-55555552", "SUSPENDED"),
                },
                next_cursor = (string?)null,
            });
        });
        await page.RouteAsync("**/api/v1/cities**", async route =>
        {
            selectorRequests.Add(SelectorRequest(route));
            await FulfillAsync(route, new[]
            {
                new { id = CityId, name = "Culiacán", state_code = "SIN", country_code = "MX", timezone = "America/Mazatlan" },
            });
        });
        await page.RouteAsync("**/api/v1/service-areas**", async route =>
        {
            selectorRequests.Add(SelectorRequest(route));
            await FulfillAsync(route, new[]
            {
                new { id = ServiceAreaId, city_id = CityId, name = "Centro", status = "ACTIVE" },
                new { id = RetiredServiceAreaId, city_id = CityId, name = "Antigua", status = "INACTIVE" },
            });
        });

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/ops/routes").AbsoluteUri);
        Assert.NotNull(response);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Rutas manuales", Exact = true }).WaitForAsync();

        Assert.Equal(0, await page.GetByText("ID del repartidor", new() { Exact = false }).CountAsync());
        Assert.Equal(0, await page.GetByText("ID de la ciudad", new() { Exact = false }).CountAsync());
        var create = page.GetByRole(AriaRole.Button, new() { Name = "Crear ruta (borrador)", Exact = true });
        var driverSelect = page.GetByLabel("Repartidor (flota propia)");
        await driverSelect.Locator("option", new() { HasText = "DRV-55555555" })
            .WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.True(await create.IsDisabledAsync());
        // Only ACTIVE drivers are offered, by reference, vehicle and status; no internal id is shown.
        Assert.Equal(
            new[] { "Elige un repartidor", "DRV-55555555 · Motocicleta · Activo" },
            await driverSelect.Locator("option").AllTextContentsAsync());
        await driverSelect.SelectOptionAsync(new SelectOptionValue { Label = "DRV-55555555 · Motocicleta · Activo" });
        Assert.True(await create.IsDisabledAsync());
        await page.GetByLabel("Ciudad", new() { Exact = true })
            .SelectOptionAsync(new SelectOptionValue { Label = "Culiacán, SIN" });
        var areaSelect = page.GetByLabel("Área de servicio (opcional)");
        await areaSelect.Locator("option", new() { HasText = "Centro" })
            .WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.Equal(new[] { "Toda la ciudad", "Centro" }, await areaSelect.Locator("option").AllTextContentsAsync());
        Assert.Equal(string.Empty, await areaSelect.InputValueAsync());
        Assert.Equal(0, await page.GetByText(DriverId, new() { Exact = false }).CountAsync());
        Assert.Equal(0, await page.GetByText(CityId, new() { Exact = false }).CountAsync());
        Assert.False(await create.IsDisabledAsync());
        await create.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta a1000000", Exact = true }).WaitForAsync();
        var createBody = Assert.Single(state.CreateBodies);
        Assert.Equal(DriverId, createBody.GetProperty("driver_id").GetString());
        Assert.Equal(CityId, createBody.GetProperty("city_id").GetString());
        Assert.Equal(JsonValueKind.Null, createBody.GetProperty("service_area_id").ValueKind);
        Assert.Contains(selectorRequests, request => request.PathAndQuery == $"/api/v1/service-areas?city_id={CityId}");
        Assert.All(selectorRequests, request => Assert.Equal(OrganizationA, request.Organization));

        foreach (var orderId in RouteApiState.OrderIds)
        {
            await page.GetByLabel("ID de la orden").FillAsync(orderId);
            await page.GetByRole(AriaRole.Button, new() { Name = "Agregar entrega", Exact = true }).ClickAsync();
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
            "La ruta cambió mientras la editabas. Se cargó la versión más reciente; revisa y vuelve a intentar.",
            new() { Exact = true }).WaitForAsync();
        Assert.True(state.DetailReadsAfterConflict > 0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Mover orden 81000000 arriba", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Retirar orden", Exact = false }).First.ClickAsync();
        await page.GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new() { Name = "Retirar", Exact = true })
            .ClickAsync();
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
        Assert.DoesNotContain(selectorRequests, request =>
            request.Organization != OrganizationA && request.Organization != OrganizationB);
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

    private static (string Organization, string PathAndQuery) SelectorRequest(IRoute route) =>
        (route.Request.Headers.TryGetValue("x-organization-id", out var organization) ? organization : string.Empty,
            new Uri(route.Request.Url).PathAndQuery);

    private static object Driver(string driverId, string reference, string status) => new
    {
        driver_id = driverId,
        driver_reference = reference,
        vehicle_type = "MOTORCYCLE",
        status,
        active_stop_count = 0,
        last_position_at = (string?)null,
    };

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
        internal List<JsonElement> CreateBodies { get; } = [];

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
                CreateBodies.Add(JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement.Clone());
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
