using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// AI-08 EXT-001 "driver offer UI" and its required tests "eligible offers REST recovery e2e" and "local dismiss does
/// not mutate server state": AI-07 <c>external_driver_offers</c> (hosted on <c>/driver/stops</c>) in a real browser
/// against the production Next build, on a phone in Tokyo. The API is fulfilled in the browser in AI-05 shapes:
/// listMyEligibleExternalOffers (GET /driver/me/external-offers, no driver_id), acceptExternalOffer, listMyStops,
/// /me/organization-roles and the phone read; the DriverHub is a mocked SignalR JSON hub over long polling where a
/// test needs it (a routed WebSocket cannot be dropped from the test with Microsoft.Playwright 1.61 for .NET). Each
/// offer shows its order, its commission from integer cents and its expiry in Mazatlán before "Aceptar oferta"; the
/// accept is the normative POST with one Idempotency-Key per offer, reused for a retry after a failure; a 409 says the
/// offer is gone and reads the list again; "No me interesa" sends nothing and the offer comes back after a reload; an
/// ExternalOfferChanged notice only triggers a REST read (its payload is never shown) and a reconnect reads the list
/// again; a driver without the external-offer capability (403) sees no panel and reads nothing more. No offer reaches
/// browser storage and nothing is logged. The dispatcher's "Publicar oferta externa" is
/// <c>ExternalOfferPublishPwaPlaywrightTests</c>.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class ExternalOffersDriverPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationId = "11111111-1111-1111-1111-111111111111";
    private const string DriverId = "d1000000-0000-4000-8000-000000000009";
    private const string OffersRoute = "**/api/v1/driver/me/external-offers**";

    private static readonly Offer First = new("0f000000-0000-4000-8000-000000000001", "0e000000-0000-4000-8000-0000000000a1", 4_550, "2026-11-03T06:30:00Z");
    private static readonly Offer Second = new("0f000000-0000-4000-8000-000000000002", "0e000000-0000-4000-8000-0000000000b2", 2_500, "2026-11-03T15:05:00Z");

    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/driver/me/stops$"),
        new("^GET /api/v1/me/organization-roles$"),
        new("^GET /api/v1/driver/me/phone$"),
        new("^GET /api/v1/driver/me/dispatch-contact$"),
        new("^GET /api/v1/driver/me/external-offers$"),
        new("^POST /api/v1/external-offers/[0-9a-f-]{36}/accept$"),
        new("^POST /hubs/driver/negotiate$"),
        new("^(GET|POST|DELETE) /hubs/driver$"),
    ];

    /// <summary>
    /// AI-07 must_show_before_accept and accept: each card shows the order, the commission and the expiry in Mazatlán
    /// (2 November there, already 3 November on the phone). A 503 keeps the offer and its key; the retry with the same
    /// key is accepted and the list is read again; a 409 on the other offer says it is gone and reads the list again.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Driver_sees_the_order_commission_and_expiry_before_accepting_and_retries_with_the_same_key()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Lists.Enqueue([First, Second]);
        phone.Lists.Enqueue([Second]);
        phone.Lists.Enqueue([]);
        phone.AcceptAnswers.Enqueue(503);
        phone.AcceptAnswers.Enqueue(200);
        phone.AcceptAnswers.Enqueue(409);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        var panel = Panel(page);
        await ExpectCardsAsync(page, First, Second);
        Assert.Equal(1, phone.ListReads);
        Assert.Equal("Oferta para orden", (await Card(page, First).Locator("p").First.TextContentAsync())?.Trim());
        Assert.Equal(
            new[] { "Aceptar oferta", "No me interesa" },
            (await Card(page, First).GetByRole(AriaRole.Button).AllInnerTextsAsync()).Select(text => text.Trim()));
        Assert.False(await DriverStopsPwaPlaywrightTests.HasHorizontalOverflowAsync(page));
        await phone.AssertOnlyOrderIdsShownAsync(First, Second);

        // A 503: the offer stays with a message; the retry sends the same key and the accepted offer leaves the list.
        await Card(page, First).GetByRole(AriaRole.Button, new() { Name = "Aceptar oferta", Exact = true }).ClickAsync();
        await ExpectMessageAsync(panel, "No pudimos aceptar la oferta. Intenta nuevamente.");
        await ExpectCardsAsync(page, First, Second);
        await Card(page, First).GetByRole(AriaRole.Button, new() { Name = "Aceptar oferta", Exact = true }).ClickAsync();
        await ExpectMessageAsync(panel, "Oferta aceptada. La asignación externa quedó confirmada.");
        await ExpectCardsAsync(page, Second);
        Assert.Equal(2, phone.ListReads);

        // A 409: another driver took it; the list is read again and the panel keeps only the message.
        await Card(page, Second).GetByRole(AriaRole.Button, new() { Name = "Aceptar oferta", Exact = true }).ClickAsync();
        await ExpectMessageAsync(panel, "La oferta ya no está disponible o fue aceptada por otro repartidor.");
        await ExpectCardsAsync(page);
        Assert.Equal(3, phone.ListReads);

        var accepts = phone.Accepts();
        Assert.Equal(
            new[] { $"/api/v1/external-offers/{First.Id}/accept", $"/api/v1/external-offers/{First.Id}/accept", $"/api/v1/external-offers/{Second.Id}/accept" },
            accepts.Select(accept => accept.Path));
        Assert.All(accepts, accept =>
        {
            Assert.Null(accept.Body);
            Assert.Equal(string.Empty, accept.Query);
            Assert.Matches(KeyPattern(), accept.IdempotencyKey ?? string.Empty);
        });
        Assert.Equal(accepts[0].IdempotencyKey, accepts[1].IdempotencyKey);
        Assert.NotEqual(accepts[1].IdempotencyKey, accepts[2].IdempotencyKey);

        await phone.FinishAsync(First, Second);
    }

    /// <summary>
    /// AI-07 dismiss: "No me interesa" hides the offer on this phone only, sends no request and changes nothing on the
    /// server; after a reload the list is read again and the offer is back.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task No_me_interesa_sends_nothing_and_the_offer_comes_back_after_a_reload()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Lists.Enqueue([First, Second]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectCardsAsync(page, First, Second);
        var before = phone.AllCalls.Count;
        await Card(page, First).GetByRole(AriaRole.Button, new() { Name = "No me interesa", Exact = true }).ClickAsync();
        await ExpectCardsAsync(page, Second);
        await Card(page, Second).GetByRole(AriaRole.Button, new() { Name = "No me interesa", Exact = true }).ClickAsync();
        // With nothing left and no message, the panel goes.
        await WaitForAsync(async () => await Panel(page).CountAsync() == 0, "the offers panel stayed with no offer left");
        await Task.Delay(1_000);
        // Nothing about the offers was sent: no read, no accept, no other write (the hub stays unreachable here).
        Assert.Equal(1, phone.ListReads);
        Assert.DoesNotContain(phone.AllCalls.Skip(before), call =>
            call.Path.Contains("external-offers", StringComparison.Ordinal) || (call.Method != "GET" && !call.Path.StartsWith("/hubs/", StringComparison.Ordinal)));
        Assert.Empty(phone.Accepts());

        // The dismissal was not kept anywhere: a reload reads the list and both offers are back.
        await page.ReloadAsync();
        await ExpectCardsAsync(page, First, Second);
        Assert.Equal(2, phone.ListReads);
        Assert.Empty(phone.Accepts());

        await phone.FinishAsync(First, Second);
    }

    /// <summary>
    /// AI-07 recovery: an ExternalOfferChanged notice only triggers a REST read (gathered: two notices, one read) and the
    /// card shows what REST answered, never the notice's payload; when the DriverHub connection drops, the reconnect reads
    /// the list (and the stops) again from REST.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_notice_and_a_reconnect_only_read_the_offers_again_from_REST()
    {
        await using var phone = await StartAsync(hub: true);
        var page = phone.Page;
        phone.Lists.Enqueue([First]);
        phone.Lists.Enqueue([First, Second]);
        phone.Lists.Enqueue([Second]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectCardsAsync(page, First);
        await WaitForAsync(() => phone.HubReady, "the DriverHub never connected");
        Assert.Equal(1, phone.ListReads);

        // Two notices within the gathering window: one REST read, whose commission ($25.00) is shown, not the notice's.
        phone.SendOfferChanged(Second, version: 1, commissionCents: 999);
        phone.SendOfferChanged(Second, version: 2, commissionCents: 999);
        await ExpectCardsAsync(page, First, Second);
        await Task.Delay(1_000);
        Assert.Equal(2, phone.ListReads);
        Assert.DoesNotContain("$9.99", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);

        // The connection drops: the reconnect negotiates again and reads the stops and the offers from REST.
        var stopsBefore = phone.StopsReads;
        phone.DropHub();
        await ExpectCardsAsync(page, Second);
        await WaitForAsync(() => phone.Negotiations == 2, "the DriverHub did not negotiate again");
        Assert.Equal(3, phone.ListReads);
        Assert.True(phone.StopsReads > stopsBefore, "The reconnect did not read the stops from REST.");
        Assert.All(phone.ListCalls(), call => Assert.Equal(string.Empty, call.Query));

        await phone.FinishAsync(First, Second);
    }

    /// <summary>
    /// A driver whose role holds no external-offer capability (an OWN driver: the read answers 403) sees no panel and
    /// no message, and a later notice reads nothing.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_driver_without_the_capability_sees_no_panel_and_a_notice_reads_nothing()
    {
        await using var phone = await StartAsync(hub: true);
        var page = phone.Page;
        phone.ListFailures.Enqueue(403);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await page.GetByText("No tienes paradas activas.", new() { Exact = true }).WaitForAsync();
        await WaitForAsync(() => phone.ListReads == 1, "the offers were never read");
        await WaitForAsync(() => phone.HubReady, "the DriverHub never connected");
        await WaitForAsync(async () => await page.GetByText("Consultando ofertas externas...").CountAsync() == 0, "the offers stayed loading");
        phone.SendOfferChanged(First, version: 1, commissionCents: 4_550);
        await Task.Delay(1_000);
        Assert.Equal(1, phone.ListReads);
        Assert.Equal(0, await Panel(page).CountAsync());
        Assert.DoesNotContain("No pudimos actualizar las ofertas.", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);

        await phone.FinishAsync();
    }

    // ---------- helpers ----------

    private static ILocator Panel(IPage page) => page.Locator("section[aria-labelledby=external-offers-heading]");

    private static ILocator Card(IPage page, Offer offer) =>
        Panel(page).Locator("li", new() { Has = page.Locator($"h3:text-is('{offer.OrderId}')") });

    private static async Task ExpectMessageAsync(ILocator panel, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var actual = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await panel.CountAsync() == 1)
            {
                actual = (await panel.Locator("p[aria-live=polite]").InnerTextAsync()).Trim();
                if (actual == message) return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Expected «{message}», found «{actual}».");
    }

    /// <summary>
    /// The cards in order: the order, "Externa", the commission from integer cents, the expiry as Mazatlán formats it (a
    /// Mazatlán date, the phone being in Tokyo) and "Disponible".
    /// </summary>
    private static async Task ExpectCardsAsync(IPage page, params Offer[] offers)
    {
        var expected = new List<string>();
        foreach (var offer in offers)
        {
            var expiry = await page.EvaluateAsync<string>(
                """
                value => new Intl.DateTimeFormat("es-MX", {
                  dateStyle: "medium",
                  timeStyle: "short",
                  timeZone: "America/Mazatlan",
                }).format(new Date(value))
                """,
                offer.ExpiresAt);
            Assert.StartsWith(offer.MazatlanDate, expiry, StringComparison.Ordinal);
            expected.Add(Regex.Replace(
                $"Oferta para orden {offer.OrderId} Externa Comisión {offer.Commission} Expira {expiry} Estado Disponible",
                "\\s+", " "));
        }

        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                actual = await page.Locator("section[aria-labelledby=external-offers-heading] li").EvaluateAllAsync<string[]>(
                    """
                    items => items.map(item => [...item.querySelectorAll("p, h3, span, dt, dd")]
                      .map(node => node.textContent.trim()).join(" ").replace(/\s+/g, " "))
                    """);
                if (actual.SequenceEqual(expected, StringComparer.Ordinal)) return;
            }
            catch (PlaywrightException)
            {
                // The list was replaced while it was read; read it again.
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The offers did not match: {string.Join(" || ", actual)}");
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

    private async Task<PhoneSession> StartAsync(bool hub = false)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.DriverStopsJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var session = new PhoneSession(playwright, browser, context, trace, page, server.BaseAddress, hub);
        await session.InstallAsync();
        return session;
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();

    /// <summary>The accept key: a random UUID per offer (external-offers-controller.ts), at least 16 characters (AI-05).</summary>
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex KeyPattern();

    /// <summary>An AI-05 ExternalOffer the driver may accept: OPEN, its commission in integer cents.</summary>
    private sealed record Offer(string Id, string OrderId, long CommissionCents, string ExpiresAt)
    {
        internal string Commission => $"${CommissionCents / 100}.{CommissionCents % 100:00}";

        /// <summary>The expiry's calendar date in Mazatlán (UTC-7), the day before Tokyo's for these instants.</summary>
        internal string MazatlanDate =>
            $"{DateTimeOffset.Parse(ExpiresAt, System.Globalization.CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(-7)).Day} nov 2026";

        internal object Json() => new
        {
            id = Id,
            order_id = OrderId,
            status = "OPEN",
            commission = new { currency = "MXN", amount_cents = CommissionCents },
            expires_at = ExpiresAt,
            accepted_by_driver_id = (string?)null,
            accepted_at = (string?)null,
            version = 1,
        };
    }

    private sealed record DriverCall(string Method, string Path, string Query, string? IdempotencyKey, string? Body, string Organization);

    /// <summary>
    /// The phone and the fulfilled API. Playwright runs the last matching route first: the block of every other call to
    /// the test API is registered before the routes of the screen.
    /// </summary>
    private sealed class PhoneSession(
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        PlaywrightFailureTrace trace,
        IPage page,
        Uri web,
        bool hub) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<DriverCall> _calls = [];
        private readonly List<string> _hubOrganizations = [];
        private Channel<string?> _polls = Channel.CreateUnbounded<string?>();
        private string _connectionToken = string.Empty;
        private readonly ConcurrentQueue<string> _console = new();
        private readonly ConcurrentQueue<string> _foreign = new();
        private Offer[] _list = [];
        private int _negotiations;
        private int _stopsReads;
        private bool _hubReady;
        private bool _firstPollAnswered;

        internal IPage Page => page;

        /// <summary>The next listMyEligibleExternalOffers answers; the last one is repeated.</summary>
        internal ConcurrentQueue<Offer[]> Lists { get; } = new();

        internal ConcurrentQueue<int> ListFailures { get; } = new();

        /// <summary>The statuses of the next acceptExternalOffer answers; 200 once they run out.</summary>
        internal ConcurrentQueue<int> AcceptAnswers { get; } = new();

        internal IReadOnlyList<DriverCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal int ListReads => ListCalls().Length;

        internal int StopsReads => Volatile.Read(ref _stopsReads);

        internal int Negotiations => Volatile.Read(ref _negotiations);

        internal bool HubReady => Volatile.Read(ref _hubReady);

        internal DriverCall[] ListCalls() => [.. AllCalls.Where(call => call.Method == "GET" && call.Path == "/api/v1/driver/me/external-offers")];

        internal DriverCall[] Accepts() => [.. AllCalls.Where(call => call.Method == "POST" && call.Path.EndsWith("/accept", StringComparison.Ordinal))];

        internal async Task InstallAsync()
        {
            page.Console += (_, message) =>
            {
                if (message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal)) return;
                // The SignalR client's own log of the hub it cannot reach (blocked here unless the test mocks it) and of
                // the connection the test drops on purpose; neither is the offers' output.
                if (message.Text.Contains("Error from HTTP request. TypeError: Failed to fetch.", StringComparison.Ordinal) ||
                    message.Text.Contains("Failed to complete negotiation", StringComparison.Ordinal) ||
                    message.Text.Contains("Failed to start the connection", StringComparison.Ordinal) ||
                    message.Text.Contains("Connection disconnected with error 'Error: Not Found: Status code '404''", StringComparison.Ordinal)) return;
                _console.Enqueue($"{message.Type}:{message.Text}");
            };
            page.PageError += (_, error) => _console.Enqueue($"page:{error}");
            page.Request += (_, request) =>
            {
                if (!request.Url.StartsWith("http", StringComparison.Ordinal)) return;
                if (request.Url.StartsWith(DriverStopsNextServerFixture.TestApiOrigin + "/", StringComparison.Ordinal)) return;
                _foreign.Enqueue(request.Url);
            };
            await page.RouteAsync($"{DriverStopsNextServerFixture.TestApiOrigin}/**", async route =>
            {
                Record(route.Request, string.Empty);
                await route.AbortAsync("blockedbyclient");
            });
            if (hub)
            {
                await page.RouteAsync("**/hubs/driver**", HandleHubAsync);
            }
            else
            {
                await page.RouteAsync("**/hubs/driver/negotiate**", async route =>
                {
                    Record(route.Request, string.Empty);
                    await route.AbortAsync();
                });
            }

            await DriverOrganizationRoles.RouteAsync(page);
            await page.RouteAsync(DriverStopsPwaPlaywrightTests.StopsRoute, async route =>
            {
                Record(route.Request, string.Empty);
                Interlocked.Increment(ref _stopsReads);
                await route.FulfillAsync(Json(DriverStopsPwaPlaywrightTests.StopsBody(route.Request, [])));
            });
            await page.RouteAsync("**/api/v1/driver/me/phone", route => route.FulfillAsync(Json(JsonSerializer.Serialize(new
            {
                voice_calls_enabled = false,
                registered = false,
                consent_version = (string?)null,
                consented_at = (string?)null,
            }))));
            await page.RouteAsync(OffersRoute, HandleListAsync);
            await page.RouteAsync("**/api/v1/external-offers/*/accept", HandleAcceptAsync);
            await page.AddInitScriptAsync(
                $$"""
                window.__paquetenviaDriverSession = {
                  organizationId: {{JsonSerializer.Serialize(OrganizationId)}},
                  cacheNamespace: "ext-001-external-offers-driver",
                  getAccessToken: () => "synthetic-driver-token",
                };
                """);
        }

        private DriverCall Record(IRequest request, string? key)
        {
            var uri = new Uri(request.Url);
            var call = new DriverCall(
                request.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                key,
                request.PostData,
                request.Headers.GetValueOrDefault("x-organization-id") ?? string.Empty);
            lock (_gate) _calls.Add(call);
            return call;
        }

        private async Task HandleListAsync(IRoute route)
        {
            var call = Record(route.Request, null);
            if (call.Organization != OrganizationId || call.Query.Length > 0 || call.Body is not null)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 400 });
                return;
            }

            if (ListFailures.TryDequeue(out var status))
            {
                await route.FulfillAsync(Problem(status));
                return;
            }

            Offer[] list;
            lock (_gate)
            {
                if (Lists.TryDequeue(out var next)) _list = next;
                list = _list;
            }

            await route.FulfillAsync(Json(JsonSerializer.Serialize(new { items = list.Select(offer => offer.Json()).ToArray(), next_cursor = (string?)null })));
        }

        private async Task HandleAcceptAsync(IRoute route)
        {
            var headers = await route.Request.AllHeadersAsync();
            var call = Record(route.Request, headers.GetValueOrDefault("idempotency-key"));
            var status = AcceptAnswers.TryDequeue(out var next) ? next : 200;
            if (status != 200)
            {
                await route.FulfillAsync(Problem(status));
                return;
            }

            var offerId = call.Path.Split('/')[4];
            var orderId = new[] { First, Second }.First(offer => offer.Id == offerId).OrderId;
            await route.FulfillAsync(Json(JsonSerializer.Serialize(new
            {
                id = "a6000000-0000-4000-8000-000000000009",
                order_id = orderId,
                driver_id = DriverId,
                route_id = (string?)null,
                status = "ACCEPTED",
                cost = new { currency = "MXN", amount_cents = 4_550 },
            })));
        }

        /// <summary>
        /// The DriverHub over the SignalR JSON protocol and long polling: the negotiation (version 1, LongPolling only),
        /// the first poll, which answers at once, the polls that wait for the next server message, the client's sends
        /// (the handshake is answered, pings need nothing) and its DELETE when the connection ends.
        /// </summary>
        private async Task HandleHubAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Record(request, null);
            lock (_gate) _hubOrganizations.Add(query["organization_id"] ?? string.Empty);
            if (uri.AbsolutePath == "/hubs/driver/negotiate" && request.Method == "POST")
            {
                var token = $"synthetic-driver-connection-{Interlocked.Increment(ref _negotiations)}";
                lock (_gate)
                {
                    _connectionToken = token;
                    _polls = Channel.CreateUnbounded<string?>();
                }

                Volatile.Write(ref _firstPollAnswered, false);

                await route.FulfillAsync(Json(JsonSerializer.Serialize(new
                {
                    negotiateVersion = 1,
                    connectionId = token,
                    connectionToken = token,
                    availableTransports = new[] { new { transport = "LongPolling", transferFormats = new[] { "Text", "Binary" } } },
                })));
                return;
            }

            Channel<string?> polls;
            string current;
            lock (_gate)
            {
                polls = _polls;
                current = _connectionToken;
            }

            if (uri.AbsolutePath != "/hubs/driver" || query["id"] != current)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
                return;
            }

            switch (request.Method)
            {
                case "GET" when !Volatile.Read(ref _firstPollAnswered):
                    Volatile.Write(ref _firstPollAnswered, true);
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/plain", Body = string.Empty });
                    return;
                case "GET":
                    var message = await polls.Reader.ReadAsync();
                    // A null message drops the connection, as a server that no longer knows it answers.
                    if (message is null) await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
                    else await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/plain", Body = message });
                    return;
                case "POST":
                    if ((request.PostData ?? string.Empty).Contains("\"protocol\"", StringComparison.Ordinal))
                    {
                        polls.Writer.TryWrite("{}\u001e");
                        Volatile.Write(ref _hubReady, true);
                    }

                    await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/plain", Body = string.Empty });
                    return;
                default:
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 202 });
                    return;
            }
        }

        /// <summary>One AI-12 ExternalOfferChanged.v1 envelope pushed by the hub, with a payload the screen must not show.</summary>
        internal void SendOfferChanged(Offer offer, int version, long commissionCents)
        {
            var envelope = new
            {
                event_id = $"e7000000-0000-4000-8000-{version:x12}",
                event_type = "ExternalOfferChanged.v1",
                occurred_at = "2026-11-03T05:30:00Z",
                aggregate_id = offer.Id,
                aggregate_version = version,
                payload = new { offer_id = offer.Id, status = "OPEN", commission_cents = commissionCents, expires_at = offer.ExpiresAt },
            };
            Channel<string?> polls;
            lock (_gate) polls = _polls;
            polls.Writer.TryWrite(JsonSerializer.Serialize(new { type = 1, target = "ExternalOfferChanged", arguments = new[] { envelope } }) + "\u001e");
        }

        /// <summary>The server drops the connection, as an API restart would: the waiting poll answers 404.</summary>
        internal void DropHub()
        {
            Channel<string?> polls;
            lock (_gate) polls = _polls;
            Volatile.Write(ref _hubReady, false);
            Volatile.Write(ref _firstPollAnswered, false);
            polls.Writer.TryWrite(null);
        }

        /// <summary>The only UUIDs on screen are the offers' order ids (AI-07 must_show_before_accept: order_id).</summary>
        internal async Task AssertOnlyOrderIdsShownAsync(params Offer[] offers)
        {
            var visible = await page.Locator("body").InnerTextAsync();
            var shown = UuidPattern().Matches(visible).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal);
            Assert.Equal(offers.Select(offer => offer.OrderId).Order(StringComparer.Ordinal), shown);
            foreach (var id in new[] { OrganizationId, DriverId, First.Id, Second.Id })
            {
                Assert.DoesNotContain(id[..8], visible, StringComparison.Ordinal);
                Assert.DoesNotContain(id[^12..], visible, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// No offer reached browser storage - Web Storage, IndexedDB (where the app keeps its stops by design) or Cache
        /// Storage -, only the screen's sources were called, nothing went to a third origin and nothing was logged.
        /// </summary>
        internal async Task FinishAsync(params Offer[] offers)
        {
            var needles = offers.SelectMany(offer => new[] { offer.Id, offer.OrderId }).Append("external-offers").ToArray();
            var stored = await page.EvaluateAsync<string[]>(
                """
                async needles => {
                  const texts = [];
                  for (const storage of [localStorage, sessionStorage])
                    for (let index = 0; index < storage.length; index++)
                      texts.push(`storage:${storage.key(index)}=${storage.getItem(storage.key(index))}`);
                  if (document.cookie !== "") texts.push(`cookie:${document.cookie}`);
                  for (const info of await indexedDB.databases()) {
                    const database = await new Promise((resolve, reject) => {
                      const request = indexedDB.open(info.name);
                      request.onsuccess = () => resolve(request.result);
                      request.onerror = () => reject(request.error);
                    });
                    for (const store of database.objectStoreNames) {
                      const records = await new Promise((resolve, reject) => {
                        const request = database.transaction(store, "readonly").objectStore(store).getAll();
                        request.onsuccess = () => resolve(request.result);
                        request.onerror = () => reject(request.error);
                      });
                      texts.push(`indexedDB:${info.name}/${store}=${JSON.stringify(records)}`);
                    }
                    database.close();
                  }
                  for (const name of await caches.keys())
                    for (const request of await (await caches.open(name)).keys()) texts.push(`cache:${request.url}`);
                  return texts.filter(text => needles.some(needle => text.includes(needle)));
                }
                """,
                needles);
            Assert.Empty(stored);
            lock (_gate) Assert.All(_hubOrganizations, organization => Assert.Equal(OrganizationId, organization));
            var calls = AllCalls;
            Assert.NotEmpty(calls);
            Assert.All(calls, call => Assert.Contains(AllowedCalls, pattern => pattern.IsMatch($"{call.Method} {call.Path}")));
            var webOrigin = web.GetLeftPart(UriPartial.Authority) + "/";
            Assert.DoesNotContain(_foreign, url => !url.StartsWith(webOrigin, StringComparison.Ordinal));
            Assert.True(_console.IsEmpty, string.Join(" || ", _console));
            trace.Passed();
        }

        private static RouteFulfillOptions Json(string body) => new()
        {
            Status = 200,
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = body,
        };

        private static RouteFulfillOptions Problem(int status) => new()
        {
            Status = status,
            ContentType = "application/problem+json",
            Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status }),
        };

        public async ValueTask DisposeAsync()
        {
            await trace.DisposeAsync();
            await context.DisposeAsync();
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }
}
