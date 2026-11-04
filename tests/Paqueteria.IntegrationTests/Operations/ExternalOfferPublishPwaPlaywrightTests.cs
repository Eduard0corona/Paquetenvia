using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// AI-08 EXT-001 "Ofertas a repartidores externos", the dispatcher's side: AI-07 <c>/ops/dashboard</c>
/// <c>publish_external_offer</c> ("Publicar oferta externa" on the order card) in a real browser against the production
/// Next build, and the dashboard's pause of the reload after it during a 429 (SEC-004-PANEL-SCREENS-2026-10-13). The
/// driver's side, AI-07 <c>external_driver_offers</c>, is <c>ExternalOffersDriverPwaPlaywrightTests</c>. The API is
/// fulfilled in the browser: the operations dashboard read model (OBS-001, ADR-OBS-001), getOperationsQueueCounts,
/// createExternalOffer and /me/organization-contexts in AI-05 shapes; the OperationsHub is unreachable. Only an order
/// that is READY_FOR_PICKUP or RESCHEDULED without an assignment offers the form; the commission typed in pesos is sent
/// as integer cents, the expiry typed in Mazatlán wall-clock time as its UTC instant whatever the device zone, and the
/// vehicle as the only eligible type; the confirmation names the order, the commission, the vehicle and the expiry; a
/// retry after an unanswered request resends the same Idempotency-Key and body, and a new offer gets a new key. Z25
/// ("Corregirlo"): the key is reused only for the identical offer, so a commission, expiry or vehicle changed after an
/// unanswered request goes with a new key. The fake server answers a known key with another body with AI-05's 409
/// IDEMPOTENCY_CONFLICT, as the real one does. Nothing is kept in browser storage or logged and no internal id is shown.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class ExternalOfferPublishPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationAName = "Logística Sintética";
    private const string ZoneCentro = "5a000000-0000-4000-8000-000000000001";
    private const string DriverMoto = "d1000000-0000-4000-8000-000000000001";
    private const string GeneratedAt = "2026-11-03T05:20:00Z";

    private static readonly CardOrder Ready = new("0e000000-0000-4000-8000-000000000001", "ORD_OfertaSintetica0000001", "READY_FOR_PICKUP", null);
    private static readonly CardOrder Rescheduled = new("0e000000-0000-4000-8000-000000000002", "ORD_OfertaSintetica0000002", "RESCHEDULED", null);
    private static readonly CardOrder Assigned = new("0e000000-0000-4000-8000-000000000003", "ORD_OfertaSintetica0000003", "ASSIGNED", "DRV-1a2b3c4d");

    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/operations/dashboard$"),
        new("^GET /api/v1/operations/queue-counts$"),
        new("^POST /api/v1/external-offers$"),
        new("^POST /hubs/operations/negotiate$"),
    ];

    /// <summary>
    /// A device in Tokyo with an English locale: only the orders without an assignment that can still be assigned offer
    /// the form; a commission that is not pesos is refused by the form before any confirmation; 45.5 pesos are 4550 cents
    /// and 23:30 on 2 November in Mazatlán is 06:30 UTC on 3 November; a 503 keeps the key, the retry of the same offer
    /// sends the same key and body, the published offer reads the dashboard again and the next offer gets a new key.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Publishing_sends_integer_cents_and_the_Mazatlan_expiry_and_retries_with_the_same_key()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Locale = "en-US",
            TimezoneId = "Asia/Tokyo",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page);
        api.OfferAnswers.Enqueue(503);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/dashboard").AbsoluteUri);
        var card = Card(page, Ready);
        await card.WaitForAsync();
        await Card(page, Assigned).WaitForAsync();
        // Only an order that can still be assigned, and has no assignment, offers the form.
        Assert.Equal(1, await card.Locator("form.opsExternalOffer").CountAsync());
        Assert.Equal(1, await Card(page, Rescheduled).Locator("form.opsExternalOffer").CountAsync());
        Assert.Equal(0, await Card(page, Assigned).Locator("form.opsExternalOffer").CountAsync());
        var form = card.Locator("form.opsExternalOffer");
        Assert.Equal(
            new[] { "Motocicleta", "Automóvil", "Van", "Bicicleta", "A pie" },
            (await form.GetByLabel("Vehículo").Locator("option").AllInnerTextsAsync()).Select(text => text.Trim()));
        await AssertNoIdentifiersAsync(page);

        // A decimal comma is not pesos: the form refuses it and nothing is confirmed or sent.
        await form.GetByLabel("Comisión (MXN)").FillAsync("45,5");
        await form.GetByLabel("Expiración (hora de Mazatlán)").FillAsync("2026-11-02T23:30");
        await form.GetByLabel("Vehículo").SelectOptionAsync(new SelectOptionValue { Label = "Automóvil" });
        await form.GetByRole(AriaRole.Button, new() { Name = "Publicar oferta", Exact = true }).ClickAsync();
        Assert.True(await form.GetByLabel("Comisión (MXN)").EvaluateAsync<bool>("input => !input.validity.valid"));
        Assert.Equal(0, await OpenDialog(page).CountAsync());
        Assert.Empty(api.Offers());

        // 45.5 pesos and 23:30 in Mazatlán: the confirmation names the order, the commission, the vehicle and the expiry.
        await form.GetByLabel("Comisión (MXN)").FillAsync("45.5");
        var readsBefore = api.DashboardReads().Length;
        await ConfirmAsync(page, form,
            $"Se ofrecerá la orden {Ready.PublicId} a repartidores externos con una comisión de $45.50 MXN (automóvil), " +
            "vigente hasta 2 nov 2026, 23:30 (hora de Mazatlán).");
        await ExpectTextAsync(form.Locator("span[role=status]"), "No fue posible publicar la oferta.");

        // The retry of the same offer resends the same key and body; once published, the dashboard is read again.
        await ConfirmAsync(page, form,
            $"Se ofrecerá la orden {Ready.PublicId} a repartidores externos con una comisión de $45.50 MXN (automóvil), " +
            "vigente hasta 2 nov 2026, 23:30 (hora de Mazatlán).");
        await ExpectTextAsync(form.Locator("span[role=status]"), "Oferta externa publicada.");
        await WaitForAsync(() => api.DashboardReads().Length > readsBefore, "the dashboard was not read again after the offer");
        var offers = api.Offers();
        Assert.Equal(2, offers.Length);
        var body = JsonSerializer.Serialize(new
        {
            order_id = Ready.Id,
            commission_cents = 4_550,
            expires_at = "2026-11-03T06:30:00.000Z",
            eligible_constraints = new { vehicle_types = new[] { "CAR" } },
        });
        Assert.All(offers, offer =>
        {
            Assert.Equal(body, offer.Body);
            Assert.Equal(string.Empty, offer.Query);
            Assert.Matches(UuidKeyPattern(), offer.IdempotencyKey ?? string.Empty);
        });
        Assert.Equal(offers[0].IdempotencyKey, offers[1].IdempotencyKey);
        // The unanswered offer read nothing: the first read after the confirmation came after the published one.
        Assert.Equal(readsBefore, api.DashboardReads().Count(read => read.At < offers[1].At));

        // A new offer for the rescheduled order: 19.99 pesos (1998.9999999999998 in binary floating point) on foot.
        var other = Card(page, Rescheduled).Locator("form.opsExternalOffer");
        await other.GetByLabel("Comisión (MXN)").FillAsync("19.99");
        await other.GetByLabel("Expiración (hora de Mazatlán)").FillAsync("2026-11-03T08:05");
        await other.GetByLabel("Vehículo").SelectOptionAsync(new SelectOptionValue { Label = "A pie" });
        await ConfirmAsync(page, other,
            $"Se ofrecerá la orden {Rescheduled.PublicId} a repartidores externos con una comisión de $19.99 MXN (a pie), " +
            "vigente hasta 3 nov 2026, 08:05 (hora de Mazatlán).");
        await ExpectTextAsync(other.Locator("span[role=status]"), "Oferta externa publicada.");
        var third = api.Offers()[2];
        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                order_id = Rescheduled.Id,
                commission_cents = 1_999,
                expires_at = "2026-11-03T15:05:00.000Z",
                eligible_constraints = new { vehicle_types = new[] { "WALKER" } },
            }),
            third.Body);
        Assert.Matches(UuidKeyPattern(), third.IdempotencyKey ?? string.Empty);
        Assert.NotEqual(offers[1].IdempotencyKey, third.IdempotencyKey);
        AssertDashboardQueriesAreEmpty(api);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// Z25 ("Corregirlo"): after unanswered publishes, each changed offer (commission, then expiry, then vehicle) goes
    /// with a new key and its own body, never a key already sent with another body, which the server refuses as a
    /// reuse. The identical retry of an offer resends its key and body: the last one, and the first one when dispatch
    /// goes back to it, which is published. Once the server confirmed it, the same offer again is a new offer with a
    /// new key.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task A_changed_offer_after_an_unanswered_publish_goes_with_a_new_key()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Locale = "en-US",
            TimezoneId = "Asia/Tokyo",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page);
        foreach (var _ in Enumerable.Range(0, 5)) api.OfferAnswers.Enqueue(503);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/dashboard").AbsoluteUri);
        var form = Card(page, Ready).Locator("form.opsExternalOffer");
        await form.WaitForAsync();
        var status = form.Locator("span[role=status]");

        // 45.5 pesos by car until 23:30 in Mazatlán: unanswered.
        await form.GetByLabel("Comisión (MXN)").FillAsync("45.5");
        await form.GetByLabel("Expiración (hora de Mazatlán)").FillAsync("2026-11-02T23:30");
        await form.GetByLabel("Vehículo").SelectOptionAsync(new SelectOptionValue { Label = "Automóvil" });
        await ConfirmAsync(page, form, Description("$45.50 MXN (automóvil)", "2 nov 2026, 23:30"));
        await ExpectTextAsync(status, "No fue posible publicar la oferta.");
        await WaitForAsync(() => api.Offers().Length == 1, "the first offer was not sent");

        // Another commission: unanswered too.
        await form.GetByLabel("Comisión (MXN)").FillAsync("50");
        await ConfirmAsync(page, form, Description("$50.00 MXN (automóvil)", "2 nov 2026, 23:30"));
        await WaitForAsync(() => api.Offers().Length == 2, "the offer with another commission was not sent");
        await ExpectTextAsync(status, "No fue posible publicar la oferta.");

        // Another expiry: unanswered too.
        await form.GetByLabel("Expiración (hora de Mazatlán)").FillAsync("2026-11-02T23:45");
        await ConfirmAsync(page, form, Description("$50.00 MXN (automóvil)", "2 nov 2026, 23:45"));
        await WaitForAsync(() => api.Offers().Length == 3, "the offer with another expiry was not sent");
        await ExpectTextAsync(status, "No fue posible publicar la oferta.");

        // Another vehicle: unanswered too.
        await form.GetByLabel("Vehículo").SelectOptionAsync(new SelectOptionValue { Label = "Van" });
        await ConfirmAsync(page, form, Description("$50.00 MXN (van)", "2 nov 2026, 23:45"));
        await WaitForAsync(() => api.Offers().Length == 4, "the offer with another vehicle was not sent");
        await ExpectTextAsync(status, "No fue posible publicar la oferta.");

        // The identical retry of the last offer: its key and body again, unanswered too.
        await ConfirmAsync(page, form, Description("$50.00 MXN (van)", "2 nov 2026, 23:45"));
        await WaitForAsync(() => api.Offers().Length == 5, "the identical retry was not sent");
        await ExpectTextAsync(status, "No fue posible publicar la oferta.");

        // Back to the first offer: its own key and body again, and it is published.
        await form.GetByLabel("Comisión (MXN)").FillAsync("45.5");
        await form.GetByLabel("Expiración (hora de Mazatlán)").FillAsync("2026-11-02T23:30");
        await form.GetByLabel("Vehículo").SelectOptionAsync(new SelectOptionValue { Label = "Automóvil" });
        var readsBefore = api.DashboardReads().Length;
        await ConfirmAsync(page, form, Description("$45.50 MXN (automóvil)", "2 nov 2026, 23:30"));
        await ExpectTextAsync(status, "Oferta externa publicada.");
        await WaitForAsync(() => api.DashboardReads().Length > readsBefore, "the dashboard was not read again after the offer");

        // The same offer once more after the confirmation: a new offer, with a new key.
        await ConfirmAsync(page, form, Description("$45.50 MXN (automóvil)", "2 nov 2026, 23:30"));
        await WaitForAsync(() => api.Offers().Length == 7, "the next offer was not sent");
        await ExpectTextAsync(status, "Oferta externa publicada.");

        var offers = api.Offers();
        Assert.Equal(
            new[]
            {
                OfferBody(4_550, "2026-11-03T06:30:00.000Z", "CAR"),
                OfferBody(5_000, "2026-11-03T06:30:00.000Z", "CAR"),
                OfferBody(5_000, "2026-11-03T06:45:00.000Z", "CAR"),
                OfferBody(5_000, "2026-11-03T06:45:00.000Z", "VAN"),
                OfferBody(5_000, "2026-11-03T06:45:00.000Z", "VAN"),
                OfferBody(4_550, "2026-11-03T06:30:00.000Z", "CAR"),
                OfferBody(4_550, "2026-11-03T06:30:00.000Z", "CAR"),
            },
            offers.Select(offer => offer.Body));
        var keys = offers.Select(offer => offer.IdempotencyKey ?? string.Empty).ToArray();
        foreach (var key in keys) Assert.Matches(UuidKeyPattern(), key);
        // One key per offer: the four different offers and the one after the confirmation each have their own, and
        // only an identical retry repeats one.
        Assert.Equal(5, keys.Distinct().Count());
        Assert.Equal(keys[3], keys[4]);
        Assert.Equal(keys[0], keys[5]);
        Assert.DoesNotContain(keys[6], keys[..6]);
        Assert.Equal(new[] { 503, 503, 503, 503, 503, 201, 201 }, api.OfferStatuses());
        Assert.Equal(string.Empty, string.Concat(offers.Select(offer => offer.Query)));
        AssertDashboardQueriesAreEmpty(api);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 /ops/dashboard rate_limit: during the dashboard's 429 wait the offer, which is not limited, is published,
    /// but the reload after it waits for 0 and happens once, by itself, list and counts; the countdown is announced once
    /// with the seconds asked for and "Actualizar" is disabled meanwhile.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task The_reload_after_a_published_offer_waits_for_the_dashboard()
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
        var api = await InstallAsync(page);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/dashboard").AbsoluteUri);
        var form = Card(page, Ready).Locator("form.opsExternalOffer");
        await form.WaitForAsync();
        await form.GetByLabel("Comisión (MXN)").FillAsync("60");
        await form.GetByLabel("Expiración (hora de Mazatlán)").FillAsync("2026-11-02T23:45");

        // "Actualizar" meets a 429 on the dashboard: the countdown, one announcement, "Actualizar" disabled.
        var refresh = page.Locator("main header").GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });
        api.DashboardTooManyRequests = 1;
        await refresh.ClickAsync();
        await page.Locator("main").GetByRole(AriaRole.Timer).GetByText(TooManyRequestsPattern()).WaitForAsync();
        Assert.Equal("Demasiadas solicitudes. Espera 6 segundos e intenta de nuevo.",
            (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.True(await refresh.IsDisabledAsync());
        var limitedAt = api.DashboardReads().Last().At;

        // The offer is published during the wait.
        await ConfirmAsync(page, form,
            $"Se ofrecerá la orden {Ready.PublicId} a repartidores externos con una comisión de $60.00 MXN (motocicleta), " +
            "vigente hasta 2 nov 2026, 23:45 (hora de Mazatlán).");
        await ExpectTextAsync(form.Locator("span[role=status]"), "Oferta externa publicada.");
        var offer = Assert.Single(api.Offers());
        Assert.True(offer.At < limitedAt.AddSeconds(5), "The offer was not published during the wait, so the pause was not exercised.");
        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                order_id = Ready.Id,
                commission_cents = 6_000,
                expires_at = "2026-11-03T06:45:00.000Z",
                eligible_constraints = new { vehicle_types = new[] { "MOTORCYCLE" } },
            }),
            offer.Body);

        // At 0 the dashboard and its counts are read once, by itself.
        await WaitForAsync(async () => await page.Locator("main").GetByRole(AriaRole.Timer).CountAsync() == 0, "the countdown stayed after 0");
        await WaitForAsync(() => api.DashboardReads().Any(read => read.At > limitedAt), "the dashboard was not read after the wait");
        await WaitForAsync(async () => await refresh.IsEnabledAsync(), "Actualizar stayed disabled after the wait");
        await Task.Delay(500);
        var lists = api.DashboardReads().Where(read => read.At > limitedAt).ToArray();
        var counts = api.CountReads().Where(read => read.At > offer.At).ToArray();
        Assert.Single(lists);
        Assert.Single(counts);
        Assert.True(lists[0].At >= limitedAt.AddSeconds(5.5), "The dashboard was read during the wait.");
        Assert.True(counts[0].At >= limitedAt.AddSeconds(5.5), "The counts were read during the wait.");
        Assert.Equal(string.Empty, (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        AssertDashboardQueriesAreEmpty(api);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    private static string Description(string commission, string expiry) =>
        $"Se ofrecerá la orden {Ready.PublicId} a repartidores externos con una comisión de {commission}, " +
        $"vigente hasta {expiry} (hora de Mazatlán).";

    private static string OfferBody(long commissionCents, string expiresAt, string vehicle) =>
        JsonSerializer.Serialize(new
        {
            order_id = Ready.Id,
            commission_cents = commissionCents,
            expires_at = expiresAt,
            eligible_constraints = new { vehicle_types = new[] { vehicle } },
        });

    private static ILocator Card(IPage page, CardOrder order) =>
        page.Locator("article.opsOrderCard", new() { Has = page.Locator($"h3:text-is('{order.PublicId}')") });

    private static ILocator OpenDialog(IPage page) => page.Locator("dialog.confirmDialog[open]");

    /// <summary>"Publicar oferta", then the confirmation: its title, its text, "Cancelar" first and focused.</summary>
    private static async Task ConfirmAsync(IPage page, ILocator form, string description)
    {
        await form.GetByRole(AriaRole.Button, new() { Name = "Publicar oferta", Exact = true }).ClickAsync();
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal("¿Publicar oferta externa?", (await dialog.Locator("h2").InnerTextAsync()).Trim());
        Assert.Equal(description, (await dialog.Locator("p").InnerTextAsync()).Trim());
        Assert.Equal(new[] { "Cancelar", "Publicar oferta" }, (await dialog.Locator("button").AllInnerTextsAsync()).Select(text => text.Trim()));
        Assert.Equal("Cancelar", await page.EvaluateAsync<string>("() => document.activeElement?.textContent ?? ''"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Publicar oferta", Exact = true }).ClickAsync();
    }

    private static async Task ExpectTextAsync(ILocator locator, string expected)
    {
        var actual = string.Empty;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = (await locator.InnerTextAsync()).Trim();
            if (actual == expected) return;
            await Task.Delay(50);
        }

        Assert.Fail($"Expected «{expected}», found «{actual}».");
    }

    /// <summary>The dashboard is read without filters on this screen.</summary>
    private static void AssertDashboardQueriesAreEmpty(PublishApi api)
    {
        var reads = api.DashboardReads();
        Assert.NotEmpty(reads);
        Assert.All(reads, read => Assert.Equal(string.Empty, read.Query));
    }

    /// <summary>No UUID nor the first 8 characters of a synthetic id is visible anywhere on the page.</summary>
    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        foreach (var id in new[] { OrganizationA, ZoneCentro, DriverMoto, Ready.Id, "0f000000-0000-4000-8000-000000000001" })
        {
            Assert.DoesNotContain(id[..8], visible, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Nothing of the screen reached browser storage: no Web Storage, cookie or IndexedDB database, and the service
    /// worker's Cache Storage holds only the static assets it caches by design (public/sw.js).
    /// </summary>
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

    private static void AssertOnlyAllowedCalls(CallRecorder calls, Uri web)
    {
        Assert.NotEmpty(calls.Api);
        Assert.All(calls.Api, call => Assert.Contains(AllowedCalls, pattern => pattern.IsMatch($"{call.Method} {call.Path}")));
        var webOrigin = web.GetLeftPart(UriPartial.Authority) + "/";
        Assert.DoesNotContain(calls.Foreign, url => !url.StartsWith(webOrigin, StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure) =>
        await WaitForAsync(() => Task.FromResult(condition()), failure);

    private static async Task WaitForAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
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
                calls.Api.Enqueue((request.Method, new Uri(request.Url).AbsolutePath));
                return;
            }

            calls.Foreign.Enqueue(request.Url);
        };
        return calls;
    }

    private static async Task<PublishApi> InstallAsync(IPage page)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(OrganizationA)}},
              sessionNamespace: "ext-001-external-offer-publish-playwright",
              getAccessToken: () => "synthetic-operations-token",
            };
            """);
        var api = new PublishApi();
        await page.RouteAsync("**/api/v1/**", api.HandleAsync);
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        return api;
    }

    private static Task FulfillAsync(IRoute route, object body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions { Status = status, ContentType = "application/json", Body = JsonSerializer.Serialize(body) });

    private static Task ProblemAsync(IRoute route, int status, string? code = null) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/problem+json",
            Body = code is null
                ? JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status })
                : JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status, code }),
        });

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();

    /// <summary>The order card's key: a random UUID per offer (operations-order-card.tsx), at least 16 characters (AI-05).</summary>
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidKeyPattern();

    [GeneratedRegex("^Demasiadas solicitudes\\. Espera [1-6] segundos? e intenta de nuevo\\.$")]
    private static partial Regex TooManyRequestsPattern();

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    private sealed record PublishCall(DateTimeOffset At, string Method, string Path, string Query, string? IdempotencyKey, string? Body);

    /// <summary>An order of the dashboard read model; an assigned one names its driver by reference.</summary>
    private sealed record CardOrder(string Id, string PublicId, string Status, string? DriverReference)
    {
        internal object Row() => new
        {
            order_id = Id,
            aggregate_version = 3,
            public_id = PublicId,
            owner = new { organization_id = OrganizationA, display_name = OrganizationAName },
            @operator = (object?)null,
            client = (object?)null,
            status = Status,
            created_at = "2026-11-02T16:00:00Z",
            updated_at = "2026-11-03T05:00:00Z",
            service_type = "SAME_DAY",
            pickup_window = (object?)null,
            delivery_window = (object?)null,
            delivery_zone = new { operating_zone_id = ZoneCentro, name = "Centro", zone_type = "CORE" },
            assignment = DriverReference is null
                ? null
                : new { assignment_id = "a6000000-0000-4000-8000-000000000001", assignment_type = "OWN", status = "ACCEPTED", driver_id = DriverMoto, driver_reference = DriverReference },
            latest_driver_location = (object?)null,
            total = new { currency = "MXN", amount_cents = 25_000L },
            cost_warning = (string?)null,
            unassigned_alert = DriverReference is null && Status is "READY_FOR_PICKUP" or "RESCHEDULED",
        };
    }

    /// <summary>The dashboard read model and its counts for one DISPATCHER organization, and createExternalOffer.</summary>
    private sealed class PublishApi
    {
        private static readonly string[] OrderStatuses =
        [
            "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING",
            "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED", "DELIVERED", "CLOSED", "CLAIM_OPEN",
            "CLAIM_RESOLVED", "CANCELLED",
        ];

        private static readonly CardOrder[] Orders = [Ready, Rescheduled, Assigned];

        private readonly object _gate = new();
        private readonly List<PublishCall> _calls = [];
        private readonly List<int> _offerStatuses = [];
        private readonly Dictionary<string, string?> _offerBodiesByKey = [];
        private int _dashboardTooManyRequests;

        /// <summary>The statuses of the next createExternalOffer answers; 201 once they run out.</summary>
        internal ConcurrentQueue<int> OfferAnswers { get; } = new();

        internal int DashboardTooManyRequests
        {
            set => Volatile.Write(ref _dashboardTooManyRequests, value);
        }

        internal PublishCall[] Offers() => Calls("POST", "/api/v1/external-offers");

        /// <summary>The status each createExternalOffer was answered with, in order.</summary>
        internal int[] OfferStatuses()
        {
            lock (_gate) return [.. _offerStatuses];
        }

        internal PublishCall[] DashboardReads() => Calls("GET", "/api/v1/operations/dashboard");

        internal PublishCall[] CountReads() => Calls("GET", "/api/v1/operations/queue-counts");

        private PublishCall[] Calls(string method, string path)
        {
            lock (_gate) return [.. _calls.Where(call => call.Method == method && call.Path == path)];
        }

        internal async Task HandleAsync(IRoute route)
        {
            var at = DateTimeOffset.UtcNow;
            var request = route.Request;
            var uri = new Uri(request.Url);
            var headers = await request.AllHeadersAsync();
            var call = new PublishCall(at, request.Method, uri.AbsolutePath, uri.Query.TrimStart('?'),
                headers.GetValueOrDefault("idempotency-key"), request.PostData);
            lock (_gate) _calls.Add(call);
            if (headers.GetValueOrDefault("x-organization-id") != OrganizationA && call.Path != "/api/v1/me/organization-contexts")
            {
                await ProblemAsync(route, 403);
                return;
            }

            switch (call.Method, call.Path)
            {
                case ("GET", "/api/v1/me/organization-contexts"):
                    await FulfillAsync(route, new[]
                    {
                        new { organization_id = OrganizationA, display_name = OrganizationAName, role = "DISPATCHER", is_default = true },
                    });
                    return;
                case ("GET", "/api/v1/operations/dashboard"):
                    if (Interlocked.Decrement(ref _dashboardTooManyRequests) >= 0)
                    {
                        await TooManyRequestsAsync(route);
                        return;
                    }

                    Interlocked.Exchange(ref _dashboardTooManyRequests, 0);
                    await FulfillAsync(route, new { generated_at = GeneratedAt, items = Orders.Select(order => order.Row()).ToArray(), next_cursor = (string?)null });
                    return;
                case ("GET", "/api/v1/operations/queue-counts"):
                    var byStatus = OrderStatuses.ToDictionary(status => status, status => Orders.Count(order => order.Status == status));
                    await FulfillAsync(route, new
                    {
                        generated_at = GeneratedAt,
                        total = Orders.Length,
                        by_status = byStatus,
                        queues = new { unassigned = 2, needs_attention = 1, price_review = 0, delivered_not_closed = 0, en_route = 0 },
                    });
                    return;
                case ("POST", "/api/v1/external-offers"):
                    // Every key is kept with its body, as if an unanswered request had reached the server: the same key
                    // with another body is AI-05's 409 IDEMPOTENCY_CONFLICT.
                    bool reused;
                    lock (_gate)
                    {
                        var key = call.IdempotencyKey ?? string.Empty;
                        reused = _offerBodiesByKey.TryGetValue(key, out var known) && known != call.Body;
                        if (!reused) _offerBodiesByKey[key] = call.Body;
                    }

                    if (reused)
                    {
                        lock (_gate) _offerStatuses.Add(409);
                        await ProblemAsync(route, 409, "IDEMPOTENCY_CONFLICT");
                        return;
                    }

                    if (OfferAnswers.TryDequeue(out var status) && status != 201)
                    {
                        lock (_gate) _offerStatuses.Add(status);
                        await ProblemAsync(route, status);
                        return;
                    }

                    lock (_gate) _offerStatuses.Add(201);

                    using (var body = JsonDocument.Parse(call.Body ?? "{}"))
                    {
                        await FulfillAsync(
                            route,
                            new
                            {
                                id = "0f000000-0000-4000-8000-000000000001",
                                order_id = body.RootElement.GetProperty("order_id").GetString(),
                                status = "OPEN",
                                commission = new { currency = "MXN", amount_cents = body.RootElement.GetProperty("commission_cents").GetInt64() },
                                expires_at = body.RootElement.GetProperty("expires_at").GetString(),
                                accepted_by_driver_id = (string?)null,
                                accepted_at = (string?)null,
                                version = 1,
                            },
                            201);
                    }

                    return;
                default:
                    await ProblemAsync(route, 404);
                    return;
            }
        }

        private static Task TooManyRequestsAsync(IRoute route) =>
            route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 429,
                ContentType = "application/problem+json",
                // The test API is another origin, so it exposes Retry-After as a cross-origin API would have to.
                Headers = new Dictionary<string, string>
                {
                    ["Retry-After"] = "6",
                    ["Cache-Control"] = "no-store",
                    ["Access-Control-Expose-Headers"] = "Retry-After",
                },
                Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Too Many Requests", status = 429, traceId = new string('0', 32) }),
            });
    }
}
