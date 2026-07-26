using System.Text.Json;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

[Collection(DriverStopsPwaCollection.Name)]
public sealed class DriverStopsPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private static readonly object[] Stops =
    [
        new
        {
            order_id = "22222222-2222-2222-2222-222222222222",
            aggregate_version = 3,
            order_public_id = "ORD_abcdefghijklmnopqrstuv",
            stop_type = "PICKUP",
            status = "ASSIGNED",
            address_summary = "Centro, Culiacán",
        },
        new
        {
            order_id = "33333333-3333-3333-3333-333333333333",
            aggregate_version = 4,
            order_public_id = "ORD_bcdefghijklmnopqrstuvw",
            stop_type = "DELIVERY",
            status = "IN_TRANSIT",
            address_summary = "Las Quintas, Culiacán",
        },
    ];

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
        await RouteStopsAsync(page, _ => (200, Stops));

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
        Assert.Contains("Recolección", body);
        Assert.Contains("En tránsito", body);
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
        Assert.Contains("Centro, Culiacán", await page.Locator("body").InnerTextAsync());
        await page.GetByRole(AriaRole.Link, new() { Name = "Volver a mis paradas" }).ClickAsync();
        await page.WaitForURLAsync("**/driver/stops");
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
        await RouteStopsAsync(page, _ => (200, Array.Empty<object>()));
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "No tienes paradas activas.");
        Assert.DoesNotContain("error", await page.Locator("body").InnerTextAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Offline_list_and_detail_use_only_the_current_partition_snapshot()
    {
        var offline = false;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-offline-01");
        await page.RouteAsync(
            "**/api/v1/driver/me/stops",
            route => offline
                ? route.AbortAsync()
                : route.FulfillAsync(JsonResponse(Stops)));

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        offline = true;
        await page.ReloadAsync();
        await ExpectTextAsync(page, "Mostrando última información disponible");
        Assert.Contains("Última sincronización", await page.Locator("body").InnerTextAsync());
        await page.GetByRole(AriaRole.Link, new() { Name = "Ver detalle" }).First.ClickAsync();
        await ExpectTextAsync(page, "Centro, Culiacán");
        Assert.Contains("Sin conexión", await page.Locator("body").InnerTextAsync());
        Assert.Empty(await page.Locator("main").GetByRole(AriaRole.Button).AllAsync());
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Organization_switch_never_renders_the_previous_partition()
    {
        var failRequests = false;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await InstallSessionAsync(page, "opaque-session-switch-01");
        await page.RouteAsync("**/api/v1/driver/me/stops", async route =>
        {
            if (failRequests)
            {
                await route.AbortAsync();
                return;
            }
            var organization = await route.Request.HeaderValueAsync("X-Organization-Id");
            object[] payload = organization == "11111111-1111-1111-1111-111111111111"
                ? Stops[..1]
                :
                [
                    new
                    {
                        order_id = "22222222-2222-2222-2222-222222222222",
                        aggregate_version = 9,
                        order_public_id = "ORD_cdefghijklmnopqrstuvwx",
                        stop_type = "RETURN",
                        status = "RETURNING",
                        address_summary = "Organización B",
                    },
                ];
            await route.FulfillAsync(JsonResponse(payload));
        });

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "Centro, Culiacán");
        await ChangeOrganizationAsync(
            page,
            "22222222-2222-2222-2222-222222222222",
            "opaque-session-switch-01");
        await ExpectTextAsync(page, "Organización B");
        var switched = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Centro, Culiacán", switched);

        failRequests = true;
        await ChangeOrganizationAsync(
            page,
            "33333333-3333-3333-3333-333333333333",
            "opaque-session-switch-01");
        await ExpectTextAsync(page, "No hay información disponible sin conexión");
        Assert.DoesNotContain("Organización B", await page.Locator("body").InnerTextAsync());
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
            "**/api/v1/driver/me/stops",
            route => route.FulfillAsync(
                currentStatus == 200
                    ? JsonResponse(Stops)
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
        await page.RouteAsync("**/api/v1/driver/me/stops", route => route.AbortAsync());
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
        await page.RouteAsync("**/api/v1/driver/me/stops", route =>
        {
            response += 1;
            return response switch
            {
                1 => route.FulfillAsync(JsonResponse(Stops)),
                2 => route.FulfillAsync(JsonResponse(
                new[]
                {
                    new
                    {
                        order_id = "22222222-2222-2222-2222-222222222222",
                        aggregate_version = 4,
                        order_public_id = "ORD_abcdefghijklmnopqrstuv",
                        stop_type = "PICKUP",
                        status = "UNKNOWN",
                        address_summary = "No debe mostrarse",
                    },
                })),
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

    private static async Task InstallSessionAsync(
        IPage page,
        string cacheNamespace,
        string organizationId = "11111111-1111-1111-1111-111111111111",
        string token = "synthetic-driver-token")
    {
        await page.RouteAsync(
            "**/hubs/driver/negotiate**",
            route => route.AbortAsync());
        var configuration = JsonSerializer.Serialize(new
        {
            organizationId,
            cacheNamespace,
            token,
        });
        await page.AddInitScriptAsync(
            $$"""
            (() => {
              const value = {{configuration}};
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
              window.__paquetenviaDriverSession = {
                organizationId: value.organizationId,
                cacheNamespace: value.cacheNamespace,
                getAccessToken: () => "synthetic-driver-token",
              };
              window.dispatchEvent(new Event("paquetenvia:driver-session-changed"));
            }
            """,
            new { organizationId, cacheNamespace });
    }

    private static async Task RouteStopsAsync(
        IPage page,
        Func<IRequest, (int Status, object Payload)> response) =>
        await page.RouteAsync("**/api/v1/driver/me/stops", route =>
        {
            var result = response(route.Request);
            return route.FulfillAsync(
                result.Status == 200
                    ? JsonResponse(result.Payload)
                    : new RouteFulfillOptions { Status = result.Status });
        });

    private static RouteFulfillOptions JsonResponse(object value) => new()
    {
        Status = 200,
        ContentType = "application/json; charset=utf-8",
        Body = JsonSerializer.Serialize(value),
    };

    private static async Task ExpectTextAsync(IPage page, string text) =>
        await page.GetByText(text, new() { Exact = false }).First.WaitForAsync();

    private static Task<bool> HasHorizontalOverflowAsync(IPage page) =>
        page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
}
