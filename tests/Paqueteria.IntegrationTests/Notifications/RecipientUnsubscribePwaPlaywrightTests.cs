using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Notifications;

/// <summary>
/// AI-08 NTF-006, AI-07 <c>/avisos/baja/:token</c> (NTF-RECIPIENT-NOTICES-2026-10-20 E4-P8, the texts of
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 O9): the page the unsubscribe link of a recipient e-mail opens, in a real
/// browser against the production Next build, on a phone in Tokyo. Recipient notices stay off in the pilot; the page is
/// tested as AI-07 defines it, with unsubscribeRecipientNotices fulfilled in the browser in its AI-05 shape (204, the
/// uniform 404, 429 with Retry-After, 503). The page opens with the neutral heading "Seguimiento de envío" and no brand,
/// links no PWA manifest, is not indexed, starts no session and registers no service worker; opening it sends nothing,
/// and one press of "Dejar de recibir correos" sends the token of the link once, as the JSON object {}, without
/// credentials or referrer; success, an invalid link (malformed, which sends nothing, or refused), a wait and a failure
/// each show one es-MX message; the wait counts down with the button disabled until it ends. The page never shows the
/// token, an e-mail, an order or an organization, and nothing reaches browser storage or the console.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class RecipientUnsubscribePwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    /// <summary>88 Base64URL characters, the format the API issues: a repeated synthetic word, never a credential.</summary>
    private const string Token =
        "baja-sintetica-baja-sintetica-baja-sintetica-baja-sintetica-baja-sintetica-baja-sintetic";

    private const string Heading = "Seguimiento de envío";
    private const string Button = "Dejar de recibir correos";
    private const string Success = "Listo. Ya no recibirás correos de avisos en esta dirección.";
    private const string InvalidLink = "Este enlace no es válido o ya no está disponible.";
    private const string Failure = "No pudimos procesar tu solicitud. Intenta más tarde.";

    /// <summary>
    /// The page as AI-07 rules it before and after one press: the neutral heading and title, no manifest, not indexed,
    /// no-store; nothing is sent when it opens; the press sends one POST of {} with the token and no credentials, and
    /// its 204 shows the success line and removes the button.
    /// </summary>
    [Fact]
    [Trait("Category", "PublicTrackingPwa")]
    public async Task Opening_the_link_sends_nothing_and_one_press_records_the_opt_out()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Answers.Enqueue(204);

        var response = await page.GotoAsync(new Uri(server.BaseAddress, $"/avisos/baja/{Token}").AbsoluteUri);
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.Contains("no-store", response.Headers.GetValueOrDefault("cache-control") ?? string.Empty, StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Button, new() { Name = Button, Exact = true }).WaitForAsync();
        Assert.Equal(Heading, (await page.Locator("h1").InnerTextAsync()).Trim());
        Assert.Equal(Heading, await page.TitleAsync());
        Assert.Equal(0, await page.Locator("link[rel=manifest]").CountAsync());
        var robots = await page.Locator("meta[name=robots]").GetAttributeAsync("content") ?? string.Empty;
        Assert.Contains("noindex", robots, StringComparison.Ordinal);
        Assert.Contains("nofollow", robots, StringComparison.Ordinal);
        Assert.Equal($"{Heading} {Button}", await VisibleTextAsync(page));

        // Opening the page changes nothing: a link scanner never unsubscribes anyone.
        await Task.Delay(1_000);
        Assert.Empty(phone.Posts());

        await page.GetByRole(AriaRole.Button, new() { Name = Button, Exact = true }).ClickAsync();
        await ExpectStatusAsync(page, Success);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button).CountAsync());
        Assert.Equal($"{Heading} {Success}", await VisibleTextAsync(page));
        var post = Assert.Single(phone.Posts());
        Assert.Equal($"/api/v1/recipient-notices/unsubscribe/{Token}", post.Path);
        Assert.Equal(string.Empty, post.Query);
        Assert.Equal("{}", post.Body);
        Assert.Equal("application/json", post.ContentType);
        Assert.Null(post.Authorization);
        Assert.Null(post.Cookie);
        Assert.Null(post.Referer);
        Assert.Null(post.Organization);

        await phone.FinishAsync();
    }

    /// <summary>
    /// AI-07 uniform answers: a link whose token is malformed says the link is not valid at once and sends nothing; a
    /// well-formed token the API refuses (the uniform 404) says exactly the same, with the same markup, after one press.
    /// </summary>
    [Fact]
    [Trait("Category", "PublicTrackingPwa")]
    public async Task A_malformed_or_refused_link_says_the_same_and_only_a_well_formed_one_is_sent()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Answers.Enqueue(404);

        // 87 characters: not the token format, so the page says so at once and sends nothing.
        await page.GotoAsync(new Uri(server.BaseAddress, $"/avisos/baja/{Token[..87]}").AbsoluteUri);
        await ExpectStatusAsync(page, InvalidLink);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button).CountAsync());
        var malformed = await page.Locator("main").InnerHTMLAsync();
        await Task.Delay(1_000);
        Assert.Empty(phone.Posts());

        await page.GotoAsync(new Uri(server.BaseAddress, $"/avisos/baja/{Token}").AbsoluteUri);
        await page.GetByRole(AriaRole.Button, new() { Name = Button, Exact = true }).ClickAsync();
        await ExpectStatusAsync(page, InvalidLink);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button).CountAsync());
        Assert.Equal(malformed, await page.Locator("main").InnerHTMLAsync());
        Assert.Equal($"{Heading} {InvalidLink}", await VisibleTextAsync(page));
        var post = Assert.Single(phone.Posts());
        Assert.Equal($"/api/v1/recipient-notices/unsubscribe/{Token}", post.Path);

        await phone.FinishAsync();
    }

    /// <summary>
    /// AI-07 wait_rule and failure: a 429 counts down from its Retry-After in whole seconds ("1 segundo" at one) with the
    /// button disabled and nothing sent until it ends; a 503 shows the failure line and keeps the button; each press sends
    /// the same request once, and the last one is recorded.
    /// </summary>
    [Fact]
    [Trait("Category", "PublicTrackingPwa")]
    public async Task A_wait_counts_down_with_the_button_disabled_and_a_failure_keeps_the_button()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Answers.Enqueue(429);
        phone.Answers.Enqueue(503);
        phone.Answers.Enqueue(204);

        await page.GotoAsync(new Uri(server.BaseAddress, $"/avisos/baja/{Token}").AbsoluteUri);
        var button = page.GetByRole(AriaRole.Button, new() { Name = Button, Exact = true });
        await button.ClickAsync();
        var timer = page.GetByRole(AriaRole.Timer);
        await timer.GetByText(TooManyRequestsPattern()).WaitForAsync();
        Assert.True(await button.IsDisabledAsync());
        await timer.GetByText("Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo.", new() { Exact = true }).WaitForAsync();
        Assert.True(await button.IsDisabledAsync());
        await WaitForAsync(async () => await timer.CountAsync() == 0 && await button.IsEnabledAsync(), "the wait did not end");
        Assert.Single(phone.Posts());
        Assert.Equal($"{Heading} {Button}", await VisibleTextAsync(page));

        await button.ClickAsync();
        await ExpectStatusAsync(page, Failure);
        Assert.True(await button.IsEnabledAsync());
        Assert.Equal($"{Heading} {Failure} {Button}", await VisibleTextAsync(page));

        await button.ClickAsync();
        await ExpectStatusAsync(page, Success);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button).CountAsync());
        var posts = phone.Posts();
        Assert.Equal(3, posts.Length);
        Assert.All(posts, post =>
        {
            Assert.Equal($"/api/v1/recipient-notices/unsubscribe/{Token}", post.Path);
            Assert.Equal("{}", post.Body);
        });
        Assert.True(posts[1].At - posts[0].At >= TimeSpan.FromSeconds(5.5), "The button was pressed again during the wait.");

        await phone.FinishAsync();
    }

    private static async Task<string> VisibleTextAsync(IPage page) =>
        Regex.Replace(await page.Locator("body").InnerTextAsync(), "\\s+", " ").Trim();

    private static async Task ExpectStatusAsync(IPage page, string text)
    {
        var status = page.Locator("p[role=status]");
        var actual = string.Empty;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await status.CountAsync() == 1)
            {
                actual = (await status.InnerTextAsync()).Trim();
                if (actual == text) return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Expected «{text}», found «{actual}».");
    }

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

    private async Task<PhoneSession> StartAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.PublicTrackingJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var phone = new PhoneSession(playwright, browser, context, trace, page, server.BaseAddress);
        await phone.InstallAsync();
        return phone;
    }

    [GeneratedRegex("^Demasiadas solicitudes\\. Espera [1-6] segundos? e intenta de nuevo\\.$")]
    private static partial Regex TooManyRequestsPattern();

    private sealed record UnsubscribeCall(
        DateTimeOffset At, string Method, string Path, string Query, string? Body, string? ContentType,
        string? Authorization, string? Cookie, string? Referer, string? Organization);

    private sealed class PhoneSession(
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        PlaywrightFailureTrace trace,
        IPage page,
        Uri web) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<UnsubscribeCall> _calls = [];
        private readonly ConcurrentQueue<string> _console = new();
        private readonly ConcurrentQueue<string> _webPaths = new();
        private readonly ConcurrentQueue<string> _foreign = new();

        internal IPage Page => page;

        /// <summary>The statuses of the next unsubscribeRecipientNotices answers.</summary>
        internal ConcurrentQueue<int> Answers { get; } = new();

        internal UnsubscribeCall[] Posts()
        {
            lock (_gate) return [.. _calls];
        }

        internal async Task InstallAsync()
        {
            page.Console += (_, message) =>
            {
                if (!message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal))
                {
                    _console.Enqueue($"{message.Type}:{message.Text}");
                }
            };
            page.PageError += (_, error) => _console.Enqueue($"page:{error}");
            var webOrigin = web.GetLeftPart(UriPartial.Authority);
            page.Request += (_, request) =>
            {
                if (!request.Url.StartsWith("http", StringComparison.Ordinal)) return;
                if (request.Url.StartsWith(webOrigin + "/", StringComparison.Ordinal)) _webPaths.Enqueue(new Uri(request.Url).AbsolutePath);
                else if (!request.Url.StartsWith(DriverStopsNextServerFixture.TestApiOrigin + "/", StringComparison.Ordinal)) _foreign.Enqueue(request.Url);
            };
            // Every API call is recorded; only the unsubscribe operation is answered.
            await page.RouteAsync($"{DriverStopsNextServerFixture.TestApiOrigin}/**", async route =>
            {
                var request = route.Request;
                var uri = new Uri(request.Url);
                var headers = await request.AllHeadersAsync();
                var call = new UnsubscribeCall(
                    DateTimeOffset.UtcNow,
                    request.Method,
                    uri.AbsolutePath,
                    uri.Query.TrimStart('?'),
                    request.PostData,
                    headers.GetValueOrDefault("content-type"),
                    headers.GetValueOrDefault("authorization"),
                    headers.GetValueOrDefault("cookie"),
                    headers.GetValueOrDefault("referer"),
                    headers.GetValueOrDefault("x-organization-id"));
                lock (_gate) _calls.Add(call);
                if (call.Method != "POST" || !call.Path.StartsWith("/api/v1/recipient-notices/unsubscribe/", StringComparison.Ordinal))
                {
                    await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
                    return;
                }

                var status = Answers.TryDequeue(out var next) ? next : 204;
                await route.FulfillAsync(status switch
                {
                    204 => new RouteFulfillOptions
                    {
                        Status = 204,
                        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store", ["Referrer-Policy"] = "no-referrer" },
                    },
                    429 => new RouteFulfillOptions
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
                        Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Too Many Requests", status = 429 }),
                    },
                    _ => new RouteFulfillOptions
                    {
                        Status = status,
                        ContentType = "application/problem+json",
                        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
                        Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status }),
                    },
                });
            });
        }

        /// <summary>
        /// The token is never on screen; no session, no service worker, no browser storage; only the unsubscribe
        /// operation was called and only the page's own assets were fetched; nothing was logged.
        /// </summary>
        internal async Task FinishAsync()
        {
            Assert.DoesNotContain(Token[..20], await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
            var findings = await page.EvaluateAsync<string[]>(
                """
                async () => {
                  const findings = [];
                  if (localStorage.length > 0) findings.push(`localStorage:${localStorage.length}`);
                  if (sessionStorage.length > 0) findings.push(`sessionStorage:${sessionStorage.length}`);
                  if (document.cookie !== "") findings.push("cookie");
                  for (const database of await indexedDB.databases()) findings.push(`indexedDB:${database.name}`);
                  for (const name of await caches.keys()) findings.push(`cache:${name}`);
                  for (const registration of await navigator.serviceWorker.getRegistrations()) findings.push(`sw:${registration.scope}`);
                  if (navigator.serviceWorker.controller !== null) findings.push("sw:controller");
                  return findings;
                }
                """);
            Assert.Empty(findings);
            Assert.DoesNotContain(Posts(), call =>
                call.Method != "POST" || !call.Path.StartsWith("/api/v1/recipient-notices/unsubscribe/", StringComparison.Ordinal));
            Assert.DoesNotContain(_webPaths, path =>
                path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/auth/", StringComparison.Ordinal) ||
                path == "/manifest.webmanifest" || path == "/sw.js");
            Assert.Empty(_foreign);
            Assert.True(_console.IsEmpty, string.Join(" || ", _console));
            trace.Passed();
        }

        public async ValueTask DisposeAsync()
        {
            await trace.DisposeAsync();
            await context.DisposeAsync();
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }
}
