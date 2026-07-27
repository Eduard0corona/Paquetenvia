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
        await page.RouteAsync("**/api/v1/driver/me/stops", async route =>
        {
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
              const cache = await caches.open("paquetenvia-driver-shell-v4");
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
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 2);
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
              const cache = await caches.open("paquetenvia-driver-shell-v4");
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
