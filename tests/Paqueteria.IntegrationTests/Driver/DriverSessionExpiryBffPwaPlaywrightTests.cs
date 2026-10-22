using Microsoft.Playwright;
using static Paqueteria.IntegrationTests.Driver.DriverOfflineOperationsPwaPlaywrightTests;
using static Paqueteria.IntegrationTests.Driver.DriverStopsPwaPlaywrightTests;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// DRV-SESSION-EXPIRY-OFFLINE-2026-10-19 in the pilot's sign-in mode (AuthCenter BFF): owner answer E2 "Conservar y
/// pedir entrar (Recommended)" (a session that ends keeps the queue and the same driver sends it after signing in
/// again; nobody else sees it) and E3 "Sí, y envía al validar (Recommended)" (a reload without signal opens the saved
/// stops of the driver the phone remembers, and the queue is sent once the server confirms that same driver). The
/// browser plays the BFF and API on the page's own origin (<see cref="DriverBffSession"/>) and the synthetic order
/// backend of the DRV-002 suite.
/// </summary>
[Collection(DriverStopsBffPwaCollection.Name)]
public sealed class DriverSessionExpiryBffPwaPlaywrightTests(DriverStopsBffNextServerFixture server)
{
    private const string DriverA = "bff-expiry-driver-a-0001";
    private const string DriverB = "bff-expiry-driver-b-0001";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Session_expiry_mid_queue_keeps_everything_and_only_the_same_driver_sends_it(bool anotherPerson)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await DriverBffSession.LaunchAsync(playwright);
        await using var context = await DriverBffSession.NewContextAsync(browser);
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, $"anotherPerson={anotherPerson}");
        var page = await context.NewPageAsync();
        var bff = await BffOrigin.InstallAsync(page, server.BaseAddress, DriverA);

        await page.GotoAsync(new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");
        await ExpectTextAsync(page, "Llegué a recolección");

        // Offline at the door: the action is queued.
        await context.SetOfflineAsync(true);
        try
        {
            await EnqueueButtonAsync(page, "Llegué a recolección");
            await WaitForOperationCountAsync(page, 1);
        }
        finally
        {
            // Back online, the session has ended meanwhile (its fixed 480 minutes): every request answers 401.
            bff.SessionEnded = true;
            await context.SetOfflineAsync(false);
        }
        await ExpectTextAsync(page, "Tu sesión venció, entra de nuevo");
        await ExpectTextAsync(page, "Lo que tengas pendiente sigue guardado en este teléfono");
        Assert.True(bff.UnauthorizedAnswers > 0);
        Assert.Empty(bff.Backend.TransitionTargets);
        Assert.Equal(1, await ReadStoreCountAsync(page, "operations"));
        Assert.Equal(1, await ReadStoreCountAsync(page, "snapshots"));
        var queued = await ReadTransitionKeyAsync(page);

        // "Iniciar sesión" → /login → AuthCenter → back to the same stop, signed in again.
        var signIn = page.GetByRole(AriaRole.Link, new() { Name = "Iniciar sesión" });
        Assert.Equal(
            $"/login?return_url=%2Fdriver%2Fstops%2F{OrderId}",
            await signIn.GetAttributeAsync("href"));
        await signIn.ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter" }).WaitForAsync();
        bff.SignInOnAuthCenterAs(anotherPerson ? DriverB : DriverA);
        await page.GetByRole(AriaRole.Link, new() { Name = "Continuar con AuthCenter" }).ClickAsync();
        await page.WaitForURLAsync($"**/driver/stops/{OrderId}");
        await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");

        if (!anotherPerson)
        {
            // The same driver in the same organization: the same partition, sent by itself with the same key.
            await WaitForOperationCountAsync(page, 0);
            Assert.Equal(new[] { "AT_PICKUP" }, bff.Backend.TransitionTargets);
            Assert.Equal(new[] { queued }, bff.Backend.TransitionKeys);
            Assert.Equal(new[] { DriverBffSession.CsrfToken }, bff.TransitionCsrfTokens);
            Assert.Equal(DriverA, await ReadMarkerNamespaceAsync(page));
            trace.Passed();
            return;
        }

        // Another person: their own partition; the queue is neither shown nor sent nor wiped.
        await ExpectTextAsync(page, "Llegué a recolección");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(bff.Backend.TransitionTargets);
        Assert.Equal(1, await ReadStoreCountAsync(page, "operations"));
        var body = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Sincronización pendiente", body);
        Assert.DoesNotContain("Tu sesión venció", body);
        Assert.Equal(DriverB, await ReadMarkerNamespaceAsync(page));
        trace.Passed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Offline_reload_opens_the_remembered_stops_and_sends_only_after_the_server_confirms_the_driver(
        bool anotherPerson)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await DriverBffSession.LaunchAsync(playwright);
        await using var context = await DriverBffSession.NewContextAsync(browser);
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, $"anotherPerson={anotherPerson}");
        var page = await context.NewPageAsync();
        var bff = await BffOrigin.InstallAsync(page, server.BaseAddress, DriverA);

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");
        await WaitForSnapshotCountAsync(page, 1);
        await EnsureServiceWorkerControlAsync(page);
        await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");
        Assert.Equal(DriverA, await ReadMarkerNamespaceAsync(page));
        var marker = await page.EvaluateAsync<string>(
            "() => localStorage.getItem('paquetenvia:driver-session-marker') ?? ''");
        Assert.DoesNotContain(DriverBffSession.CsrfToken, marker);
        Assert.DoesNotContain("Repartidor", marker);

        // No signal, and the app is reloaded (reopened).
        bff.Reachable = false;
        await context.SetOfflineAsync(true);
        try
        {
            bff.ResetObservedRequests();
            await page.ReloadAsync();
            await ExpectTextAsync(page, "ORD_DRV002_SYNTHETIC");
            await ExpectTextAsync(page, "Sesión sin confirmar");
            await ExpectTextAsync(page, "Mostrando última información disponible");
            Assert.DoesNotContain("Sesión no disponible", await page.Locator("body").InnerTextAsync());

            await page.GetByRole(AriaRole.Link, new() { Name = "Ver detalle" }).First.ClickAsync();
            await page.WaitForURLAsync($"**/driver/stops/{OrderId}");
            await EnqueueButtonAsync(page, "Llegué a recolección");
            await WaitForOperationCountAsync(page, 1);
            await page.GetByRole(AriaRole.Button, new() { Name = "Sincronizar ahora" }).ClickAsync();
            await ExpectTextAsync(page, "Tus acciones se enviarán cuando haya conexión y el servidor confirme tu sesión.");
            // A remembered session asks the API and the hub for nothing; only the session is checked.
            Assert.DoesNotContain(bff.ObservedRequests, path => !path.StartsWith("/auth/", StringComparison.Ordinal));
        }
        finally
        {
            if (anotherPerson)
            {
                bff.SignedInAs = DriverB;
            }
            bff.Reachable = true;
            await context.SetOfflineAsync(false);
        }

        if (!anotherPerson)
        {
            // The connection returns and the server confirms the same driver: the queue is sent.
            await WaitForOperationCountAsync(page, 0);
            Assert.Equal(new[] { "AT_PICKUP" }, bff.Backend.TransitionTargets);
            Assert.Equal(new[] { DriverBffSession.CsrfToken }, bff.TransitionCsrfTokens);
            await page.GetByText("Sesión sin confirmar").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            trace.Passed();
            return;
        }

        // The server answers for another person: nothing of the remembered partition is shown or sent.
        await page.GetByText("Sesión sin confirmar").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await ExpectTextAsync(page, "Llegué a recolección");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(bff.Backend.TransitionTargets);
        Assert.Equal(1, await ReadStoreCountAsync(page, "operations"));
        Assert.DoesNotContain("Sincronización pendiente", await page.Locator("body").InnerTextAsync());
        Assert.Equal(DriverB, await ReadMarkerNamespaceAsync(page));
        trace.Passed();
    }

    /// <summary>
    /// Not covered by the owner's answer, so unchanged: a 403 (membership or driver profile revoked) still wipes the
    /// partition, and the phone stops remembering that driver.
    /// </summary>
    [Fact]
    [Trait("Category", "DriverOfflineOperationsPwa")]
    public async Task Forbidden_still_wipes_the_partition_and_forgets_the_driver()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await DriverBffSession.LaunchAsync(playwright);
        await using var context = await DriverBffSession.NewContextAsync(browser);
        await using var trace = await PlaywrightFailureTrace.StartAsync(context);
        var page = await context.NewPageAsync();
        var bff = await BffOrigin.InstallAsync(page, server.BaseAddress, DriverA);

        await page.GotoAsync(new Uri(server.BaseAddress, $"/driver/stops/{OrderId}").AbsoluteUri);
        await ExpectTextAsync(page, "Llegué a recolección");
        Assert.Equal(DriverA, await ReadMarkerNamespaceAsync(page));
        await context.SetOfflineAsync(true);
        try
        {
            await EnqueueButtonAsync(page, "Llegué a recolección");
            await WaitForOperationCountAsync(page, 1);
        }
        finally
        {
            bff.Backend.StopsFailureStatus = 403;
            await context.SetOfflineAsync(false);
        }

        await ExpectTextAsync(page, "Acceso no disponible");
        await WaitForOperationCountAsync(page, 0);
        Assert.Equal(0, await ReadStoreCountAsync(page, "snapshots"));
        Assert.Empty(bff.Backend.TransitionTargets);
        Assert.Equal(string.Empty, await ReadMarkerNamespaceAsync(page));
        trace.Passed();
    }

    private static Task<string> ReadTransitionKeyAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("operations", "readonly").objectStore("operations").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(request.result[0]?.transitionIdempotencyKey ?? "");
                };
              };
            })
            """);

    private static Task<string> ReadMarkerNamespaceAsync(IPage page) =>
        page.EvaluateAsync<string>(
            """
            () => {
              const raw = localStorage.getItem("paquetenvia:driver-session-marker");
              return raw ? JSON.parse(raw).sessionNamespace : "";
            }
            """);

    /// <summary>
    /// The page's own origin as the pilot routes it: the BFF (<c>/auth/session</c>, <c>/auth/login</c> playing
    /// AuthCenter), the organization contexts and the synthetic API of the DRV-002 suite. <see cref="SessionEnded"/>
    /// answers every API and session request with 401; <see cref="Reachable"/> false fails them as without signal.
    /// </summary>
    private sealed class BffOrigin
    {
        private readonly object gate = new();
        private readonly List<string> observedRequests = [];
        private readonly List<string> transitionCsrfTokens = [];
        private int unauthorizedAnswers;

        private BffOrigin(string person)
        {
            signedInAs = person;
        }

        private bool sessionEnded;
        private bool reachable = true;
        private string? signedInAs;
        private string? pendingSignIn;

        internal SyntheticOfflineBackend Backend { get; } = new();

        internal bool SessionEnded
        {
            get { lock (gate) return sessionEnded; }
            set { lock (gate) sessionEnded = value; }
        }

        internal bool Reachable
        {
            get { lock (gate) return reachable; }
            set { lock (gate) reachable = value; }
        }

        internal string? SignedInAs
        {
            get { lock (gate) return signedInAs; }
            set { lock (gate) signedInAs = value; }
        }
        internal int UnauthorizedAnswers { get { lock (gate) return unauthorizedAnswers; } }
        internal IReadOnlyList<string> ObservedRequests { get { lock (gate) return [.. observedRequests]; } }
        internal IReadOnlyList<string> TransitionCsrfTokens { get { lock (gate) return [.. transitionCsrfTokens]; } }

        internal void ResetObservedRequests()
        {
            lock (gate) observedRequests.Clear();
        }

        /// <summary>The next <c>/auth/login</c> plays a successful AuthCenter sign-in as this person.</summary>
        internal void SignInOnAuthCenterAs(string sessionNamespace)
        {
            lock (gate) pendingSignIn = sessionNamespace;
        }

        internal static async Task<BffOrigin> InstallAsync(IPage page, Uri origin, string signedInAs)
        {
            var bff = new BffOrigin(signedInAs);
            var authority = origin.GetLeftPart(UriPartial.Authority);
            await DriverBffSession.BlockUnfulfilledAsync(page, origin);
            await bff.Backend.RouteAsync(page);
            await DriverOrganizationRoles.RouteAsync(page);
            await DriverBffSession.RouteOrganizationContextsAsync(page);
            await page.RouteAsync(DriverBffSession.SessionRoute, bff.RouteSessionAsync);
            await page.RouteAsync($"{authority}/auth/login**", bff.RouteLoginAsync);
            // Registered last, so it sees every API request first and lets the backend answer when it may.
            await page.RouteAsync($"{authority}/api/**", bff.GateApiAsync);
            await page.RouteAsync($"{authority}/hubs/**", bff.GateHubAsync);
            return bff;
        }

        private async Task RouteSessionAsync(IRoute route)
        {
            Observe(route.Request);
            if (!Reachable)
            {
                await route.AbortAsync("internetdisconnected");
                return;
            }
            var person = SessionEnded ? null : SignedInAs;
            if (person is null)
            {
                lock (gate) unauthorizedAnswers += 1;
                await route.FulfillAsync(new RouteFulfillOptions { Status = 401 });
                return;
            }
            await route.FulfillAsync(DriverBffSession.Json(DriverBffSession.SessionBody(person)));
        }

        private async Task RouteLoginAsync(IRoute route)
        {
            Observe(route.Request);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(route.Request.Url).Query);
            var returnUrl = query["return_url"].ToString();
            Assert.StartsWith("/driver/stops", returnUrl, StringComparison.Ordinal);
            lock (gate)
            {
                signedInAs = pendingSignIn ?? throw new InvalidOperationException("No sign-in was expected.");
                pendingSignIn = null;
                sessionEnded = false;
            }
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 302,
                Headers = new Dictionary<string, string> { ["Location"] = returnUrl },
            });
        }

        private async Task GateApiAsync(IRoute route)
        {
            Observe(route.Request);
            if (!Reachable)
            {
                await route.AbortAsync("internetdisconnected");
                return;
            }
            if (SessionEnded)
            {
                lock (gate) unauthorizedAnswers += 1;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 401,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Unauthorized","status":401}""",
                });
                return;
            }
            if (route.Request.Method == "POST" && route.Request.Url.EndsWith("/transitions", StringComparison.Ordinal))
            {
                var csrf = await route.Request.HeaderValueAsync("X-AuthCenter-CSRF") ?? string.Empty;
                lock (gate) transitionCsrfTokens.Add(csrf);
            }
            await route.FallbackAsync();
        }

        private async Task GateHubAsync(IRoute route)
        {
            Observe(route.Request);
            await route.AbortAsync();
        }

        private void Observe(IRequest request)
        {
            lock (gate) observedRequests.Add(new Uri(request.Url).AbsolutePath);
        }
    }
}
