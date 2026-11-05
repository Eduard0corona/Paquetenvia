using System.Collections.Concurrent;
using System.Text.Json;
using Identity.Endpoints.AuthCenter;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Npgsql;
using Paqueteria.IntegrationTests.Driver;
using Paqueteria.IntegrationTests.Realtime;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// AI-08 AUTH-001 required test "browser test proving tokens are not exposed to JavaScript", and the panel sign-in of
/// AI-07 <c>/login</c> (GATE-002-BFF-001, AUTH-001-MFA-STEP-UP, AUTH-001-ACCESS-DENIED-MESSAGE,
/// AUTH-001-RP-INITIATED-LOGOUT) in a real browser, end to end on one origin: the API on Kestrel over PostgreSQL with
/// AuthCenter pointing at <see cref="FakeAuthCenterServer"/> (discovery, token, JWKS and revocation on its back channel),
/// the production Next.js build in BFF mode, and <see cref="SameOriginFront"/> serving both on
/// <c>https://app.paquetenvia.test</c> as the pilot's ingress does. Only the browser's hop to AuthCenter's authorize and
/// end-session pages is played by Playwright routing, through the fake's own validation (client, exact redirect URI,
/// scopes, S256 PKCE, state, nonce). Signing in leaves the session in the <c>__Host-</c> HttpOnly cookie, which
/// <c>document.cookie</c> never sees, and no access, refresh or ID token reaches Web Storage, IndexedDB, Cache Storage,
/// the page's globals, its markup or <c>/auth/session</c>; each role lands on its own screen; a write without the CSRF
/// header is refused; logout revokes the refresh token, ends the AuthCenter session and returns to <c>/login</c>
/// signed out; <c>/login?mfa=required</c> asks AuthCenter for the second factor and returns to the screen; an account
/// without access sees the access-denied message.
/// </summary>
[Collection(PanelSignInPwaCollection.Name)]
public sealed class PanelSignInPwaPlaywrightTests(PostgreSqlSecurityWebApplicationFactory database)
{
    private const string PublicOrigin = FakeAuthCenterServer.PublicOrigin;
    private const string OperationsOrganizationId = "22222222-2222-2222-2222-222222222222";
    private const string DispatcherSubject = "auth001-panel-dispatcher";
    private const string FinanceSubject = "auth001-panel-finance";
    private const string ViewerSubject = "mock-subject-active-viewer";
    private const string PlatformAdminSubject = "mock-subject-platform-admin-no-mfa";
    private const string SuspendedSubject = "mock-subject-suspended";

    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    /// <summary>
    /// A DISPATCHER signs in from <c>/login</c> and lands on <c>/ops/inbox</c>. The session lives only in the
    /// <c>__Host-</c> cookie (HttpOnly, Secure, SameSite=Lax, path /, host-only), the OIDC correlation and nonce cookies
    /// are gone, and none of the three tokens AuthCenter issued is anywhere the page's JavaScript can read.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Sign_in_keeps_the_session_in_an_HttpOnly_cookie_and_no_token_reaches_the_page()
    {
        await using var site = await StartAsync();
        var page = site.Page;
        site.Subject = DispatcherSubject;

        await SignInAsync(site);
        await ExpectSignedInAsync(page, "Synthetic Operations");
        var continuar = page.GetByRole(AriaRole.Link, new() { Name = "Continuar", Exact = true });
        Assert.Equal("/ops/inbox", await continuar.GetAttributeAsync("href"));
        await continuar.ClickAsync();
        await page.WaitForURLAsync($"{PublicOrigin}/ops/inbox");

        var cookies = await site.Context.CookiesAsync([PublicOrigin]);
        var session = Assert.Single(cookies, cookie => cookie.Name == AuthCenterDefaults.SessionCookieName);
        Assert.True(session.HttpOnly);
        Assert.True(session.Secure);
        Assert.Equal(SameSiteAttribute.Lax, session.SameSite);
        Assert.Equal("/", session.Path);
        Assert.Equal("app.paquetenvia.test", session.Domain);
        Assert.DoesNotContain(cookies, cookie =>
            cookie.Name.StartsWith(AuthCenterDefaults.CorrelationCookiePrefix, StringComparison.Ordinal) ||
            cookie.Name.StartsWith(AuthCenterDefaults.NonceCookiePrefix, StringComparison.Ordinal));
        Assert.Equal(string.Empty, await page.EvaluateAsync<string>("() => document.cookie"));

        // /auth/session answers what the page needs and no token.
        var answer = await page.EvaluateAsync<JsonElement>(
            """
            async () => {
              const response = await fetch("/auth/session", { credentials: "include", cache: "no-store" });
              return { status: response.status, body: await response.text() };
            }
            """);
        Assert.Equal(200, answer.GetProperty("status").GetInt32());
        using (var body = JsonDocument.Parse(answer.GetProperty("body").GetString()!))
        {
            Assert.Equal(
                ["authenticated", "authorized", "csrfToken", "mfa", "sessionNamespace", "user"],
                body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(["email", "name"], body.RootElement.GetProperty("user").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.True(body.RootElement.GetProperty("authorized").GetBoolean());
        }

        await AssertNoTokenReachedThePageAsync(site, answer.GetProperty("body").GetString()!);
        site.Passed();
    }

    /// <summary>Each role lands on its own screen (AI-07 landing): FINANCE on Cobro contra entrega, VIEWER on Órdenes.</summary>
    [Theory]
    [Trait("Category", "DriverStopsPwa")]
    [InlineData(FinanceSubject, "Synthetic Operations", "/finance/cod")]
    [InlineData(ViewerSubject, "Synthetic Viewer", "/ops/orders")]
    public async Task Each_role_lands_on_its_own_screen(string subject, string organization, string landing)
    {
        await using var site = await StartAsync(subject);
        var page = site.Page;
        site.Subject = subject;

        await SignInAsync(site);
        await ExpectSignedInAsync(page, organization);
        var continuar = page.GetByRole(AriaRole.Link, new() { Name = "Continuar", Exact = true });
        Assert.Equal(landing, await continuar.GetAttributeAsync("href"));
        await continuar.ClickAsync();
        await page.WaitForURLAsync($"{PublicOrigin}{landing}");
        await AssertNoTokenReachedThePageAsync(site, null);
        site.Passed();
    }

    /// <summary>
    /// A cookie-authenticated write without the CSRF header is refused and changes nothing; "Cerrar sesión" sends it
    /// with the header, revokes the refresh token, ends the AuthCenter session (RP-initiated logout with the ID token
    /// hint and the registered return) and lands on <c>/login</c> signed out, the cookie gone.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_write_without_the_CSRF_header_is_refused_and_logout_ends_the_session()
    {
        await using var site = await StartAsync();
        var page = site.Page;
        site.Subject = DispatcherSubject;
        await SignInAsync(site);
        await ExpectSignedInAsync(page, "Synthetic Operations");
        var refreshToken = site.AuthCenter.LastRefreshToken;
        var idToken = site.AuthCenter.LastIdToken;

        var refused = await page.EvaluateAsync<int>(
            "async () => (await fetch('/auth/logout', { method: 'POST', credentials: 'include' })).status");
        // The cookie alone authenticates nothing that writes: without the session-bound header the API answers 401.
        Assert.Equal(401, refused);
        Assert.Equal(200, await SessionStatusAsync(page));
        Assert.Empty(site.AuthCenter.RevokedTokens);

        await page.GetByRole(AriaRole.Button, new() { Name = "Cerrar sesión", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter", Exact = true }).WaitForAsync();
        Assert.Equal($"{PublicOrigin}/login", page.Url);
        var endSession = Assert.Single(site.EndSessionRequests);
        FakeAuthCenterServer.AssertEndSessionRequest(endSession, idToken);
        Assert.Contains(refreshToken, site.AuthCenter.RevokedTokens);
        Assert.DoesNotContain(await site.Context.CookiesAsync([PublicOrigin]), cookie => cookie.Name == AuthCenterDefaults.SessionCookieName);
        Assert.Equal(401, await SessionStatusAsync(page));
        site.Passed();
    }

    /// <summary>
    /// AUTH-001-MFA-STEP-UP: a PLATFORM_ADMIN signed in without a second factor opens <c>/login?mfa=required</c> with a
    /// return to the inbox; "Verificar identidad" asks AuthCenter for <c>acr_values=urn:authcenter:acr:mfa</c>, the new
    /// session has MFA and the browser returns to the inbox.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Step_up_asks_AuthCenter_for_the_second_factor_and_returns_to_the_screen()
    {
        await using var site = await StartAsync();
        var page = site.Page;
        site.Subject = PlatformAdminSubject;
        await SignInAsync(site);
        await ExpectSignedInAsync(page, "Synthetic Viewer");
        Assert.Null(site.AuthCenter.LastRequestedAcrValues);
        Assert.False(await SessionMfaAsync(page));

        await page.GotoAsync($"{PublicOrigin}/login?mfa=required&return_url=%2Fops%2Finbox");
        var verify = page.GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true });
        await verify.WaitForAsync();
        Assert.Equal("/auth/login?mfa=required&return_url=%2Fops%2Finbox", await verify.GetAttributeAsync("href"));
        await verify.ClickAsync();
        await page.WaitForURLAsync($"{PublicOrigin}/ops/inbox");
        Assert.Equal(AuthCenterDefaults.MfaContextClass, site.AuthCenter.LastRequestedAcrValues);
        Assert.True(await SessionMfaAsync(page));
        Assert.Equal(2, site.Authorizations.Count);
        await AssertNoTokenReachedThePageAsync(site, null);
        site.Passed();
    }

    /// <summary>
    /// AUTH-001-ACCESS-DENIED-MESSAGE: when AuthCenter answers the sign-in with <c>access_denied</c>, the API returns to
    /// <c>/login?error=access_denied</c>, which says so (never AuthCenter's description) and offers to sign in again,
    /// with no session. AI-08 AUTH-001 "suspended user ... return 403": a suspended account that AuthCenter signs in gets
    /// a session that is not authorized, the page says the account has no access and offers no screen, and a tenant read
    /// answers 403.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_denied_sign_in_and_a_suspended_account_get_no_access()
    {
        await using var site = await StartAsync();
        var page = site.Page;
        site.DenyNext = true;

        await SignInAsync(site);
        await page.WaitForURLAsync($"{PublicOrigin}/login?error=access_denied");
        await page.GetByRole(AriaRole.Alert).GetByText("Tu cuenta no tiene acceso a Paquetenvia; pídelo a un administrador", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter", Exact = true }).WaitForAsync();
        Assert.DoesNotContain("secret-detail", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(await site.Context.CookiesAsync([PublicOrigin]), cookie => cookie.Name == AuthCenterDefaults.SessionCookieName);
        Assert.Equal(401, await SessionStatusAsync(page));

        site.Subject = SuspendedSubject;
        await SignInAsync(site);
        await page.WaitForURLAsync($"{PublicOrigin}/login");
        await page.GetByRole(AriaRole.Status).GetByText("Sesión iniciada como Usuario Sintético.", new() { Exact = true }).WaitForAsync();
        await page.GetByText("Tu cuenta no tiene acceso a Paquetenvia en este momento.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Continuar", Exact = true }).CountAsync());
        var contexts = await page.EvaluateAsync<int>(
            "async () => (await fetch('/api/v1/me/organization-contexts', { credentials: 'include', cache: 'no-store' })).status");
        Assert.Equal(403, contexts);
        site.Passed();
    }

    // ---------- helpers ----------

    /// <summary>"Continuar con AuthCenter" from <c>/login</c>; AuthCenter's interactive step answers for the subject.</summary>
    private static async Task SignInAsync(Site site)
    {
        await site.Page.GotoAsync($"{PublicOrigin}/login");
        var start = site.Page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter", Exact = true });
        await start.WaitForAsync();
        Assert.Equal("/auth/login?return_url=%2Flogin", await start.GetAttributeAsync("href"));
        await start.ClickAsync();
    }

    private static async Task ExpectSignedInAsync(IPage page, string organization)
    {
        await page.WaitForURLAsync($"{PublicOrigin}/login");
        await page.GetByRole(AriaRole.Status).GetByText("Sesión iniciada como Usuario Sintético.", new() { Exact = true }).WaitForAsync();
        await page.GetByText($"Organización activa: {organization}.", new() { Exact = true }).WaitForAsync();
    }

    private static Task<int> SessionStatusAsync(IPage page) =>
        page.EvaluateAsync<int>("async () => (await fetch('/auth/session', { credentials: 'include', cache: 'no-store' })).status");

    private static Task<bool> SessionMfaAsync(IPage page) =>
        page.EvaluateAsync<bool>("async () => (await (await fetch('/auth/session', { credentials: 'include', cache: 'no-store' })).json()).mfa");

    /// <summary>
    /// None of the tokens of the current session (access, refresh, ID) is in Web Storage, IndexedDB, Cache
    /// Storage, a string or plain object global of the page, its markup, <c>document.cookie</c> or the
    /// <c>/auth/session</c> answer.
    /// </summary>
    private static async Task AssertNoTokenReachedThePageAsync(Site site, string? sessionAnswer)
    {
        string[] tokens = [site.AuthCenter.LastAccessToken, site.AuthCenter.LastRefreshToken, site.AuthCenter.LastIdToken];
        Assert.All(tokens, token => Assert.False(string.IsNullOrEmpty(token)));
        var needles = tokens.Select(token => token.Length > 40 ? token[^40..] : token).ToArray();
        var found = await site.Page.EvaluateAsync<string[]>(
            """
            async needles => {
              const texts = [document.documentElement.outerHTML, document.cookie];
              for (const storage of [localStorage, sessionStorage])
                for (let index = 0; index < storage.length; index++)
                  texts.push(`${storage.key(index)}=${storage.getItem(storage.key(index))}`);
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
                  texts.push(JSON.stringify(records));
                }
                database.close();
              }
              for (const name of await caches.keys())
                for (const request of await (await caches.open(name)).keys()) {
                  texts.push(request.url);
                  const response = await (await caches.open(name)).match(request);
                  if (response !== undefined) texts.push(await response.clone().text());
                }
              for (const key of Object.getOwnPropertyNames(window)) {
                let value;
                try { value = window[key]; } catch { continue; }
                if (typeof value === "string") texts.push(value);
                else if (value !== null && typeof value === "object" && key.startsWith("__paquetenvia")) {
                  try { texts.push(JSON.stringify(value)); } catch { }
                  for (const member of Object.values(value)) {
                    if (typeof member !== "function") continue;
                    try { const result = await member(); if (typeof result === "string") texts.push(result); } catch { }
                  }
                }
              }
              return needles.filter(needle => texts.some(text => text.includes(needle)));
            }
            """,
            needles);
        Assert.Empty(found);
        if (sessionAnswer is not null)
        {
            Assert.DoesNotContain(needles, needle => sessionAnswer.Contains(needle, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The API (Kestrel, PostgreSQL, AuthCenter at the fake), the Next.js BFF build and the same-origin front, and a
    /// browser that resolves <c>app.paquetenvia.test</c> to the front.
    /// </summary>
    private async Task<Site> StartAsync(string? subject = null)
    {
        await SeedAsync();
        var authCenter = new FakeAuthCenterServer();
        var api = new RealtimeKestrelWebApplicationFactory(
            database.ApplicationConnectionString,
            new RealtimeAuthorizationRecorder(),
            allowedOrigin: PublicOrigin,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "AuthCenter",
                ["AuthCenter:Authority"] = FakeAuthCenterServer.Authority,
                ["AuthCenter:Issuer"] = FakeAuthCenterServer.Issuer,
                ["AuthCenter:ClientId"] = FakeAuthCenterServer.ClientId,
                ["AuthCenter:ClientSecret"] = FakeAuthCenterServer.ClientSecret,
                ["AuthCenter:PublicOrigin"] = PublicOrigin,
                ["AuthCenter:SessionStore"] = "Memory",
            },
            configureTestServices: services => services.Configure<OpenIdConnectOptions>(
                AuthCenterDefaults.OpenIdConnectScheme,
                options => options.BackchannelHttpHandler = authCenter));
        var apiAddress = api.Start();
        var web = await DriverStopsNextServer.StartProductionBffAsync();
        var front = await SameOriginFront.StartAsync(apiAddress, web.BaseAddress, RealtimeKestrelWebApplicationFactory.LoopbackCertificate);
        var interactive = await AuthCenterInteractiveStep.StartAsync(authCenter);
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // The public origin is the front on loopback and is reached directly; any other host would go to a proxy
            // that does not exist, so nothing leaves the machine (and a proxy of the environment is never used).
            Proxy = new Proxy { Server = "http://127.0.0.1:9", Bypass = "app.paquetenvia.test,authcenter.test" },
            Args =
            [
                "--ignore-certificate-errors",
                $"--host-resolver-rules=MAP app.paquetenvia.test:443 127.0.0.1:{front.Port},MAP authcenter.test:443 127.0.0.1:{interactive.Port}",
            ],
        });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            // The panel does not use the driver's service worker; with it blocked, every request of the sign-in is the
            // page's own and reaches the routes below.
            ServiceWorkers = ServiceWorkerPolicy.Block,
        });
        var trace = await PlaywrightFailureTrace.StartAsync(context, subject, PlaywrightFailureTrace.DriverStopsJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var site = new Site(authCenter, interactive, api, web, front, playwright, browser, context, trace, page);
        await site.InstallAsync();
        return site;
    }

    /// <summary>A DISPATCHER and a FINANCE member of the operations organization, signed in by AuthCenter subject.</summary>
    private async Task SeedAsync()
    {
        await SeedGate.WaitAsync();
        try
        {
            if (_seeded) return;
            await using var connection = new NpgsqlConnection(database.AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO identity.users(id,identity_subject,status) VALUES
                  ('a0010000-0000-4000-8000-000000000001',@dispatcher,'ACTIVE'),
                  ('a0010000-0000-4000-8000-000000000002',@finance,'ACTIVE')
                ON CONFLICT DO NOTHING;
                INSERT INTO organizations.organization_memberships(id,user_id,organization_id,role,status,is_default)
                SELECT gen_random_uuid(), member.user_id, @organization::uuid, member.role, 'ACTIVE', true
                FROM (VALUES
                  ('a0010000-0000-4000-8000-000000000001'::uuid,'DISPATCHER'),
                  ('a0010000-0000-4000-8000-000000000002'::uuid,'FINANCE')) AS member(user_id, role)
                WHERE NOT EXISTS (
                  SELECT 1 FROM organizations.organization_memberships existing WHERE existing.user_id = member.user_id);
                """,
                connection);
            command.Parameters.AddWithValue("dispatcher", DispatcherSubject);
            command.Parameters.AddWithValue("finance", FinanceSubject);
            command.Parameters.AddWithValue("organization", OperationsOrganizationId);
            await command.ExecuteNonQueryAsync();
            _seeded = true;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private sealed class Site(
        FakeAuthCenterServer authCenter,
        AuthCenterInteractiveStep interactive,
        RealtimeKestrelWebApplicationFactory api,
        DriverStopsNextServer web,
        SameOriginFront front,
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        PlaywrightFailureTrace trace,
        IPage page) : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _console = new();

        internal FakeAuthCenterServer AuthCenter => authCenter;

        internal IBrowserContext Context => context;

        internal IPage Page => page;

        /// <summary>Who AuthCenter's interactive step signs in.</summary>
        internal string Subject
        {
            get => interactive.Subject;
            set => interactive.Subject = value;
        }

        internal ConcurrentQueue<string> Authorizations => interactive.Authorizations;

        /// <summary>AuthCenter answers the next sign-in with <c>access_denied</c>.</summary>
        internal bool DenyNext
        {
            set => interactive.DenyNext = value;
        }

        internal ConcurrentQueue<string> EndSessionRequests => interactive.EndSessionRequests;

        internal async Task InstallAsync()
        {
            page.Console += (_, message) =>
            {
                // The browser's own lines: resource errors and Playwright's note that it blocked the service worker.
                if (!message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal) &&
                    message.Text != "Service Worker registration blocked by Playwright")
                {
                    _console.Enqueue($"{message.Type}:{message.Text}");
                }
            };
            page.PageError += (_, error) => _console.Enqueue($"page:{error}");
            // The hubs are not part of the sign-in; the front does not relay WebSocket upgrades.
            await context.RouteAsync($"{PublicOrigin}/hubs/**", route => route.AbortAsync());
        }

        internal void Passed()
        {
            Assert.True(_console.IsEmpty, string.Join(" || ", _console));
            trace.Passed();
        }

        public async ValueTask DisposeAsync()
        {
            await trace.DisposeAsync();
            await context.DisposeAsync();
            await browser.DisposeAsync();
            playwright.Dispose();
            await front.DisposeAsync();
            await interactive.DisposeAsync();
            await web.DisposeAsync();
            await api.DisposeAsync();
            authCenter.Dispose();
        }
    }
}

/// <summary>
/// AuthCenter's pages the browser itself visits, on <c>https://authcenter.test</c> (mapped to a loopback listener): the
/// interactive step of <c>/oauth/authorize</c>, which signs in <see cref="Subject"/> through
/// <see cref="FakeAuthCenterServer.Authorize"/> (it validates client, exact redirect URI, scopes, S256 PKCE, state and
/// nonce) and returns to the callback, and <c>/oauth/logout</c>, which returns to the registered post-logout page.
/// A listener, not Playwright routing: Microsoft.Playwright 1.61 does not route the target of a redirect the browser
/// follows from a request it was not asked to route (the API's 302 to the authorize page).
/// </summary>
internal sealed class AuthCenterInteractiveStep : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;

    private AuthCenterInteractiveStep(Microsoft.AspNetCore.Builder.WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    internal int Port { get; }

    internal string Subject { get; set; } = string.Empty;

    /// <summary>The next authorization request is answered with <c>access_denied</c> instead of a code.</summary>
    internal bool DenyNext { get; set; }

    internal ConcurrentQueue<string> Authorizations { get; } = new();

    internal ConcurrentQueue<string> EndSessionRequests { get; } = new();

    internal static async Task<AuthCenterInteractiveStep> StartAsync(FakeAuthCenterServer authCenter)
    {
        AuthCenterInteractiveStep? step = null;
        var (app, port) = await LoopbackHttpsHost.StartAsync(
            RealtimeKestrelWebApplicationFactory.LoopbackCertificate,
            context =>
            {
                var url = $"{FakeAuthCenterServer.Authority}{context.Request.Path}{context.Request.QueryString}";
                context.Response.Headers.CacheControl = "no-store";
                switch (context.Request.Path.Value)
                {
                    case "/oauth/authorize":
                        step!.Authorizations.Enqueue(url);
                        if (step.DenyNext)
                        {
                            step.DenyNext = false;
                            context.Response.Redirect(FakeAuthCenterServer.PublicOrigin +
                                FakeAuthCenterServer.CallbackErrorPath("access_denied", context.Request.Query["state"].ToString()));
                            break;
                        }

                        var result = authCenter.Authorize(new Uri(url), step.Subject);
                        context.Response.Redirect(FakeAuthCenterServer.PublicOrigin + FakeAuthCenterServer.CallbackPath(result.Code, result.State));
                        break;
                    case "/oauth/logout":
                        step!.EndSessionRequests.Enqueue(url);
                        context.Response.Redirect(FakeAuthCenterServer.PostLogoutRedirectUri);
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        break;
                }

                return Task.CompletedTask;
            });
        step = new AuthCenterInteractiveStep(app, port);
        return step;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PanelSignInPwaCollection : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "Panel sign-in";
}
