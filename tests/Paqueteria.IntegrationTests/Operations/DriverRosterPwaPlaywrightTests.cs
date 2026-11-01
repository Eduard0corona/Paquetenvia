using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for AI-07 <c>driver_roster</c>
/// (<c>/ops/drivers</c>, UI-PHASE3-DRIVERS-2026-10-11) in a real browser against the production Next build. The API
/// is fulfilled in the browser with AI-05 shapes (listOrganizationDrivers, /me/organization-contexts): the list shows
/// each OWN driver by its DRV- reference, vehicle, status (dot and text), active stops and the capture time of its
/// latest position in America/Mazatlan whatever the device zone, ordered by reference; "Cargar más" follows
/// next_cursor and a repeated cursor stops the paging; load, Actualizar, visibility and focus read the first page
/// again, never while hidden and never twice within 5 seconds; a 401 or 403 clears the rows; MFA_REQUIRED offers the
/// step-up; an organization switch clears the list; FINANCE, VIEWER and DRIVER are not offered the screen and never
/// read drivers (D5-CAPABILITY-MATRIX, x-capability-matrix driver_list_operations). The screen calls only AI-05
/// paths, keeps nothing in browser storage, logs nothing and renders no internal id. It shows no amount, so the
/// integer-cents rule has nothing to check here.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class DriverRosterPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    /// <summary>The roster's only data call and the shell's role read: every other API path is a failure.</summary>
    private static readonly string[] AllowedCalls =
    [
        "GET /api/v1/me/organization-contexts",
        "GET /api/v1/drivers",
    ];

    // The API pages by driver_id; the screen orders by reference. No id shares a fragment with a reference.
    private static readonly RosterDriver Active = new(
        "e1a00000-0000-4000-8000-0000000000a1", "DRV-7c2e9f10", "MOTORCYCLE", "ACTIVE", 3, "2026-10-12T17:04:05.1234567Z");

    private static readonly RosterDriver Pending = new(
        "e2b00000-0000-4000-8000-0000000000b2", "DRV-0b4d8e21", "BICYCLE", "PENDING", 0, null);

    /// <summary>Its last position is late on 4 October in Mazatlán, already 5 October in UTC and in Tokyo.</summary>
    private static readonly RosterDriver Suspended = new(
        "e3c00000-0000-4000-8000-0000000000c3", "DRV-3f9a1c55", "CAR", "SUSPENDED", 1, "2026-10-05T06:30:00Z");

    private static readonly RosterDriver OtherOrganization = new(
        "e4d00000-0000-4000-8000-0000000000d4", "DRV-5e6f7a8b", "VAN", "ACTIVE", 2, "2026-11-01T05:15:00Z");

    private static readonly string[] PendingRow =
        ["DRV-0b4d8e21", "Bicicleta", "Pendiente de activar", "Sin paradas", "Sin ubicación registrada"];

    private static readonly string[] SuspendedRow =
        ["DRV-3f9a1c55", "Automóvil", "Suspendido", "1 parada", "4 oct 2026, 23:30"];

    private static readonly string[] ActiveRow =
        ["DRV-7c2e9f10", "Motocicleta", "Activo", "3 paradas", "12 oct 2026, 10:04"];

    private static readonly string[] OtherOrganizationRow =
        ["DRV-5e6f7a8b", "Van", "Activo", "2 paradas", "31 oct 2026, 22:15"];

    /// <summary>The shell's items per role (nav-items.ts over D5-CAPABILITY-MATRIX), in display order.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> NavigationByRole = new Dictionary<string, string[]>
    {
        ["DISPATCHER"] =
        [
            "/ops/inbox", "/ops/orders", "/ops/dashboard", "/ops/orders/new", "/ops/routes", "/ops/incidents",
            "/ops/orders/import", "/ops/drivers", "/finance/cod",
        ],
        ["PLATFORM_ADMIN"] =
        [
            "/ops/inbox", "/ops/orders", "/ops/dashboard", "/ops/orders/new", "/ops/routes", "/ops/incidents",
            "/ops/orders/import", "/ops/drivers", "/finance/cod", "/finance/settlements",
        ],
        ["FINANCE"] = ["/finance/cod", "/finance/settlements"],
        ["VIEWER"] = ["/ops/orders", "/ops/dashboard", "/ops/routes"],
        ["DRIVER"] = [],
    };

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Dispatcher_reads_the_roster_by_reference_in_Mazatlan_time_and_pages_until_a_repeated_cursor()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale: the times must still read in Mazatlán, in es-MX.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var roster = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        roster.Pages[(OrganizationA, null)] = ([Active, Pending], "c1");
        roster.Pages[(OrganizationA, "c1")] = ([Suspended], "c2");
        // The last page hands back a cursor the list already followed.
        roster.Pages[(OrganizationA, "c2")] = ([], "c1");

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Repartidores", Exact = true }).WaitForAsync();
        await ExpectRowsAsync(page, PendingRow, ActiveRow);
        Assert.Equal(new[] { string.Empty }, roster.Queries(OrganizationA));
        await ExpectNavigationAsync(page, "DISPATCHER");
        Assert.Equal("page", await page.Locator("nav.appNav a[href=\"/ops/drivers\"]").GetAttributeAsync("aria-current"));

        // The five columns of AI-07, the state as a dot next to its text, and each time as its instant in Mazatlán.
        Assert.Equal(
            new[] { "Repartidor", "Vehículo", "Estado", "Paradas activas", "Última ubicación" },
            (await page.Locator("thead th").AllTextContentsAsync()).Select(text => text.Trim()));
        Assert.Equal(
            new[] { true, true },
            await page.Locator("tbody tr").EvaluateAllAsync<bool[]>(
                """
                rows => rows.map(row => {
                  const badge = row.cells[2].querySelector(".statusBadge");
                  return badge !== null &&
                    badge.querySelector(".statusDot")?.getAttribute("aria-hidden") === "true" &&
                    badge.querySelector(".statusBadgeLabel")?.textContent.trim().length > 0;
                })
                """));
        Assert.Equal(new[] { Active.LastPositionAt }, await TimeAttributesAsync(page.Locator("tbody")));
        await page.GetByText("Horarios mostrados en hora de Mazatlán.", new() { Exact = true }).WaitForAsync();
        await ExpectLastUpdatedInMazatlanAsync(page);
        Assert.Equal("Se muestran 2 repartidores; hay más por cargar.", await SummaryAsync(page));

        // "Cargar más" follows next_cursor and keeps the order by reference.
        var loadMore = page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true });
        await loadMore.ClickAsync();
        await ExpectRowsAsync(page, PendingRow, SuspendedRow, ActiveRow);
        Assert.Equal(new[] { string.Empty, "cursor=c1" }, roster.Queries(OrganizationA));
        Assert.Equal(
            new[] { Suspended.LastPositionAt, Active.LastPositionAt },
            await TimeAttributesAsync(page.Locator("tbody")));
        Assert.Equal("Se muestran 3 repartidores; hay más por cargar.", await SummaryAsync(page));

        // A cursor the list already followed stops the paging with the message; the rows stay.
        await loadMore.ClickAsync();
        await page.Locator("main").GetByRole(AriaRole.Alert)
            .GetByText("No fue posible cargar más repartidores. Actualiza la lista.", new() { Exact = true })
            .WaitForAsync();
        Assert.Equal(new[] { string.Empty, "cursor=c1", "cursor=c2" }, roster.Queries(OrganizationA));
        Assert.Equal(0, await loadMore.CountAsync());
        await ExpectRowsAsync(page, PendingRow, SuspendedRow, ActiveRow);
        Assert.Equal("Se muestran 3 repartidores.", await SummaryAsync(page));

        // "Actualizar" reads the first page again and replaces the list with it.
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });
        await refresh.ClickAsync();
        await ExpectRowsAsync(page, PendingRow, ActiveRow);
        Assert.Equal(new[] { string.Empty, "cursor=c1", "cursor=c2", string.Empty }, roster.Queries(OrganizationA));
        Assert.Equal(0, await page.Locator("main").GetByRole(AriaRole.Alert).CountAsync());
        Assert.Equal(1, await loadMore.CountAsync());

        // A failed read keeps the rows and says so; the next answer clears the message.
        roster.Answer = RosterAnswer.Unavailable;
        await refresh.ClickAsync();
        await page.Locator("main").GetByRole(AriaRole.Alert)
            .GetByText("No fue posible actualizar la lista de repartidores.", new() { Exact = true })
            .WaitForAsync();
        await ExpectRowsAsync(page, PendingRow, ActiveRow);
        roster.Answer = RosterAnswer.Ok;
        await refresh.ClickAsync();
        await page.Locator("main").GetByRole(AriaRole.Alert).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await ExpectRowsAsync(page, PendingRow, ActiveRow);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        Assert.All(roster.Reads, read => Assert.Equal(OrganizationA, read.Organization));
        trace.Passed();
    }

    /// <summary>
    /// AI-07 driver_roster recovery: the page becoming visible and window focus read the first page again, never while
    /// hidden and never twice within 5 seconds (the device cannot hide a headless page, so document.hidden is set the
    /// way a hidden tab reports it).
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Roster_reads_again_on_focus_and_visibility_never_while_hidden_nor_twice_within_5_seconds()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var roster = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        roster.Pages[(OrganizationA, null)] = ([Active, Pending], null);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        await ExpectRowsAsync(page, PendingRow, ActiveRow);
        Assert.Single(roster.Reads);
        var firstRead = roster.Reads[0].At;

        // Within 5 seconds of the load neither focus nor visibility reads again.
        await page.EvaluateAsync(FocusAndShowScript);
        await Task.Delay(700);
        Assert.Single(roster.Reads);

        // Past 5 seconds, a hidden page still reads nothing.
        await DelayUntilAsync(firstRead.AddSeconds(6));
        await page.EvaluateAsync(
            """
            () => {
              Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
              Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
              document.dispatchEvent(new Event("visibilitychange"));
              window.dispatchEvent(new Event("focus"));
            }
            """);
        await Task.Delay(700);
        Assert.Single(roster.Reads);

        // Visible again: one read of the first page; a focus right after it reads nothing more.
        await page.EvaluateAsync(
            """
            () => {
              delete document.hidden;
              delete document.visibilityState;
              document.dispatchEvent(new Event("visibilitychange"));
            }
            """);
        await WaitForAsync(() => roster.Reads.Count == 2, "the page becoming visible did not read the roster again");
        Assert.Equal(new[] { string.Empty, string.Empty }, roster.Queries(OrganizationA));
        await ExpectRowsAsync(page, PendingRow, ActiveRow);
        await page.EvaluateAsync(FocusAndShowScript);
        await Task.Delay(700);
        Assert.Equal(2, roster.Reads.Count);

        // "Actualizar" is the person's own read and is never held back.
        await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).ClickAsync();
        await WaitForAsync(() => roster.Reads.Count == 3, "Actualizar did not read the roster");
        await ExpectRowsAsync(page, PendingRow, ActiveRow);
        Assert.Empty(console);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_clears_the_roster_before_the_other_answers_and_a_403_clears_the_rows()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            TimezoneId = "Europe/Madrid",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var roster = await InstallAsync(
            page,
            (OrganizationA, OrganizationAName, "DISPATCHER"),
            (OrganizationB, OrganizationBName, "DISPATCHER"));
        roster.Pages[(OrganizationA, null)] = ([Active, Pending], null);
        roster.Pages[(OrganizationB, null)] = ([OtherOrganization], null);
        var releaseOrganizationB = roster.Hold(OrganizationB);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        await ExpectRowsAsync(page, PendingRow, ActiveRow);

        // The shell's own switcher: the previous organization's rows go before the other one answers.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationBName });
        await WaitForAsync(() => roster.Reads.Any(read => read.Organization == OrganizationB), "organization B was never read");
        Assert.Equal(0, await page.Locator("table").CountAsync());
        var pending = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(Active.Reference, pending, StringComparison.Ordinal);
        Assert.DoesNotContain(Pending.Reference, pending, StringComparison.Ordinal);
        await page.GetByText("Cargando repartidores…", new() { Exact = true }).WaitForAsync();
        releaseOrganizationB.TrySetResult();
        await ExpectRowsAsync(page, OtherOrganizationRow);
        Assert.Equal("Se muestra 1 repartidor.", await SummaryAsync(page));
        Assert.Equal(new[] { OtherOrganization.LastPositionAt }, await TimeAttributesAsync(page.Locator("tbody")));
        Assert.Equal(new[] { string.Empty }, roster.Queries(OrganizationB));
        // Still the drivers page: the role in B has it.
        Assert.EndsWith("/ops/drivers", page.Url, StringComparison.Ordinal);
        await AssertNoIdentifiersAsync(page);

        // The API takes the access away: the rows go and the screen says so.
        roster.Answer = RosterAnswer.Forbidden;
        await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await page.GetByText("Tu rol en la organización activa no consulta repartidores.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.Locator("table").CountAsync());
        Assert.DoesNotContain(
            OtherOrganization.Reference,
            await page.Locator("main").InnerTextAsync(),
            StringComparison.Ordinal);
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).IsDisabledAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        // Every read carries the organization selected when it was sent: A until the switch, B after it.
        var organizations = roster.Reads.Select(read => read.Organization).ToArray();
        var switched = Array.IndexOf(organizations, OrganizationB);
        Assert.True(switched > 0, string.Join(", ", organizations));
        Assert.All(organizations[..switched], organization => Assert.Equal(OrganizationA, organization));
        Assert.All(organizations[switched..], organization => Assert.Equal(OrganizationB, organization));
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Platform_admin_without_MFA_gets_the_step_up_and_a_401_clears_the_rows()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var roster = await InstallAsync(page, (OrganizationA, OrganizationAName, "PLATFORM_ADMIN"));
        roster.Answer = RosterAnswer.MfaRequired;

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        // Only the second factor is missing: nothing is shown and "Verificar identidad" returns to this screen.
        await page.GetByRole(AriaRole.Status)
            .GetByText("Esta acción requiere verificar tu identidad (MFA).", new() { Exact = false })
            .WaitForAsync();
        Assert.Equal(
            "/login?mfa=required&return_url=%2Fops%2Fdrivers",
            await page.GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true }).GetAttributeAsync("href"));
        Assert.Equal(0, await page.Locator("table").CountAsync());
        Assert.Equal(0, await page.GetByText("Sin repartidores", new() { Exact = true }).CountAsync());
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");

        // With the second factor verified, an empty fleet says so.
        roster.Answer = RosterAnswer.Ok;
        roster.Pages[(OrganizationA, null)] = ([], null);
        var refresh = page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });
        await refresh.ClickAsync();
        await page.GetByText("Sin repartidores", new() { Exact = true }).WaitForAsync();
        Assert.Equal(
            "Sin repartidores Tu flota todavía no tiene repartidores registrados.",
            Regex.Replace(await page.Locator("main .emptyState").InnerTextAsync(), @"\s+", " ").Trim());
        Assert.Equal(0, await page.GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true }).CountAsync());

        roster.Pages[(OrganizationA, null)] = ([Suspended], null);
        await refresh.ClickAsync();
        await ExpectRowsAsync(page, SuspendedRow);

        // A 401 clears the rows exactly like a 403.
        roster.Answer = RosterAnswer.Unauthorized;
        await refresh.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.Locator("table").CountAsync());
        Assert.DoesNotContain(Suspended.Reference, await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 driver_roster excluded_roles: FINANCE, VIEWER and DRIVER are not offered the screen; opened by its URL it
    /// shows "Acceso no disponible" and never calls listOrganizationDrivers.
    /// </summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("FINANCE")]
    [InlineData("VIEWER")]
    [InlineData("DRIVER")]
    public async Task Roles_outside_the_roster_are_not_offered_it_and_never_read_drivers(string role)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(
            context,
            role,
            PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var roster = await InstallAsync(page, (OrganizationA, OrganizationAName, role));
        roster.Pages[(OrganizationA, null)] = ([Active, Pending], null);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await page.GetByText("Tu rol en la organización activa no consulta repartidores.", new() { Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, role);
        await Task.Delay(500);
        Assert.Empty(roster.Reads);
        Assert.Equal(0, await page.Locator("table").CountAsync());
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).IsDisabledAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        Assert.DoesNotContain(calls.Api, call => call.Path == "/api/v1/drivers");
        trace.Passed();
    }

    private const string FocusAndShowScript =
        """
        () => {
          document.dispatchEvent(new Event("visibilitychange"));
          window.dispatchEvent(new Event("focus"));
        }
        """;

    /// <summary>The shell's items in order, or its note when the role has none.</summary>
    private static async Task ExpectNavigationAsync(IPage page, string role)
    {
        var expected = NavigationByRole[role];
        if (expected.Length == 0)
        {
            await page.GetByText("Tu rol en esta organización no tiene secciones en este panel.", new() { Exact = true })
                .WaitForAsync();
            Assert.Equal(0, await page.Locator("nav.appNav").CountAsync());
            return;
        }

        await page.Locator("nav.appNav").WaitForAsync();
        Assert.Equal(expected, await page.Locator("nav.appNav a").EvaluateAllAsync<string[]>(
            "links => links.map(link => link.getAttribute(\"href\"))"));
    }

    /// <summary>Each row's cells (reference, vehicle, status, active stops, latest location) in display order.</summary>
    private static async Task ExpectRowsAsync(IPage page, params string[][] rows)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string[][] actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("tbody tr").EvaluateAllAsync<string[][]>(
                "rows => rows.map(row => [...row.cells].map(cell => cell.innerText.trim()))");
            if (actual.Length == rows.Length && actual.Zip(rows).All(pair => pair.First.SequenceEqual(pair.Second)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            $"The roster did not match the API answer: {string.Join(" | ", actual.Select(row => string.Join(", ", row)))}; " +
            $"screen: {await page.Locator("main").InnerTextAsync()}");
    }

    private static async Task<string?[]> TimeAttributesAsync(ILocator scope) =>
        await scope.Locator("time").EvaluateAllAsync<string?[]>(
            "times => times.map(time => time.getAttribute(\"datetime\"))");

    private static async Task<string> SummaryAsync(IPage page) =>
        (await page.Locator("p.opsSummaryNote").InnerTextAsync()).Trim();

    /// <summary>
    /// "Última actualización" is the device clock's instant shown in Mazatlán time (UTC-7 all year since Mexico ended
    /// daylight saving time in 2022), whatever the device zone.
    /// </summary>
    private static async Task ExpectLastUpdatedInMazatlanAsync(IPage page)
    {
        var time = page.Locator(".pageActions time");
        await time.WaitForAsync();
        var instant = DateTimeOffset.Parse(
            (await time.GetAttributeAsync("datetime"))!,
            System.Globalization.CultureInfo.InvariantCulture);
        var mazatlan = instant.ToOffset(TimeSpan.FromHours(-7));
        var text = (await time.InnerTextAsync()).Trim();
        Assert.Matches(
            $"^{mazatlan.Day} [a-z]+\\.? {mazatlan.Year}, {mazatlan:HH}:{mazatlan:mm}$",
            text);
    }

    /// <summary>No internal id anywhere in the page, neither as text nor in the markup; only DRV- references.</summary>
    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        var markup = await page.Locator("main").EvaluateAsync<string>("main => main.outerHTML");
        foreach (var driver in new[] { Active, Pending, Suspended, OtherOrganization })
        {
            Assert.DoesNotContain(driver.Id, markup, StringComparison.Ordinal);
            Assert.DoesNotContain(driver.Id[..8], visible, StringComparison.Ordinal);
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

    /// <summary>Every API call is an AI-05 operation of this screen, and nothing goes to any third origin.</summary>
    private static void AssertOnlyAi05Calls(CallRecorder calls, Uri web)
    {
        Assert.All(calls.Api, call => Assert.Contains($"{call.Method} {call.Path}", AllowedCalls));
        var webOrigin = web.GetLeftPart(UriPartial.Authority) + "/";
        Assert.DoesNotContain(calls.Foreign, url => !url.StartsWith(webOrigin, StringComparison.Ordinal));
    }

    private static async Task DelayUntilAsync(DateTimeOffset instant)
    {
        var remaining = instant - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail(failure);
    }

    /// <summary>The application's own console output; resource errors of fulfilled 4xx/5xx answers are the browser's.</summary>
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

    /// <summary>
    /// Installs the in-memory operations session (Mock mode, as the other panel suites) with the shell's organization
    /// switch, and fulfils /me/organization-contexts and listOrganizationDrivers. Playwright runs the last matching route
    /// first, so the catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<RosterApi> InstallAsync(
        IPage page,
        params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-driver-roster-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", route => FulfillAsync(
            route,
            organizations.Select((organization, index) => new
            {
                organization_id = organization.Id,
                display_name = organization.Name,
                role = organization.Role,
                is_default = index == 0,
            }).ToArray()));
        var roster = new RosterApi();
        await page.RouteAsync("**/api/v1/drivers**", roster.HandleAsync);
        return roster;
    }

    private static Task FulfillAsync(IRoute route, object body) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
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

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();

    /// <summary>An AI-05 OrganizationDriver.</summary>
    private sealed record RosterDriver(
        string Id,
        string Reference,
        string Vehicle,
        string Status,
        int ActiveStops,
        string? LastPositionAt)
    {
        internal object ToJson() => new
        {
            driver_id = Id,
            driver_reference = Reference,
            vehicle_type = Vehicle,
            status = Status,
            active_stop_count = ActiveStops,
            last_position_at = LastPositionAt,
        };
    }

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    private enum RosterAnswer
    {
        Ok,
        Unavailable,
        Forbidden,
        Unauthorized,
        MfaRequired,
    }

    /// <summary>listOrganizationDrivers per organization and cursor; every read is recorded with its organization.</summary>
    private sealed class RosterApi
    {
        private readonly object _gate = new();
        private readonly List<RosterRead> _reads = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();

        internal ConcurrentDictionary<(string Organization, string? Cursor), (RosterDriver[] Items, string? Next)> Pages { get; } = new();

        internal RosterAnswer Answer { get; set; }

        internal IReadOnlyList<RosterRead> Reads
        {
            get
            {
                lock (_gate) return [.. _reads];
            }
        }

        internal string[] Queries(string organization) =>
            Reads.Where(read => read.Organization == organization).Select(read => read.Query).ToArray();

        /// <summary>Holds the organization's answers until the returned source is completed.</summary>
        internal TaskCompletionSource Hold(string organization) =>
            _holds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task HandleAsync(IRoute route)
        {
            var uri = new Uri(route.Request.Url);
            if (uri.AbsolutePath != "/api/v1/drivers" || route.Request.Method != "GET")
            {
                await ProblemAsync(route, 404);
                return;
            }

            var organization = route.Request.Headers.TryGetValue("x-organization-id", out var value) ? value : string.Empty;
            var query = uri.Query.TrimStart('?');
            lock (_gate) _reads.Add(new RosterRead(organization, query, DateTimeOffset.UtcNow));
            if (_holds.TryGetValue(organization, out var hold)) await hold.Task;

            switch (Answer)
            {
                case RosterAnswer.Unavailable:
                    await ProblemAsync(route, 503);
                    return;
                case RosterAnswer.Forbidden:
                    await ProblemAsync(route, 403);
                    return;
                case RosterAnswer.Unauthorized:
                    await ProblemAsync(route, 401);
                    return;
                case RosterAnswer.MfaRequired:
                    await ProblemAsync(route, 403, "MFA_REQUIRED");
                    return;
            }

            var cursor = System.Web.HttpUtility.ParseQueryString(uri.Query)["cursor"];
            if (!Pages.TryGetValue((organization, cursor), out var answer))
            {
                // AI-05: a cursor this operation did not issue is 409 INVALID_REQUEST.
                await ProblemAsync(route, 409, "INVALID_REQUEST");
                return;
            }

            await FulfillAsync(route, new
            {
                items = answer.Items.Select(driver => driver.ToJson()).ToArray(),
                next_cursor = answer.Next,
            });
        }
    }

    private sealed record RosterRead(string Organization, string Query, DateTimeOffset At);
}
