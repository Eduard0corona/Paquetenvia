using Microsoft.Playwright;
using static Paqueteria.IntegrationTests.Driver.DriverStopsPwaPlaywrightTests;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// The driver PWA in the pilot's sign-in mode (AuthCenter BFF, <c>NEXT_PUBLIC_AUTH_MODE=bff</c>): the session lives in
/// the page's memory only and every page load rebuilds it from <c>GET /auth/session</c>, which is network-only.
/// DRV-PWA-INAPP-NAVIGATION: moving between the list and a stop never loads a page, so the session and the stops stay
/// also offline; a screen without a usable session offers "Iniciar sesión" with a validated return URL.
/// </summary>
[Collection(DriverStopsBffPwaCollection.Name)]
public sealed class DriverStopsBffPwaPlaywrightTests(DriverStopsBffNextServerFixture server)
{
    private const string PickupOrderId = "22222222-2222-2222-2222-222222222222";

    private static readonly object[] Stops =
    [
        Stop(
            PickupOrderId,
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
            zoneName: "Tres Ríos"),
    ];

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Bff_list_detail_and_back_stay_in_the_page_offline_and_keep_the_session()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await DriverBffSession.LaunchAsync(playwright);
        await using var context = await DriverBffSession.NewContextAsync(browser);
        var page = await context.NewPageAsync();
        await DriverBffSession.BlockUnfulfilledAsync(page, server.BaseAddress);
        await DriverOrganizationRoles.RouteAsync(page);
        await DriverBffSession.RouteSignedInAsync(page, "bff-navigation-namespace-01");
        await RouteStopsAsync(page);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_abcdefghijklmnopqrstuv");
        await WaitForSnapshotCountAsync(page, 1);
        await EnsureServiceWorkerControlAsync(page);
        await ExpectTextAsync(page, "ORD_bcdefghijklmnopqrstuvw");

        await page.UnrouteAllAsync();
        await context.SetOfflineAsync(true);
        try
        {
            Assert.True(await NetworkIsUnavailableAsync(page, "/auth/session"));
            var documents = 0;
            var sessionReads = 0;
            page.Request += (_, request) =>
            {
                if (request.ResourceType == "document")
                {
                    documents += 1;
                }
                if (new Uri(request.Url).AbsolutePath == "/auth/session")
                {
                    sessionReads += 1;
                }
            };

            await page.GetByRole(AriaRole.Link, new() { Name = "Ver detalle" }).First.ClickAsync();
            await page.WaitForURLAsync($"**/driver/stops/{PickupOrderId}");
            await ExpectTextAsync(page, "Recoger con Farmacia Sintética");
            // As a page load did, the move reads the stops again; offline that is the saved snapshot.
            await ExpectTextAsync(page, "Mostrando última información disponible");
            Assert.Contains("Centro, Culiacán", await page.Locator("body").InnerTextAsync());

            await page.GetByRole(AriaRole.Link, new() { Name = "Volver a mis paradas" }).ClickAsync();
            await page.WaitForURLAsync("**/driver/stops");
            await ExpectTextAsync(page, "ORD_bcdefghijklmnopqrstuvw");
            // Like a page load, the list starts at its title.
            Assert.Equal(
                "driver-stops-title",
                await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''"));

            // The browser's own back and forward stay in the page as well.
            await page.GoBackAsync();
            await page.WaitForURLAsync($"**/driver/stops/{PickupOrderId}");
            await ExpectTextAsync(page, "Recoger con Farmacia Sintética");
            await page.GoForwardAsync();
            await page.WaitForURLAsync("**/driver/stops");
            await ExpectTextAsync(page, "ORD_bcdefghijklmnopqrstuvw");

            var body = await page.Locator("body").InnerTextAsync();
            Assert.Contains("ORD_abcdefghijklmnopqrstuv", body);
            Assert.DoesNotContain("Sesión no disponible", body);
            Assert.Equal(0, documents);
            Assert.Equal(0, sessionReads);
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Bff_session_panels_offer_sign_in_back_to_the_same_stops_path()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await DriverBffSession.LaunchAsync(playwright);
        await using var context = await DriverBffSession.NewContextAsync(browser);
        var page = await context.NewPageAsync();
        await DriverBffSession.BlockUnfulfilledAsync(page, server.BaseAddress);
        var signedIn = false;
        await DriverBffSession.RouteSessionAsync(page, () => signedIn
            ? DriverBffSession.SessionAnswer.SignedIn("bff-sign-in-namespace-001")
            : DriverBffSession.SessionAnswer.Anonymous);
        await DriverBffSession.RouteOrganizationContextsAsync(page);
        await DriverOrganizationRoles.RouteAsync(page);
        await page.RouteAsync(StopsRoute, route => route.FulfillAsync(new RouteFulfillOptions { Status = 401 }));

        // No session: "Sesión no disponible" offers the sign-in, back to the stop that was open.
        await page.GotoAsync(new Uri(server.BaseAddress, $"/driver/stops/{PickupOrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Sesión no disponible");
        var signIn = page.GetByRole(AriaRole.Link, new() { Name = "Iniciar sesión" });
        await signIn.WaitForAsync();
        Assert.Equal(
            $"/login?return_url=%2Fdriver%2Fstops%2F{PickupOrderId}",
            await signIn.GetAttributeAsync("href"));
        var box = await signIn.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Height >= 44);
        Assert.False(await HasHorizontalOverflowAsync(page));

        // Signing in needs the network: offline the action is disabled and says why.
        await context.SetOfflineAsync(true);
        try
        {
            var disabled = page.GetByRole(AriaRole.Button, new() { Name = "Iniciar sesión" });
            await disabled.WaitForAsync();
            Assert.True(await disabled.IsDisabledAsync());
            await ExpectTextAsync(page, "Conéctate a internet para iniciar sesión.");
        }
        finally
        {
            await context.SetOfflineAsync(false);
        }

        // /login keeps the same validated return URL for AuthCenter.
        await signIn.ClickAsync();
        await page.WaitForURLAsync("**/login?return_url=**");
        var authCenter = page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter" });
        await authCenter.WaitForAsync();
        Assert.Equal(
            $"/auth/login?return_url=%2Fdriver%2Fstops%2F{PickupOrderId}",
            await authCenter.GetAttributeAsync("href"));

        // A session the API no longer accepts (401): DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 asks to sign in again.
        signedIn = true;
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "Tu sesión venció, entra de nuevo");
        var again = page.GetByRole(AriaRole.Link, new() { Name = "Iniciar sesión" });
        await again.WaitForAsync();
        Assert.Equal("/login?return_url=%2Fdriver%2Fstops", await again.GetAttributeAsync("href"));
    }

    private static Task RouteStopsAsync(IPage page) =>
        page.RouteAsync(StopsRoute, route => route.FulfillAsync(
            DriverBffSession.Json(StopsBody(route.Request, Stops))));
}
