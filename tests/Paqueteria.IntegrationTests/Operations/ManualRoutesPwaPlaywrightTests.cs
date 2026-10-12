using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class ManualRoutesPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string RouteA = "a1000000-0000-0000-0000-000000000001";
    private const string RouteB = "b2000000-0000-0000-0000-000000000001";
    private const string DriverId = "55555555-5555-5555-5555-555555555551";
    private const string SuspendedDriverId = "55555555-5555-5555-5555-555555555552";
    // UI-PHASE3-ROUTES-NO-IDS-2026-10-12: a driver listOrganizationDrivers does not return (for example an inactive one).
    private const string UnlistedDriverId = "55555555-5555-5555-5555-555555555559";
    private const string CityId = "33333333-3333-3333-3333-333333333333";
    private const string ServiceAreaId = "34343434-3434-3434-3434-343434343434";
    private const string RetiredServiceAreaId = "35353535-3535-3535-3535-353535353535";
    private const string OrderB = "8b000000-0000-0000-0000-000000000001";
    private const string GuideB = "ORD_RutasPlaywrightOrgB001";

    private static readonly string[] OrderIds =
    [
        "81000000-0000-0000-0000-000000000001",
        "82000000-0000-0000-0000-000000000001",
        "83000000-0000-0000-0000-000000000001",
    ];

    /// <summary>The guía (Order.public_id, ORD_ and 22 characters) of each order of organization A.</summary>
    private static readonly string[] Guides =
    [
        "ORD_RutasPlaywright0000001",
        "ORD_RutasPlaywright0000002",
        "ORD_RutasPlaywright0000003",
    ];

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
                    ? Detail(RouteB, UnlistedDriverId, "2026-09-15", 1, [("bb000000-0000-0000-0000-000000000001", OrderB)])
                    : Page(Summary(RouteB, UnlistedDriverId, "2026-09-15", 1, 0, 1)));
                return;
            }
            Assert.Equal(OrganizationA, organization);
            await state.HandleAsync(route);
        });
        var orders = new OrderApiState();
        await page.RouteAsync("**/api/v1/orders**", orders.HandleAsync);
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
        await page.GetByLabel("Fecha", new() { Exact = true }).FillAsync("2026-08-30");
        Assert.False(await create.IsDisabledAsync());
        await create.ClickAsync();

        // UI-PHASE3-ROUTES-NO-IDS-2026-10-12: the route by its date, the driver by DRV-, never an id or YYYY-MM-DD.
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta del 30 de agosto de 2026", Exact = true }).WaitForAsync();
        var createBody = Assert.Single(state.CreateBodies);
        Assert.Equal(DriverId, createBody.GetProperty("driver_id").GetString());
        Assert.Equal(CityId, createBody.GetProperty("city_id").GetString());
        Assert.Equal(JsonValueKind.Null, createBody.GetProperty("service_area_id").ValueKind);
        Assert.Equal("2026-08-30", createBody.GetProperty("scheduled_for").GetString());
        Assert.Contains(selectorRequests, request => request.PathAndQuery == $"/api/v1/service-areas?city_id={CityId}");
        Assert.All(selectorRequests, request => Assert.Equal(OrganizationA, request.Organization));
        await page.Locator(".opsRouteDetail header p", new() { HasText = "Repartidor DRV-55555555 · Borrador · Sin paradas" })
            .WaitForAsync();
        Assert.Contains("Ruta del 30 de agosto de 2026", await page.Locator(".opsRouteList").InnerTextAsync());
        Assert.Equal(0, await page.GetByText("2026-08-30", new() { Exact = false }).CountAsync());
        Assert.Equal(0, await page.GetByText("ID de la orden", new() { Exact = false }).CountAsync());

        // "Agregar entrega" takes the guía (ORD_…), resolved with listOrders?public_id=.
        var guide = page.GetByLabel("Guía (ORD_…)", new() { Exact = true });
        var add = page.GetByRole(AriaRole.Button, new() { Name = "Agregar entrega", Exact = true });
        for (var index = 0; index < Guides.Length; index++)
        {
            await guide.FillAsync(Guides[index]);
            await add.ClickAsync();
            await ExpectStopCountAsync(page, index + 1);
            // A saved stop empties the field.
            await ExpectInputValueAsync(guide, string.Empty);
        }
        Assert.Equal(OrderIds, state.AddedOrderIds);
        Assert.Equal(Guides, orders.Lookups);
        await ExpectSequenceAsync(page, Guides[0], Guides[1], Guides[2]);

        // An unknown guía is not found and a value that cannot be one is refused before any call.
        await guide.FillAsync("ORD_RutasPlaywright0000099");
        await add.ClickAsync();
        await ExpectStatusAsync(page, "No encontramos esa guía");
        await guide.FillAsync("PQ-000123");
        await add.ClickAsync();
        await ExpectStatusAsync(page, "No encontramos esa guía");
        Assert.Equal(Guides.Append("ORD_RutasPlaywright0000099").ToArray(), orders.Lookups);
        Assert.Equal(3, state.AddedOrderIds.Count);

        var stops = page.Locator(".opsRouteStops > li");
        await stops.Nth(0).DispatchEventAsync("dragstart");
        await stops.Nth(2).DispatchEventAsync("dragover");
        await stops.Nth(2).DispatchEventAsync("drop");
        await ExpectSequenceAsync(page, Guides[1], Guides[2], Guides[0]);

        // Only VERSION_CONFLICT says the route changed, and it reads the route again.
        state.FailNextReorderAsStale = true;
        await page.GetByRole(AriaRole.Button, new() { Name = $"Mover orden {Guides[2]} arriba", Exact = true }).ClickAsync();
        await ExpectStatusAsync(page, "La ruta cambió mientras la editabas. Se cargó la versión más reciente; revisa y vuelve a intentar.");
        Assert.True(state.DetailReadsAfterConflict > 0);

        await page.GetByRole(AriaRole.Button, new() { Name = $"Mover orden {Guides[0]} arriba", Exact = true }).ClickAsync();
        await ExpectSequenceAsync(page, Guides[1], Guides[0], Guides[2]);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Retirar orden {Guides[1]} de la ruta", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByText($"Se retirará de la ruta la parada 1 (entrega de la orden {Guides[1]}).", new() { Exact = false })
            .WaitForAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Retirar", Exact = true }).ClickAsync();
        await ExpectStopCountAsync(page, 2);
        Assert.True(state.LastMutationVersion == state.Version);

        // Any other code says its own reason and reads nothing again.
        var readsBefore = state.DetailReads;
        state.FailNextAddCode = "DRIVER_INELIGIBLE";
        await guide.FillAsync(Guides[1]);
        await add.ClickAsync();
        await ExpectStatusAsync(page, "La orden no entra en esta ruta: es de otra ciudad o área, o el repartidor ya no puede llevarla.");
        Assert.Equal(readsBefore, state.DetailReads);

        // Only MFA missing: "Verificar identidad" back to Rutas and this route.
        state.FailNextRemoveWithMfa = true;
        await page.GetByRole(AriaRole.Button, new() { Name = $"Retirar orden {Guides[0]} de la ruta", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Retirar", Exact = true }).ClickAsync();
        await ExpectStatusAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        var stepUp = page.GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true });
        Assert.Equal(
            $"/login?mfa=required&return_url=%2Fops%2Froutes%3FrouteId%3D{RouteA}",
            await stepUp.GetAttributeAsync("href"));
        await ExpectStopCountAsync(page, 2);
        await AssertNoIdentifiersAsync(page);

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
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta del 15 de septiembre de 2026", Exact = true }).WaitForAsync();
        // A driver outside listOrganizationDrivers is never named by its id; the stop shows its guía from getOrder.
        await page.Locator(".opsRouteDetail header p", new() { HasText = "Repartidor no disponible · Borrador · 1 parada" })
            .WaitForAsync();
        await ExpectSequenceAsync(page, GuideB);
        foreach (var guideA in Guides)
            Assert.Equal(0, await page.GetByText(guideA, new() { Exact = false }).CountAsync());
        await AssertNoIdentifiersAsync(page);
        Assert.DoesNotContain(state.RequestOrganizations, organization =>
            organization != OrganizationA && organization != OrganizationB);
        Assert.DoesNotContain(selectorRequests, request =>
            request.Organization != OrganizationA && request.Organization != OrganizationB);
        Assert.Contains((OrganizationB, OrderB), orders.GuideReads);
        Assert.DoesNotContain(orders.GuideReads, read => read.Organization == OrganizationB && read.OrderId != OrderB);
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Planner_prefills_the_guia_of_orderId_and_names_every_stop_by_guia()
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
        // A route that already has one stop, the order of Guides[1].
        var state = new RouteApiState(scheduledFor: null, initialOrderIds: [OrderIds[1]]);
        await page.RouteAsync("**/api/v1/routes**", state.HandleAsync);
        var orders = new OrderApiState();
        await page.RouteAsync("**/api/v1/orders**", orders.HandleAsync);
        await page.RouteAsync("**/api/v1/drivers**", route => FulfillAsync(route, new
        {
            items = new object[] { Driver(DriverId, "DRV-55555555", "ACTIVE") },
            next_cursor = (string?)null,
        }));
        await page.RouteAsync("**/api/v1/cities**", route => FulfillAsync(route, Array.Empty<object>()));

        // The dashboard's "Agregar a ruta" opens Rutas with ?orderId=: the field shows that order's guía, never the id.
        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/routes?orderId={OrderIds[0]}").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Rutas manuales", Exact = true }).WaitForAsync();
        await ExpectStatusAsync(page, "Selecciona una ruta para agregar la orden indicada.");
        await page.Locator(".opsRouteList button").First.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta sin fecha", Exact = true }).WaitForAsync();
        await page.Locator(".opsRouteDetail header p", new() { HasText = "Repartidor DRV-55555555 · Borrador · 1 parada" })
            .WaitForAsync();
        var guide = page.GetByLabel("Guía (ORD_…)", new() { Exact = true });
        await ExpectInputValueAsync(guide, Guides[0]);
        await ExpectSequenceAsync(page, Guides[1]);
        Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = $"Retirar orden {Guides[1]} de la ruta", Exact = true }).CountAsync());
        Assert.Contains((OrganizationA, OrderIds[0]), orders.GuideReads);
        Assert.Contains((OrganizationA, OrderIds[1]), orders.GuideReads);
        await AssertNoIdentifiersAsync(page);

        // The prefilled guía is added like a typed one.
        await page.GetByRole(AriaRole.Button, new() { Name = "Agregar entrega", Exact = true }).ClickAsync();
        await ExpectStopCountAsync(page, 2);
        Assert.Equal(new[] { OrderIds[0] }, state.AddedOrderIds);
        await ExpectSequenceAsync(page, Guides[1], Guides[0]);

        // An order that cannot be read leaves the field empty.
        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/routes?orderId=8f000000-0000-0000-0000-000000000001").AbsoluteUri);
        await page.Locator(".opsRouteList button").First.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ruta sin fecha", Exact = true }).WaitForAsync();
        await ExpectSequenceAsync(page, Guides[1], Guides[0]);
        Assert.Equal(string.Empty, await page.GetByLabel("Guía (ORD_…)", new() { Exact = true }).InputValueAsync());
        Assert.Contains((OrganizationA, "8f000000-0000-0000-0000-000000000001"), orders.GuideReads);
        await AssertNoIdentifiersAsync(page);
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

    /// <summary>The stops in order, each named "N. ORD_…" by its guía.</summary>
    private static async Task ExpectSequenceAsync(IPage page, params string[] guides)
    {
        var expected = guides.Select((guide, index) => $"{index + 1}. {guide}").ToArray();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<string> actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator(".opsRouteStops li strong").AllTextContentsAsync();
            if (actual.Select(text => text.Trim()).SequenceEqual(expected, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }
        Assert.Fail($"The route stops did not match the authoritative response: {string.Join(" | ", actual)}");
    }

    private static async Task ExpectStatusAsync(IPage page, string text) =>
        await page.GetByRole(AriaRole.Status).GetByText(text, new() { Exact = false }).First.WaitForAsync();

    private static async Task ExpectInputValueAsync(ILocator input, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await input.InputValueAsync() == expected) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Expected the field to hold \"{expected}\".");
    }

    /// <summary>No internal id is visible: neither whole nor as the 8-character fragment Rutas used to show.</summary>
    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        foreach (var id in new[] { RouteA, RouteB, CityId, OrderB }.Concat(OrderIds))
            Assert.DoesNotContain(id[..8], visible, StringComparison.Ordinal);
        // The synthetic DRV-55555555 reference happens to repeat the drivers' first 8 characters; nothing else may.
        Assert.DoesNotMatch(DriverFragmentPattern(), visible);
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();

    [GeneratedRegex("(?<!DRV-)55555555")]
    private static partial Regex DriverFragmentPattern();

    private static object Summary(string routeId, string driverId, string? scheduledFor, int version, long cost, int stopCount) => new
    {
        id = routeId,
        status = "DRAFT",
        version,
        driver_id = driverId,
        city_id = CityId,
        service_area_id = (string?)null,
        scheduled_for = scheduledFor,
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

    private static object Detail(
        string routeId,
        string driverId,
        string? scheduledFor,
        int version,
        IReadOnlyList<(string StopId, string OrderId)> stops) => new
    {
        id = routeId,
        status = "DRAFT",
        version,
        driver_id = driverId,
        city_id = CityId,
        service_area_id = (string?)null,
        scheduled_for = scheduledFor,
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

    private static Task FulfillAsync(IRoute route, object body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(body),
        });

    private static Task ProblemAsync(IRoute route, int status, string? code = null) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/problem+json",
            Body = code is null
                ? JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status })
                : JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status, code }),
        });

    /// <summary>
    /// listOrders?public_id= and getOrder of organizations A and B: every order of <see cref="OrderIds"/> has its
    /// <see cref="Guides"/> guía in A, <see cref="OrderB"/> has <see cref="GuideB"/> in B; anything else is not found.
    /// </summary>
    private sealed class OrderApiState
    {
        internal List<string> Lookups { get; } = [];
        internal List<(string Organization, string OrderId)> GuideReads { get; } = [];

        internal async Task HandleAsync(IRoute route)
        {
            var organization = route.Request.Headers.TryGetValue("x-organization-id", out var value) ? value : string.Empty;
            var uri = new Uri(route.Request.Url);
            Assert.Equal("GET", route.Request.Method);
            var known = organization == OrganizationB
                ? new[] { (OrderB, GuideB) }
                : OrderIds.Zip(Guides).ToArray();
            if (uri.AbsolutePath == "/api/v1/orders")
            {
                var publicId = System.Web.HttpUtility.ParseQueryString(uri.Query)["public_id"] ?? string.Empty;
                Lookups.Add(publicId);
                var match = known.Where(order => order.Item2 == publicId)
                    .Select(order => (object)new { id = order.Item1, public_id = order.Item2, status = "ASSIGNED", version = 3 })
                    .ToArray();
                await FulfillAsync(route, new { items = match, next_cursor = (string?)null });
                return;
            }
            var orderId = uri.AbsolutePath["/api/v1/orders/".Length..];
            GuideReads.Add((organization, orderId));
            var found = known.Where(order => order.Item1 == orderId).ToArray();
            if (found.Length == 0)
            {
                await ProblemAsync(route, 404);
                return;
            }
            await FulfillAsync(route, OrderDetail(orderId, found[0].Item2, organization));
        }

        private static object OrderDetail(string orderId, string publicId, string organization) => new
        {
            id = orderId,
            public_id = publicId,
            owner_org_id = organization,
            operator_org_id = (string?)null,
            status = "ASSIGNED",
            price_net = new { currency = "MXN", amount_cents = 8_000L },
            version = 3,
            origin_location_id = "61000000-0000-0000-0000-000000000001",
            destination_location_id = "62000000-0000-0000-0000-000000000001",
            service_type = "SAME_DAY",
            quote_id = "63000000-0000-0000-0000-000000000001",
            city_id = CityId,
            service_area_id = (string?)null,
            pricing_tier = "OCCASIONAL",
            total = new { currency = "MXN", amount_cents = 9_280L },
            claim_window_ends_at = (string?)null,
            finalized_at = (string?)null,
            service_window = (object?)null,
            timeline = Array.Empty<object>(),
        };
    }

    private sealed class RouteApiState
    {
        private readonly List<(string StopId, string OrderId)> stops = [];
        private bool exists;
        private int reorderCalls;
        private string? scheduledFor;

        internal RouteApiState(string? scheduledFor = "2026-08-30", IReadOnlyList<string>? initialOrderIds = null)
        {
            this.scheduledFor = scheduledFor;
            if (initialOrderIds is null) return;
            exists = true;
            Version = LastMutationVersion = 1;
            foreach (var orderId in initialOrderIds) stops.Add(($"9{stops.Count + 1}000000-0000-0000-0000-000000000001", orderId));
        }

        internal bool FailNextReorderAsStale { get; set; }
        internal string? FailNextAddCode { get; set; }
        internal bool FailNextRemoveWithMfa { get; set; }
        internal int Version { get; private set; }
        internal int LastMutationVersion { get; private set; }
        internal int DetailReads { get; private set; }
        internal int DetailReadsAfterConflict { get; private set; }
        internal List<string> RequestOrganizations { get; } = [];
        internal List<JsonElement> CreateBodies { get; } = [];
        internal List<string> AddedOrderIds { get; } = [];

        internal async Task HandleAsync(IRoute route)
        {
            RequestOrganizations.Add(route.Request.Headers["x-organization-id"]);
            var uri = new Uri(route.Request.Url);
            var path = uri.AbsolutePath;
            var method = route.Request.Method;
            if (method == "GET" && path == "/api/v1/routes")
            {
                await FulfillAsync(route, exists ? Page(Summary(RouteA, DriverId, scheduledFor, Version, stops.Count * 1_000L, stops.Count)) :
                    new { items = Array.Empty<object>(), next_cursor = (string?)null });
                return;
            }
            if (method == "POST" && path == "/api/v1/routes")
            {
                var body = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement.Clone();
                CreateBodies.Add(body);
                scheduledFor = body.GetProperty("scheduled_for").ValueKind == JsonValueKind.Null
                    ? null
                    : body.GetProperty("scheduled_for").GetString();
                exists = true;
                Version = LastMutationVersion = 1;
                await FulfillAsync(route, Summary(RouteA, DriverId, scheduledFor, Version, 0, 0), 201);
                return;
            }
            if (method == "GET" && path == $"/api/v1/routes/{RouteA}")
            {
                DetailReads++;
                if (reorderCalls > 0 && FailNextReorderAsStale is false) DetailReadsAfterConflict++;
                await FulfillAsync(route, Detail());
                return;
            }
            if (method == "POST" && path == $"/api/v1/routes/{RouteA}/stops")
            {
                if (FailNextAddCode is { } code)
                {
                    FailNextAddCode = null;
                    await ProblemAsync(route, 409, code);
                    return;
                }
                var body = JsonDocument.Parse(route.Request.PostData ?? "{}").RootElement;
                var orderId = body.GetProperty("order_id").GetString()!;
                Assert.Equal(Version, body.GetProperty("expected_version").GetInt32());
                AddedOrderIds.Add(orderId);
                stops.Add(($"9{stops.Count + 1}000000-0000-0000-0000-00000000000{AddedOrderIds.Count}", orderId));
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
                    await ProblemAsync(route, 409, "VERSION_CONFLICT");
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
                if (FailNextRemoveWithMfa)
                {
                    FailNextRemoveWithMfa = false;
                    await ProblemAsync(route, 403, "MFA_REQUIRED");
                    return;
                }
                var stopId = path[(path.LastIndexOf('/') + 1)..];
                stops.RemoveAll(stop => stop.StopId == stopId);
                Version = LastMutationVersion = checked(Version + 1);
                await FulfillAsync(route, Detail());
                return;
            }
            await ProblemAsync(route, 404);
        }

        private object Detail() => ManualRoutesPwaPlaywrightTests.Detail(RouteA, DriverId, scheduledFor, Version, stops);
    }
}
