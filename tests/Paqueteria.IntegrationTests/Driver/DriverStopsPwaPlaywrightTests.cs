using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

[Collection(DriverStopsPwaCollection.Name)]
public sealed class DriverStopsPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private static readonly object[] Stops =
    [
        Stop(
            "22222222-2222-2222-2222-222222222222",
            3,
            "ORD_abcdefghijklmnopqrstuv",
            "PICKUP",
            "ASSIGNED",
            "Centro, Culiacán",
            contactFullName: "Farmacia Sintética",
            zoneName: "Centro"),
        Stop(
            "33333333-3333-3333-3333-333333333333",
            4,
            "ORD_bcdefghijklmnopqrstuvw",
            "DELIVERY",
            "IN_TRANSIT",
            "Las Quintas, Culiacán",
            contactShortName: "M. López",
            zoneName: "Tres Ríos",
            serviceWindow: new
            {
                from = "2026-10-11T20:00:00+00:00",
                to = "2026-10-11T22:00:00+00:00",
            },
            codExpectedCents: 125_000),
    ];

    /// <summary>
    /// Every listMyStops route: the plain path and <c>?details=false</c>, the confirmation read of the sync.
    /// </summary>
    internal const string StopsRoute = "**/api/v1/driver/me/stops**";

    /// <summary>
    /// AI-05 <c>DriverStop</c> with the UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 members. The synthetic marker text
    /// travels as the address the driver sees; <c>address_summary</c> is shown only when the details are unavailable.
    /// </summary>
    internal static object Stop(
        string orderId,
        int aggregateVersion,
        string publicId,
        string stopType,
        string status,
        string addressText,
        string? contactShortName = null,
        string? zoneName = null,
        string? contactFullName = null,
        object? serviceWindow = null,
        long? codExpectedCents = null,
        bool codRecorded = false,
        bool detailsAvailable = true) => new
    {
        order_id = orderId,
        aggregate_version = aggregateVersion,
        order_public_id = publicId,
        stop_type = stopType,
        status,
        address_summary = "Synthetic quote destination",
        details_available = detailsAvailable,
        contact_short_name = detailsAvailable ? contactShortName : null,
        contact_full_name = detailsAvailable ? contactFullName : null,
        zone_name = zoneName,
        service_window = serviceWindow,
        cod = codExpectedCents is { } cents
            ? new { expected = new { currency = "MXN", amount_cents = cents }, recorded = codRecorded }
            : null,
        navigation = detailsAvailable
            ? new
            {
                latitude = 24.8067,
                longitude = -107.3938,
                address_text = addressText,
                references = (string?)null,
            }
            : null,
    };

    /// <summary>
    /// AI-05 <c>DriverStopList</c>, the listMyStops answer. A request with <c>details=false</c> gets the same stops
    /// without their protected details, as the API answers it.
    /// </summary>
    internal static string StopsBody(IRequest request, IEnumerable<object> stops, int completedToday = 0)
    {
        var array = JsonSerializer.SerializeToNode(stops.ToArray())!.AsArray();
        if (new Uri(request.Url).Query == "?details=false")
        {
            foreach (var stop in array.OfType<JsonObject>())
            {
                stop["details_available"] = false;
                stop["contact_short_name"] = null;
                stop["contact_full_name"] = null;
                stop["navigation"] = null;
            }
        }

        return new JsonObject
        {
            ["stops"] = array,
            ["completed_today"] = completedToday,
        }.ToJsonString();
    }

    [Theory]
    [InlineData(320, 568)]
    [InlineData(360, 800)]
    [InlineData(390, 844)]
    [InlineData(430, 932)]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Mobile_list_and_detail_are_accessible_minimal_and_ordered(
        int width,
        int height)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-mobile-01");
        await RouteStopsAsync(page, _ => (200, Stops), completedToday: 3);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        var cards = page.Locator("li");
        Assert.Equal(2, await cards.CountAsync());
        Assert.Contains("ORD_abcdefghijklmnopqrstuv", await cards.Nth(0).InnerTextAsync());
        Assert.Contains("ORD_bcdefghijklmnopqrstuvw", await cards.Nth(1).InnerTextAsync());
        var body = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", body);
        Assert.DoesNotContain("Teléfono", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Costo", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic quote", body, StringComparison.Ordinal);
        Assert.Contains("1 · Recoger", body);
        Assert.Contains("2 · Entregar", body);
        Assert.Contains("Siguiente", await cards.Nth(0).InnerTextAsync());
        Assert.Contains("Pendiente", await cards.Nth(1).InnerTextAsync());
        Assert.Contains("En tránsito", body);
        // UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: zone, short name, window in Mazatlán time and the cash to collect.
        var delivery = await cards.Nth(1).InnerTextAsync();
        Assert.Contains("Tres Ríos · M. López", delivery);
        Assert.Contains("13:00–15:00", delivery);
        Assert.Contains("Cobrar $1,250.00", delivery);
        Assert.Contains("Centro · Farmacia Sintética", await cards.Nth(0).InnerTextAsync());
        // Owner answer "Solo el número": the two open stops plus the three completed today.
        Assert.Contains("Hoy · 5 paradas · 3 hechas · 1 cobro pendiente $1,250.00", body);
        Assert.False(await HasHorizontalOverflowAsync(page));

        var detailLink = cards.Nth(0).GetByRole(AriaRole.Link, new() { Name = "Ver detalle" });
        var box = await detailLink.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Height >= 44);
        await detailLink.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/driver/stops/22222222-2222-2222-2222-222222222222");
        var detailHeading = page.GetByRole(
            AriaRole.Heading,
            new() { Level = 2, Name = "ORD_abcdefghijklmnopqrstuv" });
        await detailHeading.WaitForAsync();
        Assert.Equal(
            "ORD_abcdefghijklmnopqrstuv",
            await detailHeading.InnerTextAsync());
        var detailBody = await page.Locator("body").InnerTextAsync();
        Assert.Contains("Centro, Culiacán", detailBody);
        // Owner answer "Completo en recolección": the pickup shows the full contact name.
        Assert.Contains("Recoger con Farmacia Sintética", detailBody);
        var directions = page.GetByRole(AriaRole.Link, new() { Name = "Cómo llegar" });
        Assert.Equal(
            "https://www.google.com/maps/search/?api=1&query=24.8067%2C-107.3938",
            await directions.GetAttributeAsync("href"));
        Assert.Equal("_blank", await directions.GetAttributeAsync("target"));
        Assert.Equal("noopener noreferrer", await directions.GetAttributeAsync("rel"));
        var directionsBox = await directions.BoundingBoxAsync();
        Assert.NotNull(directionsBox);
        Assert.True(directionsBox.Height >= 44);
        Assert.False(await HasHorizontalOverflowAsync(page));
        await page.GetByRole(AriaRole.Link, new() { Name = "Volver a mis paradas" }).ClickAsync();
        await page.WaitForURLAsync("**/driver/stops");
        Assert.False(await HasHorizontalOverflowAsync(page));
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12 on the smallest phone: "No se pudo entregar" opens "¿Qué pasó?" with
    /// 44 px reasons, "No pagó el cobro" only on the stop that collects cash, "Otro…" with three more and no text
    /// field. A confirmed failed attempt reads "Intento fallido · El despacho decide", offers nothing, and its cash
    /// is no longer counted as pending.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Failed_delivery_fits_a_phone_and_a_failed_attempt_waits_for_dispatch()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 320, Height = 568 },
        });
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-failed-01");
        const string delivering = "44444444-4444-4444-8444-444444444444";
        const string failed = "55555555-5555-4555-8555-555555555555";
        await RouteStopsAsync(
            page,
            _ => (200,
            [
                Stop(delivering, 8, "ORD_cdefghijklmnopqrstuvwx", "DELIVERY", "DELIVERING", "Calle Sintética 8",
                    contactShortName: "M. López", codExpectedCents: 125_000),
                Stop(failed, 9, "ORD_defghijklmnopqrstuvwxy", "DELIVERY", "FAILED_ATTEMPT", "Calle Sintética 9",
                    contactShortName: "R. Pérez", codExpectedCents: 80_000),
            ]));

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_defghijklmnopqrstuvwxy");
        var cards = page.Locator("li");
        Assert.Contains("Intento fallido · El despacho decide", await cards.Nth(1).InnerTextAsync());
        Assert.DoesNotContain("Cobrar $800.00", await cards.Nth(1).InnerTextAsync());
        Assert.Contains("1 cobro pendiente $1,250.00", await page.Locator("body").InnerTextAsync());

        await page.GotoAsync(new Uri(server.BaseAddress, $"/driver/stops/{delivering}").AbsoluteUri);
        await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Group, new() { Name = "¿Qué pasó?" }).WaitForAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = "Otro…" }).CheckAsync();
        foreach (var reason in new[]
                 {
                     "No había nadie", "Dirección incorrecta", "Rechazó el paquete", "No pagó el cobro", "Otro…",
                     "Acceso restringido", "Paquete dañado", "Riesgo de seguridad",
                 })
        {
            var option = await page.Locator("label", new() { HasText = reason }).BoundingBoxAsync();
            Assert.NotNull(option);
            Assert.True(option.Height >= 44, $"{reason} is {option.Height}px high");
        }
        foreach (var name in new[] { "Enviar reporte", "Cancelar" })
        {
            var button = await page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).BoundingBoxAsync();
            Assert.NotNull(button);
            Assert.True(button.Height >= 44, $"{name} is {button.Height}px high");
        }
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Enviar reporte" }).IsDisabledAsync());
        Assert.Equal(0, await page.Locator("textarea, input[type=text], input:not([type])").CountAsync());
        Assert.False(await HasHorizontalOverflowAsync(page));
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancelar", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar", Exact = true }).WaitForAsync();

        await page.GotoAsync(new Uri(server.BaseAddress, $"/driver/stops/{failed}").AbsoluteUri);
        await ExpectTextAsync(page, "Intento fallido · El despacho decide");
        Assert.Equal(
            0,
            await page.GetByRole(AriaRole.Button, new() { Name = "No se pudo entregar" }).CountAsync());
        Assert.Equal(0, await page.Locator("input[type=file]").CountAsync());
        Assert.False(await HasHorizontalOverflowAsync(page));
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Empty_and_offline_states_do_not_invent_data()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-empty-001");
        await RouteStopsAsync(page, _ => (200, Array.Empty<object>()), completedToday: 2);
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "No tienes paradas activas.");
        await ExpectTextAsync(page, "Hoy · 2 paradas · 2 hechas");
        Assert.DoesNotContain("error", await page.Locator("body").InnerTextAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 review: one stop that breaks the contract never empties the list,
    /// and a stop whose details the server could not read says so instead of showing a contact or "Cómo llegar".
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Broken_stop_is_left_out_and_unreadable_details_are_announced()
    {
        const string unreadable = "55555555-5555-5555-5555-555555555555";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-degraded-01");
        await RouteStopsAsync(
            page,
            _ => (200,
                [
                    Stops[0],
                    Stop(
                        "44444444-4444-4444-4444-444444444444",
                        2,
                        "ORD_cdefghijklmnopqrstuvwx",
                        "PICKUP",
                        "UNKNOWN",
                        "No debe mostrarse"),
                    Stop(
                        unreadable,
                        5,
                        "ORD_efghijklmnopqrstuvwxyz",
                        "DELIVERY",
                        "DELIVERING",
                        "No debe mostrarse",
                        contactShortName: "M. López",
                        zoneName: "Tres Ríos",
                        codExpectedCents: 125_000,
                        detailsAvailable: false),
                ]),
            completedToday: 1);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_efghijklmnopqrstuvwxyz");
        var cards = page.Locator("li");
        Assert.Equal(2, await cards.CountAsync());
        var body = await page.Locator("body").InnerTextAsync();
        Assert.Contains("Hoy · 3 paradas · 1 hecha", body);
        Assert.DoesNotContain("ORD_cdefghijklmnopqrstuvwx", body);
        Assert.DoesNotContain("No debe mostrarse", body);
        var degraded = await cards.Nth(1).InnerTextAsync();
        Assert.Contains("Datos de entrega no disponibles por ahora", degraded);
        Assert.Contains("Tres Ríos", degraded);
        Assert.DoesNotContain("M. López", degraded);
        Assert.Contains("Synthetic quote destination", degraded);

        await cards.Nth(1).GetByRole(AriaRole.Link, new() { Name = "Ver detalle" }).ClickAsync();
        await page.WaitForURLAsync($"**/driver/stops/{unreadable}");
        await ExpectTextAsync(page, "Datos de entrega no disponibles por ahora");
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Cómo llegar" }).CountAsync());
        var detail = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Entregar a", detail);
        Assert.DoesNotContain("M. López", detail);
        Assert.Contains("Synthetic quote destination", detail);
        // The cash does not depend on the protected details.
        Assert.Contains("Recibí $1,250.00", detail);
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Unvisited_detail_uses_the_generic_shell_when_browser_is_really_offline()
    {
        const string orderId = "22222222-2222-2222-2222-222222222222";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-offline-01");
        await RouteStopsAsync(page, _ => (200, Stops));
        var detailDocumentRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.ResourceType == "document" &&
                new Uri(request.Url).AbsolutePath.EndsWith(orderId, StringComparison.Ordinal))
            {
                detailDocumentRequests += 1;
            }
        };

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        await WaitForSnapshotCountAsync(page, 1);
        await EnsureServiceWorkerControlAsync(page);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        await AssertGenericShellIsCompleteAndPrivateAsync(page, orderId);
        Assert.Equal(0, detailDocumentRequests);

        await page.UnrouteAllAsync();
        await context.SetOfflineAsync(true);
        try
        {
            Assert.True(await NetworkIsUnavailableAsync(page, "/__next_origin_probe__"));
            Assert.True(await NetworkIsUnavailableAsync(
                page,
                "/api/v1/driver/me/stops"));

            await page.GetByRole(AriaRole.Link, new() { Name = "Ver detalle" })
                .First.ClickAsync();
            await page.WaitForURLAsync($"**/driver/stops/{orderId}");
            await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
            await ExpectTextAsync(page, "Mostrando última información disponible");
            var detail = await page.Locator("body").InnerTextAsync();
            Assert.Contains("Centro, Culiacán", detail);
            Assert.DoesNotContain(orderId, detail);

            await page.ReloadAsync();
            await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
            await ExpectTextAsync(page, "Sin conexión");
            Assert.Contains("Centro, Culiacán", await page.Locator("body").InnerTextAsync());

            await page.GoBackAsync();
            await page.WaitForURLAsync("**/driver/stops");
            await ExpectTextAsync(page, "ORD_bcdefghijklmnopqrstuvw");

            await page.GetByRole(AriaRole.Link, new() { Name = "Ver detalle" })
                .First.ClickAsync();
            await ExpectTextAsync(page, "Centro, Culiacán");
            await page.GetByRole(
                    AriaRole.Link,
                    new() { Name = "Volver a mis paradas" })
                .ClickAsync();
            await page.WaitForURLAsync("**/driver/stops");
            await ExpectTextAsync(page, "ORD_bcdefghijklmnopqrstuvw");

            const string unknownId = "44444444-4444-4444-4444-444444444444";
            await page.GotoAsync(
                new Uri(server.BaseAddress, $"/driver/stops/{unknownId}").AbsoluteUri);
            await ExpectTextAsync(page, "Parada no disponible");
            Assert.DoesNotContain(unknownId, await page.Locator("body").InnerTextAsync());
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Offline_partition_never_renders_another_organization_or_session()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-switch-01");
        await page.RouteAsync(StopsRoute, async route =>
        {
            var organization = await route.Request.HeaderValueAsync("X-Organization-Id");
            object[] payload = organization == "11111111-1111-1111-1111-111111111111"
                ? Stops[..1]
                :
                [
                    Stop(
                        "22222222-2222-2222-2222-222222222222",
                        9,
                        "ORD_cdefghijklmnopqrstuvwx",
                        "RETURN",
                        "RETURNING",
                        "Organización B"),
                ];
            await route.FulfillAsync(JsonResponse(StopsBody(route.Request, payload)));
        });

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "Centro, Culiacán");
        await EnsureServiceWorkerControlAsync(page);
        await ExpectTextAsync(page, "Centro, Culiacán");
        await ChangeOrganizationAsync(
            page,
            "22222222-2222-2222-2222-222222222222",
            "opaque-session-switch-01");
        await ExpectTextAsync(page, "Organización B");
        await WaitForSnapshotCountAsync(page, 2);
        var switched = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Centro, Culiacán", switched);

        await page.UnrouteAllAsync();
        await context.SetOfflineAsync(true);
        try
        {
            await page.GetByRole(AriaRole.Link, new() { Name = "Ver detalle" })
                .ClickAsync();
            await ExpectTextAsync(page, "Organización B");
            var organizationBDetail = await page.Locator("body").InnerTextAsync();
            Assert.DoesNotContain("Centro, Culiacán", organizationBDetail);

            await ChangeOrganizationAsync(
                page,
                "33333333-3333-3333-3333-333333333333",
                "opaque-session-without-snapshot");
            await ExpectTextAsync(page, "Parada no disponible");
            var newPartition = await page.Locator("body").InnerTextAsync();
            Assert.DoesNotContain("Centro, Culiacán", newPartition);
            Assert.DoesNotContain("Organización B", newPartition);
            Assert.DoesNotContain("ORD_abcdefghijklmnopqrstuv", newPartition);
            Assert.DoesNotContain("ORD_cdefghijklmnopqrstuvwx", newPartition);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }
    }

    [Theory]
    [InlineData(401, "Sesión no válida")]
    [InlineData(403, "No tienes acceso a las paradas de repartidor")]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Known_revocation_clears_cache_without_offline_fallback(
        int status,
        string expected)
    {
        var currentStatus = 200;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, $"opaque-session-status-{status}");
        await page.RouteAsync(
            StopsRoute,
            route => route.FulfillAsync(
                currentStatus == 200
                    ? JsonResponse(StopsBody(route.Request, Stops))
                    : new RouteFulfillOptions { Status = currentStatus }));
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");

        currentStatus = status;
        await page.ReloadAsync();
        await ExpectTextAsync(page, expected);
        Assert.DoesNotContain(
            "ORD_abcdefghijklmnopqrstuv",
            await page.Locator("body").InnerTextAsync());

        await page.UnrouteAllAsync();
        await page.RouteAsync(StopsRoute, route => route.AbortAsync());
        await page.ReloadAsync();
        await ExpectTextAsync(page, "No hay información disponible sin conexión");
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Invalid_contract_is_fail_closed_and_does_not_replace_valid_cache()
    {
        var response = 0;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-contract-01");
        await page.RouteAsync(StopsRoute, route =>
        {
            response += 1;
            return response switch
            {
                1 => route.FulfillAsync(JsonResponse(StopsBody(route.Request, Stops))),
                // The answer itself breaks the contract (the bare list of the previous version).
                2 => route.FulfillAsync(JsonResponse(JsonSerializer.Serialize(
                new[]
                {
                    Stop(
                        "22222222-2222-2222-2222-222222222222",
                        4,
                        "ORD_abcdefghijklmnopqrstuv",
                        "PICKUP",
                        "ASSIGNED",
                        "No debe mostrarse"),
                }))),
                _ => route.AbortAsync(),
            };
        });
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        await page.ReloadAsync();
        await ExpectTextAsync(page, "La respuesta no pudo validarse de forma segura");
        Assert.DoesNotContain("No debe mostrarse", await page.Locator("body").InnerTextAsync());
        Assert.DoesNotContain("ORD_abcdefghijklmnopqrstuv",
            await page.Locator("body").InnerTextAsync());
        await page.ReloadAsync();
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        Assert.Contains("Mostrando última información disponible",
            await page.Locator("body").InnerTextAsync());
    }

    private static async Task EnsureServiceWorkerControlAsync(IPage page)
    {
        await page.EvaluateAsync(
            "() => navigator.serviceWorker.ready.then(() => undefined)");
        await page.ReloadAsync();
        await page.WaitForFunctionAsync(
            "() => navigator.serviceWorker.controller !== null");
        await page.WaitForFunctionAsync(
            """
            async () => {
              const cache = await caches.open("paquetenvia-driver-shell-v7");
              const keys = await cache.keys();
              return keys.some(key => new URL(key.url).pathname.startsWith("/_next/static/"));
            }
            """);
    }

    private static async Task WaitForSnapshotCountAsync(IPage page, int minimum)
    {
        await page.WaitForFunctionAsync(
            """
            minimum => new Promise(resolve => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 4);
              open.onerror = () => resolve(false);
              open.onsuccess = () => {
                const database = open.result;
                const count = database
                  .transaction("snapshots", "readonly")
                  .objectStore("snapshots")
                  .count();
                count.onerror = () => {
                  database.close();
                  resolve(false);
                };
                count.onsuccess = () => {
                  database.close();
                  resolve(count.result >= minimum);
                };
              };
            })
            """,
            minimum);
    }

    private static async Task AssertGenericShellIsCompleteAndPrivateAsync(
        IPage page,
        string unvisitedOrderId)
    {
        var state = await page.EvaluateAsync<bool[]>(
            """
            async orderId => {
              const cache = await caches.open("paquetenvia-driver-shell-v7");
              const shell = await cache.match("/driver/stops");
              const exact = await cache.match(`/driver/stops/${orderId}`);
              const shellText = shell ? await shell.clone().text() : "";
              const keys = await cache.keys();
              const hasStaticChunks = keys.some(
                key => new URL(key.url).pathname.startsWith("/_next/static/"));
              const isPrivate =
                !shellText.includes(orderId) &&
                !shellText.includes("ORD_abcdefghijklmnopqrstuv") &&
                !shellText.includes("Centro, Culiacán");
              return [Boolean(shell), Boolean(exact), hasStaticChunks, isPrivate];
            }
            """,
            unvisitedOrderId);

        Assert.True(state[0]);
        Assert.False(state[1]);
        Assert.True(state[2]);
        Assert.True(state[3]);
    }

    private static Task<bool> NetworkIsUnavailableAsync(IPage page, string pathname) =>
        page.EvaluateAsync<bool>(
            """
            async path => {
              try {
                await fetch(path, { cache: "no-store" });
                return false;
              } catch {
                return true;
              }
            }
            """,
            pathname);

    private static async Task InstallSessionAsync(
        IPage page,
        string cacheNamespace,
        string organizationId = "11111111-1111-1111-1111-111111111111",
        string token = "synthetic-driver-token")
    {
        // Registered first so the per-test API routes below take precedence:
        // any call to the reserved test origin that a test did not fulfil is
        // blocked here instead of reaching the network, and the assertions that
        // depend on it then fail.
        await page.RouteAsync(
            $"{DriverStopsNextServerFixture.TestApiOrigin}/**",
            route => route.AbortAsync("blockedbyclient"));
        await page.RouteAsync(
            "**/hubs/driver/negotiate**",
            route => route.AbortAsync());
        // ORG-MY-ROLES-2026-10-12: the roles read answers as the API does for a DRIVER.
        await DriverOrganizationRoles.RouteAsync(page);
        var configuration = JsonSerializer.Serialize(new
        {
            organizationId,
            cacheNamespace,
            token,
        });
        await page.AddInitScriptAsync(
            $$"""
            (() => {
              const initial = {{configuration}};
              const stored = window.sessionStorage.getItem(
                "paquetenvia-driver-test-session");
              const value = stored ? JSON.parse(stored) : initial;
              window.__paquetenviaDriverSession = {
                organizationId: value.organizationId,
                cacheNamespace: value.cacheNamespace,
                getAccessToken: () => value.token,
              };
            })();
            """);
    }

    private static async Task ChangeOrganizationAsync(
        IPage page,
        string organizationId,
        string cacheNamespace)
    {
        await page.EvaluateAsync(
            """
            value => {
              window.sessionStorage.setItem(
                "paquetenvia-driver-test-session",
                JSON.stringify(value));
              window.__paquetenviaDriverSession = {
                organizationId: value.organizationId,
                cacheNamespace: value.cacheNamespace,
                getAccessToken: () => "synthetic-driver-token",
              };
              window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
            }
            """,
            new
            {
                organizationId,
                cacheNamespace,
                token = "synthetic-driver-token",
            });
    }

    private static async Task RouteStopsAsync(
        IPage page,
        Func<IRequest, (int Status, object[] Stops)> response,
        int completedToday = 0) =>
        await page.RouteAsync(StopsRoute, route =>
        {
            var result = response(route.Request);
            return route.FulfillAsync(
                result.Status == 200
                    ? JsonResponse(StopsBody(route.Request, result.Stops, completedToday))
                    : new RouteFulfillOptions { Status = result.Status });
        });

    private static RouteFulfillOptions JsonResponse(string body) => new()
    {
        Status = 200,
        ContentType = "application/json; charset=utf-8",
        Body = body,
    };

    private static async Task ExpectTextAsync(IPage page, string text) =>
        await page.GetByText(text, new() { Exact = false }).First.WaitForAsync();

    private static Task<bool> HasHorizontalOverflowAsync(IPage page) =>
        page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
}
