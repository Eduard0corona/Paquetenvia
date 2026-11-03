using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for AI-07 <c>work_inbox</c> (<c>/ops/inbox</c>,
/// UI-PHASE3-INBOX-2026-10-10, UI-PHASE3-INBOX-TOTAL-2026-10-10, UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11,
/// SEC-004-RELOADS-AND-BULK-2026-10-13, SEC-004-PANEL-SCREENS-2026-10-13): the list and the single assignment (B5a; the
/// bulk actions are B5b), in a real browser against the production Next build. The API is fulfilled in the browser:
/// the operations dashboard read model (AI-07 work_inbox.sources.list: OBS-001, ADR-OBS-001, not declared in AI-05)
/// with its own filters, and the AI-05 shapes of getOperationsQueueCounts (no query string), listAssignableDrivers,
/// assignDriver and /me/organization-contexts; the OperationsHub is a mocked SignalR WebSocket. Each queue is its own
/// server query (one per status where AI-07 says so) merged newest first by updated_at, its tab shows the server count,
/// the chips are server filters kept in the URL, "Precio por revisar" fails closed on a row outside its rule and keeps
/// the rows shown, "Cargar más" follows the cursor of a one-query queue; "Asignar" uses the order detail's picker with
/// integer cents and its confirmation, and the inbox and the counts are read again; a 429 on the counts or on the list
/// pauses the whole inbox with one message and reads once at 0 with the view on screen; a 403 mid-session clears the
/// rows; realtime notices give one reload at most every 3 seconds, and a repeated event or a location update reads
/// nothing; a switch from a page DISPATCHER cannot use lands it on the inbox, and a switch clears rows, zones, counts,
/// the selection and the open assignment before the next organization answers; for VIEWER, FINANCE, DRIVER and
/// PLATFORM_ADMIN without MFA the API answers 403 and no order, count or zone is shown. Nothing is kept in browser
/// storage or logged, no internal id is shown, every amount comes from integer cents and every time is in
/// America/Mazatlan whatever the device zone.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class WorkInboxPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationC = "33333333-3333-3333-3333-333333333333";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";
    private const string OrganizationCName = "Paquetería Sintética Sur";

    private const string ZoneCentro = "5a000000-0000-4000-8000-000000000001";
    private const string ZoneNorte = "5a000000-0000-4000-8000-000000000002";

    private const string DriverMoto = "d1000000-0000-4000-8000-000000000001";
    private const string DriverCar = "d1000000-0000-4000-8000-000000000002";
    private const string DriverVan = "d1000000-0000-4000-8000-000000000003";

    /// <summary>Generated at 06:30 UTC on 2 November: 23:30 on 1 November in Mazatlán, 15:30 on 2 November in Tokyo.</summary>
    private const string GeneratedAt = "2026-11-02T06:30:00Z";

    private static readonly InboxOrder Ready = new(
        "a1000000-0000-4000-8000-000000000001", "ORD_BandejaSintetica000001", "READY_FOR_PICKUP", "SAME_DAY",
        ZoneCentro, ("2026-11-02T17:00:00Z", "2026-11-02T19:00:00Z"), null, 100_113, null, true, "2026-11-02T06:20:00Z");

    private static readonly InboxOrder Rescheduled = new(
        "a1000000-0000-4000-8000-000000000002", "ORD_BandejaSintetica000002", "RESCHEDULED", "URGENT",
        ZoneNorte, null, null, 25_000, "BELOW_MINIMUM_SNAPSHOT", true, "2026-11-02T06:10:00Z");

    /// <summary>Delivery window 22:30–23:30 on 2 November in Mazatlán, already 3 November in Tokyo.</summary>
    private static readonly InboxOrder FailedAttempt = new(
        "a1000000-0000-4000-8000-000000000003", "ORD_BandejaSintetica000003", "FAILED_ATTEMPT", "SAME_DAY",
        ZoneCentro, ("2026-11-03T05:30:00Z", "2026-11-03T06:30:00Z"), "DRV-7c2e9f10", 18_050, null, false,
        "2026-11-02T06:25:00Z");

    /// <summary>The newest row of "Requiere atención", though its status query comes last.</summary>
    private static readonly InboxOrder Returning = new(
        "a1000000-0000-4000-8000-000000000004", "ORD_BandejaSintetica000004", "RETURNING", "SCHEDULED_ROUTE",
        null, null, "DRV-0b1c2d3e", 5_000, null, false, "2026-11-02T06:28:00Z");

    private static readonly InboxOrder OrderOfC = new(
        "c1000000-0000-4000-8000-000000000001", "ORD_BandejaSinteticaSur01", "READY_FOR_PICKUP", "SAME_DAY",
        ZoneCentro, null, null, 9_900, null, true, "2026-11-02T06:05:00Z");

    private static readonly string[] ReadyRow =
        [Ready.PublicId, "Lista para recolección", "Centro", "02-nov, 10:00 a 02-nov, 12:00", "Sin repartidor", "$1,001.13 MXN", "seleccionable", "Asignar"];

    private static readonly string[] RescheduledRow =
        [Rescheduled.PublicId, "Reprogramada + Precio por revisar", "Norte", "Horario de la zona", "Sin repartidor", "$250.00 MXN", "seleccionable", "Asignar"];

    private static readonly string[] FailedAttemptRow =
        [FailedAttempt.PublicId, "Intento fallido", "Centro", "02-nov, 22:30 a 02-nov, 23:30", "DRV-7c2e9f10", "$180.50 MXN", "-", "-"];

    private static readonly string[] ReturningRow =
        [Returning.PublicId, "En devolución", "Sin zona asignada", "Horario de la zona", "DRV-0b1c2d3e", "$50.00 MXN", "-", "-"];

    /// <summary>The list source AI-07 declares, the AI-05 operations of the screen and the shell's role read.</summary>
    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/operations/dashboard$"),
        new("^GET /api/v1/operations/queue-counts$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/assignable-drivers$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/assignments$"),
        new("^POST /hubs/operations/negotiate$"),
    ];

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
    public async Task Dispatcher_sees_server_counts_and_each_queue_through_its_own_server_filters_and_cursors()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale: times still read in Mazatlán and amounts in es-MX pesos.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, realtime: false, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, Ready, Rescheduled, FailedAttempt, Returning);

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        Assert.NotNull(response);
        // /ops is never stored (next.config.ts privateNoStore).
        Assert.Contains("no-store", (await response.AllHeadersAsync())["cache-control"], StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Bandeja de trabajo", Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, "DISPATCHER");
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        await AssertNoIdentifiersAsync(page);
        Assert.Equal(new[] { "unassigned=true" }, api.TakeQueries(OrganizationA));
        await ExpectTabsAsync(page, "Sin asignar", "Sin asignar 2 órdenes", "Requiere atención 3 órdenes",
            "Precio por revisar 1 orden", "Entregadas sin cerrar 0 órdenes", "En ruta 0 órdenes");
        await ExpectTextAsync(page, "Totales de todas las órdenes de la organización, sin aplicar filtros.");
        await ExpectTextAsync(page, "Horarios mostrados en hora de Mazatlán.");
        Assert.Equal(
            "Sin asignar: Órdenes listas para recolección o reprogramadas que todavía no tienen repartidor.",
            (await page.Locator("#inbox-caption").InnerTextAsync()).Trim());
        Assert.Equal(
            new[] { "Seleccionar todas", "Guía", "Estado", "Destino (zona)", "Ventana de entrega", "Repartidor", "Total (IVA incluido)", "Acciones" },
            await page.Locator("table.opsInboxTable thead th").EvaluateAllAsync<string[]>(
                "cells => cells.map(cell => cell.textContent.replace(/\\s+/g, ' ').trim())"));
        Assert.Contains("Última actualización: 1 nov 2026, 23:30", await page.Locator("main header").InnerTextAsync(), StringComparison.Ordinal);
        await ExpectFooterAsync(page, "Mostrando 2 de 2 sin asignar · seleccionadas: 0");
        Assert.Equal(
            $"/ops/orders/{Ready.Id}?inbox=queue%3Dunassigned",
            await page.Locator("table.opsInboxTable a.opsInboxGuide").First.GetAttributeAsync("href"));
        Assert.Equal(
            "/ops/dashboard?view=positions",
            await page.GetByRole(AriaRole.Link, new() { Name = "Mapa de posiciones", Exact = true }).GetAttributeAsync("href"));
        Assert.Equal(1, api.CountReads(OrganizationA));

        // A status chip is a server filter kept in the URL; the counts never apply it.
        await Chip(page, "Estado", "Reprogramada").ClickAsync();
        await ExpectRowsAsync(page, RescheduledRow);
        Assert.Equal(new[] { "status=RESCHEDULED&unassigned=true" }, api.TakeQueries(OrganizationA));
        Assert.EndsWith("/ops/inbox?queue=unassigned&status=RESCHEDULED", page.Url, StringComparison.Ordinal);
        await ExpectFooterAsync(page, "Mostrando 1 sin asignar con los filtros elegidos · seleccionadas: 0");
        await ExpectTabsAsync(page, "Sin asignar", "Sin asignar 2 órdenes", "Requiere atención 3 órdenes",
            "Precio por revisar 1 orden", "Entregadas sin cerrar 0 órdenes", "En ruta 0 órdenes");
        await page.GetByRole(AriaRole.Button, new() { Name = "Limpiar filtros", Exact = true }).ClickAsync();
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        Assert.Equal(new[] { "unassigned=true" }, api.TakeQueries(OrganizationA));

        // "Requiere atención": one query per status, merged newest first by updated_at (Returning, last of the queries,
        // is the newest row); only flagged rows can be assigned.
        await Tab(page, "Requiere atención").ClickAsync();
        await ExpectRowsAsync(page, ReturningRow, FailedAttemptRow, RescheduledRow);
        await AssertNoIdentifiersAsync(page);
        Assert.Equal(
            new[] { "status=CLAIM_OPEN", "status=FAILED_ATTEMPT", "status=RESCHEDULED", "status=RETURNING" },
            api.TakeQueries(OrganizationA).Order(StringComparer.Ordinal));
        Assert.EndsWith("/ops/inbox?queue=needs_attention", page.Url, StringComparison.Ordinal);
        await ExpectFooterAsync(page, "Mostrando 3 de 3 que requieren atención · seleccionadas: 0");
        await Chip(page, "Servicio", "Urgente").ClickAsync();
        await ExpectRowsAsync(page, RescheduledRow);
        Assert.Equal(
            new[]
            {
                "status=CLAIM_OPEN&service_type=URGENT", "status=FAILED_ATTEMPT&service_type=URGENT",
                "status=RESCHEDULED&service_type=URGENT", "status=RETURNING&service_type=URGENT",
            },
            api.TakeQueries(OrganizationA).Order(StringComparer.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Limpiar filtros", Exact = true }).ClickAsync();
        await ExpectRowsAsync(page, ReturningRow, FailedAttemptRow, RescheduledRow);
        api.TakeQueries(OrganizationA);
        // The zone chips name the zones of the loaded rows; a zone is sent by its id and shown by its name.
        Assert.Equal(new[] { "Todas", "Centro", "Norte" }, await ChipsAsync(page, "Zona de entrega"));
        await Chip(page, "Zona de entrega", "Centro").ClickAsync();
        await ExpectRowsAsync(page, FailedAttemptRow);
        Assert.Equal(
            new[]
            {
                $"status=CLAIM_OPEN&delivery_zone_id={ZoneCentro}", $"status=FAILED_ATTEMPT&delivery_zone_id={ZoneCentro}",
                $"status=RESCHEDULED&delivery_zone_id={ZoneCentro}", $"status=RETURNING&delivery_zone_id={ZoneCentro}",
            },
            api.TakeQueries(OrganizationA).Order(StringComparer.Ordinal));
        Assert.EndsWith($"/ops/inbox?queue=needs_attention&zone={ZoneCentro}", page.Url, StringComparison.Ordinal);

        // "Precio por revisar": one price_review query, no status chip; the tab keeps the other chips.
        await Tab(page, "Precio por revisar").ClickAsync();
        await ExpectEmptyAsync(page, "No hay órdenes activas con precio por revisar.", "Prueba con otros filtros o límpialos.");
        Assert.Equal(new[] { $"delivery_zone_id={ZoneCentro}&price_review=true" }, api.TakeQueries(OrganizationA));
        Assert.Equal(0, await page.Locator("fieldset.opsChipGroup legend:text-is('Estado')").CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Limpiar filtros", Exact = true }).ClickAsync();
        await ExpectRowsAsync(page, RescheduledRow);
        Assert.Equal(new[] { "price_review=true" }, api.TakeQueries(OrganizationA));
        await ExpectFooterAsync(page, "Mostrando 1 de 1 con precio por revisar · seleccionadas: 0");
        // A row outside the rule fails the read closed; the rows shown stay.
        api.PriceReviewOutsideRule = true;
        await RefreshButton(page).ClickAsync();
        await page.Locator("main p.notice[role=alert]").GetByText("No fue posible actualizar la bandeja.", new() { Exact = true }).WaitForAsync();
        await ExpectRowsAsync(page, RescheduledRow);
        api.PriceReviewOutsideRule = false;

        // "Cargar más" follows the queue's cursor and appends the next page.
        api.PageSize = 1;
        await Tab(page, "Sin asignar").ClickAsync();
        await ExpectRowsAsync(page, ReadyRow);
        await ExpectFooterAsync(page, "Mostrando 1 de 2 sin asignar · seleccionadas: 0");
        await LoadMoreButton(page).ClickAsync();
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        Assert.Equal(0, await LoadMoreButton(page).CountAsync());
        Assert.Equal(new[] { "price_review=true", "unassigned=true", "unassigned=true&cursor=c1" }, api.TakeQueries(OrganizationA));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 work_inbox.actions.assign_own: "Asignar" only on rows with unassigned_alert, the order detail's picker with
    /// the same list, integer cents and the confirmation naming driver and cost; the inbox and the counts are read again.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Assign_uses_the_order_picker_in_integer_cents_and_reads_the_inbox_and_counts_again()
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
        var api = await InstallAsync(page, realtime: false, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, Ready, Rescheduled, FailedAttempt, Returning);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        await AssignButton(page, Ready).ClickAsync();
        var panel = page.Locator($"section[aria-label=\"Asignar repartidor a la orden {Ready.PublicId}\"]");
        await panel.WaitForAsync();
        Assert.Equal("true", await AssignButton(page, Ready).GetAttributeAsync("aria-expanded"));
        Assert.Equal(
            $"/ops/orders/{Ready.Id}?inbox=queue%3Dunassigned",
            await panel.GetByRole(AriaRole.Link, new() { Name = "Abrir orden", Exact = true }).GetAttributeAsync("href"));
        var choices = panel.Locator("ul.opsDriverChoices > li");
        await choices.Nth(2).WaitForAsync();
        Assert.Equal(
            new[]
            {
                "DRV-1a2b3c4d · Motocicleta · sin entregas en curso Disponible para esta orden",
                "DRV-5e6f7a8b · Automóvil · 2 entregas en curso Disponible para esta orden",
                "DRV-9c0d1e2f · Van · 1 entrega en curso No disponible: Tiene un documento vencido",
            },
            await choices.EvaluateAllAsync<string[]>("items => items.map(item => item.innerText.replace(/\\s+/g, ' ').trim())"));
        Assert.True(await panel.Locator($"input[type=radio][value=\"{DriverVan}\"]").IsDisabledAsync());
        Assert.Equal(new[] { $"/api/v1/orders/{Ready.Id}/assignable-drivers" }, api.DriverReads(OrganizationA));
        await AssertNoIdentifiersAsync(page);

        // A cost that is not pesos with up to two decimals (a decimal comma here) is refused before any request.
        await panel.Locator($"input[type=radio][value=\"{DriverCar}\"]").CheckAsync();
        await panel.GetByLabel("Costo de la asignación (MXN)").FillAsync("19,99");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Asignar repartidor", Exact = true }).ClickAsync();
        await panel.Locator("p.fieldError").GetByText("Escribe un costo válido en pesos, por ejemplo 45.00.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await OpenDialog(page).CountAsync());

        // 19.99 × 100 is 1998.9999999999998 in binary floating point, so a float parse that truncates would send 1998;
        // the value also differs from every row total, so only the typed cost can produce 1999.
        await panel.GetByLabel("Costo de la asignación (MXN)").FillAsync("19.99");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Asignar repartidor", Exact = true }).ClickAsync();
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal("¿Asignar repartidor?", (await dialog.Locator("h2").InnerTextAsync()).Trim());
        Assert.Equal(
            $"La orden {Ready.PublicId} se asignará al repartidor DRV-5e6f7a8b (automóvil) con un costo de $19.99 MXN.",
            (await dialog.Locator("p").InnerTextAsync()).Trim());
        var countReadsBefore = api.CountReads(OrganizationA);
        api.TakeQueries(OrganizationA);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Asignar", Exact = true }).ClickAsync();
        await panel.GetByText("Repartidor DRV-5e6f7a8b asignado.", new() { Exact = true }).WaitForAsync();
        var assignment = Assert.Single(api.Assignments(OrganizationA));
        Assert.Equal($"/api/v1/orders/{Ready.Id}/assignments", assignment.Path);
        Assert.Matches(AssignmentKeyPattern(), assignment.IdempotencyKey ?? string.Empty);
        Assert.Equal(
            JsonSerializer.Serialize(new { driver_id = DriverCar, assignment_type = "OWN", cost_cents = 1_999, route_id = (string?)null }),
            assignment.Body);
        Assert.Contains("\"cost_cents\":1999,", assignment.Body, StringComparison.Ordinal);

        // The inbox and its counts are read again: the order left "Sin asignar".
        await ExpectRowsAsync(page, RescheduledRow);
        Assert.Equal(new[] { "unassigned=true" }, api.TakeQueries(OrganizationA));
        await WaitForAsync(() => api.CountReads(OrganizationA) > countReadsBefore, "the counts were not read again");
        await ExpectTabsAsync(page, "Sin asignar", "Sin asignar 1 orden", "Requiere atención 3 órdenes",
            "Precio por revisar 1 orden", "Entregadas sin cerrar 0 órdenes", "En ruta 0 órdenes");

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// SEC-004-PANEL-SCREENS-2026-10-13 (Q11, Q14, Q16), AI-07 work_inbox.rate_limit and panel_rate_limit
    /// self_refreshing_screens: a 429 (Retry-After: 6) on the counts or on the list keeps the counts and rows shown and
    /// pauses the whole inbox with one message, announced once; "Actualizar" and "Cargar más" are disabled; a queue
    /// change is kept on screen and not sent; at 0 the inbox reads once, by itself, the queue on screen and the counts,
    /// and never again by itself.
    /// </summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("queue-counts")]
    [InlineData("dashboard")]
    public async Task A_429_pauses_the_whole_inbox_and_reads_once_at_zero_with_the_queue_on_screen(string limited)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, limited, PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, realtime: false, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, Ready, Rescheduled, FailedAttempt, Returning);
        api.PageSize = 1;
        var countdown = page.Locator("main").GetByRole(AriaRole.Timer);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectRowsAsync(page, ReadyRow);
        await WaitForAsync(() => api.CountReads(OrganizationA) == 1, "the counts were not read");
        api.TakeQueries(OrganizationA);
        if (limited == "dashboard") api.DashboardTooManyRequests = 1;
        else api.CountsTooManyRequests = 1;
        await RefreshButton(page).ClickAsync();
        // The visible countdown moves on its own; only the announcer, written once, holds the server's exact wait.
        await countdown.GetByText(TooManyRequestsPattern()).WaitForAsync();
        Assert.Equal(TooManyRequestsText(6), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.Null(await countdown.GetAttributeAsync("aria-live"));
        Assert.Single(api.TakeQueries(OrganizationA));
        // The counts are read after a list read: a 429 on the list leaves them unread until the wait ends.
        var countsDuringWait = limited == "dashboard" ? 1 : 2;
        Assert.Equal(countsDuringWait, api.CountReads(OrganizationA));
        // The counts and the rows shown stay; the buttons that read wait.
        await ExpectRowsAsync(page, ReadyRow);
        await ExpectTabsAsync(page, "Sin asignar", "Sin asignar 2 órdenes", "Requiere atención 3 órdenes",
            "Precio por revisar 1 orden", "Entregadas sin cerrar 0 órdenes", "En ruta 0 órdenes");
        Assert.True(await RefreshButton(page).IsDisabledAsync());
        Assert.True(await LoadMoreButton(page).IsDisabledAsync());

        // A queue change is kept on screen without being sent.
        await Tab(page, "En ruta").ClickAsync();
        await WaitForAsync(() => page.Url.EndsWith("/ops/inbox?queue=en_route", StringComparison.Ordinal), "the queue did not change");
        Assert.Equal("page", await Tab(page, "En ruta").GetAttributeAsync("aria-current"));
        await Task.Delay(500);
        Assert.Empty(api.TakeQueries(OrganizationA));
        Assert.Equal(countsDuringWait, api.CountReads(OrganizationA));

        // At 0 the inbox reads once, by itself, the queue on screen and the counts; the message goes.
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });
        await ExpectEmptyAsync(page, "No hay órdenes en ruta.");
        await WaitForAsync(() => api.CountReads(OrganizationA) == countsDuringWait + 1, "the counts were not read at 0");
        Assert.Equal(new[] { "status=DELIVERING", "status=IN_TRANSIT" }, api.TakeQueries(OrganizationA).Order(StringComparer.Ordinal));
        Assert.False(await RefreshButton(page).IsDisabledAsync());
        await Task.Delay(2_500);
        Assert.Empty(api.TakeQueries(OrganizationA));
        Assert.Equal(countsDuringWait + 1, api.CountReads(OrganizationA));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// SEC-004-RELOADS-AND-BULK-2026-10-13 Q9, AI-07 panel_rate_limit.realtime_reloads: OperationsHub notices are gathered
    /// into at most one reload every 3 seconds, no sooner than 3 seconds after the previous reload of any kind (checked
    /// with a 2.5 s floor measured where the requests arrive); a lone repeated event and a location update read nothing.
    /// Each reload reads the queue and the counts again.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Realtime_notices_give_one_reload_at_most_every_three_seconds()
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
        var api = await InstallAsync(page, realtime: true, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, Ready, Rescheduled, FailedAttempt, Returning);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        await page.Locator("main .opsConnection").GetByText("Conectada", new() { Exact = true }).WaitForAsync();
        Assert.Equal(OrganizationA, api.HubOrganization);
        Assert.Single(api.ListReadTimes(OrganizationA));

        // Three notices at once: one reload, no sooner than the spacing after the first read.
        api.SendNotice("OrderStatusChanged", Ready.Id, 4, "e1000000-0000-4000-8000-000000000001");
        api.SendNotice("AssignmentChanged", Rescheduled.Id, 2, "e1000000-0000-4000-8000-000000000002");
        api.SendNotice("IncidentCreated", FailedAttempt.Id, 5, "e1000000-0000-4000-8000-000000000003");
        await WaitForAsync(() => api.ListReadTimes(OrganizationA).Length == 2, "the notices did not reload the inbox");
        var times = api.ListReadTimes(OrganizationA);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(2_500), $"reloaded {(times[1] - times[0]).TotalMilliseconds} ms after the first read");
        await WaitForAsync(() => api.CountReads(OrganizationA) == 2, "the counts were not read with the reload");

        // A notice right after that reload waits for the spacing from it.
        api.SendNotice("OrderStatusChanged", Rescheduled.Id, 3, "e1000000-0000-4000-8000-000000000004");
        await WaitForAsync(() => api.ListReadTimes(OrganizationA).Length == 3, "the next notice did not reload the inbox");
        times = api.ListReadTimes(OrganizationA);
        Assert.True(times[2] - times[1] >= TimeSpan.FromMilliseconds(2_500), $"reloaded {(times[2] - times[1]).TotalMilliseconds} ms after the previous read");
        await WaitForAsync(() => api.CountReads(OrganizationA) == 3, "the counts were not read with the reload");

        // Long after it, a lone repeat of an applied event (same event_id) and a location update read nothing.
        api.SendNotice("OrderStatusChanged", Ready.Id, 4, "e1000000-0000-4000-8000-000000000001");
        api.SendNotice("DriverLocationUpdated", DriverCar, 9, "e1000000-0000-4000-8000-000000000005");
        await Task.Delay(3_500);
        Assert.Equal(3, api.ListReadTimes(OrganizationA).Length);
        Assert.Equal(3, api.CountReads(OrganizationA));
        Assert.All(api.TakeQueries(OrganizationA), query => Assert.Equal("unassigned=true", query));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 work_inbox.recovery "a 401 or 403 clears the rows": access withdrawn mid-session (a plain 403 on the next
    /// read) drops the rows, the counts and the zone chips and shows the access state; nothing is read by itself after.
    /// The realtime hub is left unreachable here (see the realtime test), so its stop is not observed.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task A_403_mid_session_clears_the_rows_counts_and_zones()
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
        var api = await InstallAsync(page, realtime: false, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, Ready, Rescheduled, FailedAttempt, Returning);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        await WaitForAsync(() => api.CountReads(OrganizationA) == 1, "the counts were not read");
        Assert.Equal(new[] { "Todas", "Centro", "Norte" }, await ChipsAsync(page, "Zona de entrega"));

        api.Forbidden = true;
        await RefreshButton(page).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await ExpectTextAsync(page, "No es posible mostrar la bandeja de trabajo.");
        Assert.Equal(0, await page.Locator("table.opsInboxTable").CountAsync());
        Assert.Equal(0, await page.Locator("nav.opsInboxQueues").CountAsync());
        Assert.Equal(0, await ZoneChipGroup(page).CountAsync());
        var main = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(Ready.PublicId, main, StringComparison.Ordinal);
        Assert.DoesNotContain("2 órdenes", main, StringComparison.Ordinal);
        var reads = api.AllCalls.Count;
        await Task.Delay(1_000);
        Assert.Equal(reads, api.AllCalls.Count);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 work_inbox.landing and privacy ("an organization switch clears rows, zones, counts, the selection and the
    /// batch"; the batch is B5b's): DISPATCHER lands on the inbox from a page its role cannot use; a switch drops the
    /// rows, the zone chips, the counts, the selection and the open "Asignar" panel before the next organization
    /// answers, and reads no driver list under it; VIEWER lands on "Órdenes".
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_lands_on_the_inbox_and_clears_rows_zones_counts_selection_and_the_open_assignment()
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
        var api = await InstallAsync(
            page,
            realtime: false,
            (OrganizationA, OrganizationAName, "VIEWER"),
            (OrganizationB, OrganizationBName, "DISPATCHER"),
            (OrganizationC, OrganizationCName, "PLATFORM_ADMIN"));
        api.Seed(OrganizationB, Ready, Rescheduled, FailedAttempt, Returning);
        api.Seed(OrganizationC, OrderOfC);

        // AI-07 work_inbox.landing: from a page its role cannot use, DISPATCHER lands on the inbox after the switch.
        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await SwitchOrganizationAsync(page, OrganizationBName);
        await WaitForAsync(() => new Uri(page.Url).AbsolutePath == "/ops/inbox", "DISPATCHER did not land on the inbox");
        await ExpectRowsAsync(page, ReadyRow, RescheduledRow);
        await ExpectNavigationAsync(page, "DISPATCHER");

        // Zones, a selection and an open assignment...
        Assert.Equal(new[] { "Todas", "Centro", "Norte" }, await ChipsAsync(page, "Zona de entrega"));
        await RowCheckbox(page, Ready).CheckAsync();
        await ExpectFooterAsync(page, "Mostrando 2 de 2 sin asignar · seleccionadas: 1");
        await AssignButton(page, Rescheduled).ClickAsync();
        await page.Locator($"section[aria-label=\"Asignar repartidor a la orden {Rescheduled.PublicId}\"] ul.opsDriverChoices > li").First.WaitForAsync();

        // ...go with the organization, before C answers.
        var releaseOrganizationC = api.Hold(OrganizationC);
        await SwitchOrganizationAsync(page, OrganizationCName);
        await WaitForAsync(() => api.ListReadTimes(OrganizationC).Length > 0, "organization C was never read");
        var during = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(Ready.PublicId, during, StringComparison.Ordinal);
        Assert.DoesNotContain(Rescheduled.PublicId, during, StringComparison.Ordinal);
        Assert.DoesNotContain("seleccionadas", during, StringComparison.Ordinal);
        Assert.DoesNotContain("2 órdenes", during, StringComparison.Ordinal);
        Assert.Equal(0, await ZoneChipGroup(page).CountAsync());
        Assert.Equal(0, await page.Locator("main section.opsInboxAssign").CountAsync());
        releaseOrganizationC.TrySetResult();
        await ExpectRowsAsync(page, [OrderOfC.PublicId, "Lista para recolección", "Centro", "Horario de la zona", "Sin repartidor", "$99.00 MXN", "seleccionable", "Asignar"]);
        await ExpectFooterAsync(page, "Mostrando 1 de 1 sin asignar · seleccionadas: 0");
        // Only C's zones: B's "Norte" went with the switch.
        Assert.Equal(new[] { "Todas", "Centro" }, await ChipsAsync(page, "Zona de entrega"));
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        Assert.Equal("/ops/inbox", new Uri(page.Url).AbsolutePath);
        Assert.Equal(0, await page.Locator("main section.opsInboxAssign").CountAsync());
        Assert.False(
            api.AllCalls.Any(call => call.Organization == OrganizationC && call.Path.EndsWith("/assignable-drivers", StringComparison.Ordinal)),
            string.Join(" | ", api.AllCalls.Select(call => $"{call.Organization[..2]} {call.Path}")));

        // VIEWER has no inbox: the switch lands it on "Órdenes" (UI-PHASE3-ORDERS-LIST-2026-10-12).
        await SwitchOrganizationAsync(page, OrganizationAName);
        await WaitForAsync(() => new Uri(page.Url).AbsolutePath == "/ops/orders", "VIEWER did not land on Órdenes");
        // The inbox's read for VIEWER, if it left before the move, is the API's 403 (OperationsRolePolicy).
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationA && call.Path.EndsWith("/assignable-drivers", StringComparison.Ordinal));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress, new Regex("^GET /api/v1/orders$"));
        trace.Passed();
    }

    /// <summary>
    /// AI-07 work_inbox.roles and excluded_roles: DISPATCHER and PLATFORM_ADMIN with MFA only. The shell does not offer
    /// the inbox to VIEWER, FINANCE or DRIVER, and for them, as for PLATFORM_ADMIN without MFA, the API answers 403: no
    /// order, count or realtime connection is shown.
    /// </summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("VIEWER")]
    [InlineData("FINANCE")]
    [InlineData("DRIVER")]
    [InlineData("PLATFORM_ADMIN")]
    public async Task Roles_without_the_inbox_see_no_order_or_count(string role)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, role, PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, realtime: true, (OrganizationA, OrganizationAName, role));
        api.Seed(OrganizationA, Ready, Rescheduled);
        // The API's rule (OperationsRolePolicy): DISPATCHER, and PLATFORM_ADMIN only with a satisfied MFA challenge.
        api.MfaRequired = role == "PLATFORM_ADMIN";

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await ExpectTextAsync(page, "No es posible mostrar la bandeja de trabajo.");
        await ExpectNavigationAsync(page, role);
        await Task.Delay(500);
        Assert.Equal(0, await page.Locator("table.opsInboxTable").CountAsync());
        Assert.Equal(0, await page.Locator("nav.opsInboxQueues").CountAsync());
        Assert.DoesNotContain(Ready.PublicId, await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(0, api.CountReads(OrganizationA));
        Assert.Null(api.HubOrganization);
        Assert.DoesNotContain(calls.Api, call => call.Path.StartsWith("/hubs/", StringComparison.Ordinal));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    private static string TooManyRequestsText(int seconds) => seconds == 1
        ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
        : $"Demasiadas solicitudes. Espera {seconds} segundos e intenta de nuevo.";

    /// <summary>The visible countdown at any second of the 6-second wait.</summary>
    [GeneratedRegex("^Demasiadas solicitudes\\. Espera [1-6] segundos? e intenta de nuevo\\.$")]
    private static partial Regex TooManyRequestsPattern();

    private static ILocator Tab(IPage page, string label) =>
        page.Locator("nav.opsInboxQueues a.opsInboxQueue", new() { Has = page.Locator($"span.opsInboxQueueLabel > span:text-is('{label}')") });

    private static ILocator Chip(IPage page, string group, string label) =>
        page.Locator($"fieldset.opsChipGroup:has(legend:text-is('{group}')) button.opsChip:text-is('{label}')");

    private static ILocator ZoneChipGroup(IPage page) =>
        page.Locator("fieldset.opsChipGroup:has(legend:text-is('Zona de entrega'))");

    private static async Task<string[]> ChipsAsync(IPage page, string group) =>
        (await page.Locator($"fieldset.opsChipGroup:has(legend:text-is('{group}')) button.opsChip").AllInnerTextsAsync())
            .Select(text => text.Trim())
            .ToArray();

    private static ILocator RefreshButton(IPage page) =>
        page.Locator("main header").GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });

    private static ILocator LoadMoreButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true });

    private static ILocator AssignButton(IPage page, InboxOrder order) => page.Locator($"#inbox-assign-{order.Id}");

    private static ILocator RowCheckbox(IPage page, InboxOrder order) =>
        page.GetByRole(AriaRole.Checkbox, new() { Name = $"Seleccionar la orden {order.PublicId}", Exact = true });

    private static ILocator OpenDialog(IPage page) => page.Locator("dialog.confirmDialog[open]");

    private static Task SwitchOrganizationAsync(IPage page, string name) =>
        page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = name });

    /// <summary>
    /// Each row as its columns (Guía, Estado with the price review tag, Destino, Ventana, Repartidor, Total), then
    /// whether it can be selected and whether it offers "Asignar".
    /// </summary>
    private static async Task<string[]> RowsAsync(IPage page) =>
        await page.Locator("table.opsInboxTable tbody tr").EvaluateAllAsync<string[]>(
            """
            rows => rows.map(row => {
              const cells = [...row.cells].filter(cell => !cell.classList.contains("opsInboxSelect"));
              const text = cell => cell.innerText.replace(/\s+/g, " ").trim();
              const status = cells[1].querySelector(".statusBadgeLabel").textContent.trim() +
                (cells[1].querySelector(".opsInboxTag") === null ? "" : " + " + cells[1].querySelector(".opsInboxTag").textContent.trim());
              return [text(cells[0]), status, text(cells[2]), text(cells[3]), text(cells[4]), text(cells[5]),
                row.querySelector("td.opsInboxSelect input") === null ? "-" : "seleccionable",
                row.querySelector(".opsInboxActions button") === null ? "-" : "Asignar"].join(" | ");
            })
            """);

    private static async Task ExpectRowsAsync(IPage page, params string[][] rows)
    {
        var expected = rows.Select(row => string.Join(" | ", row)).ToArray();
        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await RowsAsync(page);
            if (actual.SequenceEqual(expected, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The inbox rows did not match: {string.Join(" || ", actual)}");
    }

    /// <summary>The queue tabs as "label count", the current one first in the arguments.</summary>
    private static async Task ExpectTabsAsync(IPage page, string current, params string[] tabs)
    {
        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("nav.opsInboxQueues a.opsInboxQueue").EvaluateAllAsync<string[]>(
                "links => links.map(link => link.textContent.replace(/\\s+/g, ' ').trim())");
            if (actual.SequenceEqual(tabs, StringComparer.Ordinal)) break;
            await Task.Delay(50);
        }

        Assert.Equal(tabs, actual);
        Assert.Equal("page", await Tab(page, current).GetAttributeAsync("aria-current"));
    }

    private static async Task ExpectFooterAsync(IPage page, string text) =>
        await page.Locator("main .opsInboxFooter p.opsSummaryNote").First.GetByText(text, new() { Exact = true }).WaitForAsync();

    /// <summary>The queue's empty state: its title, then the hint when chips are set.</summary>
    private static async Task ExpectEmptyAsync(IPage page, string title, string? hint = null)
    {
        var empty = page.Locator("main div.emptyState");
        await empty.Locator("strong").GetByText(title, new() { Exact = true }).WaitForAsync();
        Assert.Equal(title + (hint ?? string.Empty), (await empty.TextContentAsync())?.Trim());
    }

    private static Task ExpectTextAsync(IPage page, string text) =>
        page.Locator("main").GetByText(text, new() { Exact = true }).WaitForAsync();

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

        await WaitForAsync(
            async () => (await page.Locator("nav.appNav a").EvaluateAllAsync<string[]>(
                "links => links.map(link => link.getAttribute(\"href\"))")).SequenceEqual(expected),
            $"the shell did not offer the {role} items");
        if (expected.Contains("/ops/inbox") && new Uri(page.Url).AbsolutePath == "/ops/inbox")
        {
            Assert.Equal("page", await page.Locator("nav.appNav a[href=\"/ops/inbox\"]").GetAttributeAsync("aria-current"));
        }
    }

    /// <summary>
    /// No internal id is visible anywhere on the page (the sibling panel suites' check): no UUID, nor the first 8
    /// characters of any synthetic organization, zone, driver or order id. Only guías and DRV- references are shown.
    /// </summary>
    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        foreach (var id in new[]
                 {
                     OrganizationA, OrganizationB, OrganizationC, ZoneCentro, ZoneNorte, DriverMoto, DriverCar, DriverVan,
                     Ready.Id, Rescheduled.Id, FailedAttempt.Id, Returning.Id, OrderOfC.Id, $"d2{Ready.Id[2..]}",
                 })
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

    /// <summary>Every API call is a source of this screen, and nothing goes to any third origin (no map tiles either).</summary>
    private static void AssertOnlyAllowedCalls(CallRecorder calls, Uri web, params Regex[] alsoAllowed)
    {
        Assert.All(calls.Api, call => Assert.Contains(
            AllowedCalls.Concat(alsoAllowed),
            pattern => pattern.IsMatch($"{call.Method} {call.Path}")));
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
    /// switch, and fulfils /me/organization-contexts, the dashboard read model, the counts and the assignment
    /// operations. With <paramref name="realtime"/> the OperationsHub is a mocked SignalR JSON hub; otherwise it is
    /// unreachable. Playwright runs the last matching route first, so the catch-all is registered before the specific
    /// ones.
    /// </summary>
    private static async Task<InboxApi> InstallAsync(
        IPage page,
        bool realtime,
        params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-work-inbox-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        var api = new InboxApi(organizations);
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", api.HandleRoleReadAsync);
        await page.RouteAsync("**/api/v1/operations/**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/orders/*/assignable-drivers**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/orders/*/assignments**", api.HandleAsync);
        if (realtime)
        {
            await page.RouteAsync(new Regex("/hubs/operations/negotiate"), api.NegotiateAsync);
            await page.RouteWebSocketAsync(new Regex("/hubs/operations"), api.AttachHub);
        }

        return api;
    }

    private static Task FulfillAsync(IRoute route, object body, int status = 200) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
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

    /// <summary>The order detail's key: one per confirmed driver and cost (driver-assignment-controller.ts).</summary>
    [GeneratedRegex("^assign-driver-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex AssignmentKeyPattern();

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    /// <summary>One order of the dashboard read model (ADR-OBS-001); the assignment changes it.</summary>
    private sealed record InboxOrder(
        string Id,
        string PublicId,
        string Status,
        string ServiceType,
        string? ZoneId,
        (string From, string To)? DeliveryWindow,
        string? DriverReference,
        long TotalCents,
        string? CostWarning,
        bool UnassignedAlert,
        string UpdatedAt)
    {
        private static readonly IReadOnlyDictionary<string, string> ZoneNames = new Dictionary<string, string>
        {
            [ZoneCentro] = "Centro",
            [ZoneNorte] = "Norte",
        };

        internal static readonly string[] Finished = ["DELIVERED", "RETURNED", "CLAIM_RESOLVED", "CLOSED", "CANCELLED"];

        internal object Json(string organization, string organizationName) => new
        {
            order_id = Id,
            aggregate_version = 3,
            public_id = PublicId,
            owner = new { organization_id = organization, display_name = organizationName },
            @operator = (object?)null,
            client = (object?)null,
            status = Status,
            created_at = "2026-11-01T18:00:00Z",
            updated_at = UpdatedAt,
            service_type = ServiceType,
            pickup_window = (object?)null,
            delivery_window = DeliveryWindow is { } window ? new { from = window.From, to = window.To } : null,
            delivery_zone = ZoneId is null ? null : new { operating_zone_id = ZoneId, name = ZoneNames[ZoneId], zone_type = "CORE" },
            assignment = DriverReference is null
                ? null
                : new
                {
                    assignment_id = $"a5{Id[2..]}",
                    assignment_type = "OWN",
                    status = "ACCEPTED",
                    driver_id = $"d2{Id[2..]}",
                    driver_reference = DriverReference,
                },
            latest_driver_location = (object?)null,
            total = new { currency = "MXN", amount_cents = TotalCents },
            cost_warning = CostWarning,
            unassigned_alert = UnassignedAlert,
        };
    }

    private sealed record InboxCall(string Organization, string Method, string Path, string Query, string? IdempotencyKey, string? Body);

    /// <summary>
    /// The dashboard read model with its own filters (unassigned, price_review, status, service_type,
    /// delivery_zone_id, cursor), newest first by updated_at and order id; getOperationsQueueCounts derived from the
    /// same orders; listAssignableDrivers and assignDriver; and a mocked OperationsHub. Every call is recorded.
    /// </summary>
    private sealed class InboxApi((string Id, string Name, string Role)[] organizations)
    {
        private static readonly string[] OrderStatuses =
        [
            "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING",
            "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED", "DELIVERED", "CLOSED", "CLAIM_OPEN",
            "CLAIM_RESOLVED", "CANCELLED",
        ];

        private static readonly string[] DashboardParameters =
            ["unassigned", "price_review", "status", "service_type", "delivery_zone_id", "cursor"];

        private readonly object _gate = new();
        private readonly List<InboxCall> _calls = [];
        private readonly List<(string Organization, DateTimeOffset At)> _listReads = [];
        private readonly Dictionary<string, List<InboxOrder>> _orders = [];
        private readonly Dictionary<string, List<string>> _queries = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();
        private IWebSocketRoute? _hub;

        internal int PageSize { get; set; } = 50;

        internal bool PriceReviewOutsideRule { get; set; }

        /// <summary>How many of the next counts reads answer 429 with Retry-After: 6.</summary>
        internal int CountsTooManyRequests { get; set; }

        /// <summary>How many of the next dashboard reads answer 429 with Retry-After: 6.</summary>
        internal int DashboardTooManyRequests { get; set; }

        /// <summary>The dashboard and the counts answer a plain 403 from now on (access withdrawn mid-session).</summary>
        internal bool Forbidden { get; set; }

        /// <summary>The dashboard and the counts answer 403 MFA_REQUIRED (PLATFORM_ADMIN without MFA).</summary>
        internal bool MfaRequired { get; set; }

        /// <summary>The organization the OperationsHub was opened for; null while it never was.</summary>
        internal string? HubOrganization { get; private set; }

        internal IReadOnlyList<InboxCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal void Seed(string organization, params InboxOrder[] orders)
        {
            lock (_gate) _orders[organization] = [.. orders];
        }

        /// <summary>The dashboard queries since the last call, in arrival order.</summary>
        internal string[] TakeQueries(string organization)
        {
            lock (_gate)
            {
                var queries = _queries.GetValueOrDefault(organization, []).ToArray();
                _queries[organization] = [];
                return queries;
            }
        }

        /// <summary>When each dashboard read of the organization started (one per queue load, the first of its queries).</summary>
        internal DateTimeOffset[] ListReadTimes(string organization)
        {
            lock (_gate)
            {
                return [.. _listReads.Where(read => read.Organization == organization).Select(read => read.At)];
            }
        }

        internal int CountReads(string organization) =>
            AllCalls.Count(call => call.Organization == organization && call.Path == "/api/v1/operations/queue-counts");

        internal string[] DriverReads(string organization) =>
            [.. AllCalls.Where(call => call.Organization == organization && call.Path.EndsWith("/assignable-drivers", StringComparison.Ordinal)).Select(call => call.Path)];

        internal InboxCall[] Assignments(string organization) =>
            [.. AllCalls.Where(call => call.Organization == organization && call.Method == "POST" && call.Path.EndsWith("/assignments", StringComparison.Ordinal))];

        internal TaskCompletionSource Hold(string organization) =>
            _holds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task HandleRoleReadAsync(IRoute route)
        {
            var organization = (await route.Request.AllHeadersAsync()).GetValueOrDefault("x-organization-id") ?? string.Empty;
            lock (_gate) _calls.Add(new InboxCall(organization, "GET", "/api/v1/me/organization-contexts", string.Empty, null, null));
            await FulfillAsync(route, organizations.Select((organization, index) => new
            {
                organization_id = organization.Id,
                display_name = organization.Name,
                role = organization.Role,
                is_default = index == 0,
            }).ToArray());
        }

        internal async Task HandleAsync(IRoute route)
        {
            // Taken before anything is awaited, so the read times measure the browser's spacing, not this handler's.
            var receivedAt = DateTimeOffset.UtcNow;
            var request = route.Request;
            var uri = new Uri(request.Url);
            var headers = await request.AllHeadersAsync();
            var organization = headers.GetValueOrDefault("x-organization-id") ?? string.Empty;
            var call = new InboxCall(
                organization,
                request.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                headers.GetValueOrDefault("idempotency-key"),
                request.PostData);
            lock (_gate)
            {
                _calls.Add(call);
                if (call.Path == "/api/v1/operations/dashboard")
                {
                    if (!_queries.TryGetValue(organization, out var queries)) _queries[organization] = queries = [];
                    queries.Add(call.Query);
                    // The queries of one load arrive within milliseconds of each other: a query more than 500 ms after the
                    // start of the organization's previous read starts a new read.
                    var last = _listReads.LastOrDefault(read => read.Organization == organization);
                    if (last.Organization is null || receivedAt - last.At > TimeSpan.FromMilliseconds(500))
                    {
                        _listReads.Add((organization, receivedAt));
                    }
                }
            }

            if (_holds.TryGetValue(organization, out var hold)) await hold.Task;
            var role = organizations.FirstOrDefault(entry => entry.Id == organization).Role;
            var segments = call.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (call.Method, segments)
            {
                case ("GET", ["api", "v1", "operations", "dashboard"]):
                case ("GET", ["api", "v1", "operations", "queue-counts"]):
                    // AI-05 getOperationsQueueCounts: any query string is 400, before capability.
                    if (segments[3] == "queue-counts" && call.Query.Length > 0)
                    {
                        await ProblemAsync(route, 400);
                        return;
                    }

                    if (Forbidden || (role != "DISPATCHER" && role != "PLATFORM_ADMIN"))
                    {
                        await ProblemAsync(route, 403);
                        return;
                    }

                    if (MfaRequired)
                    {
                        await ProblemAsync(route, 403, "MFA_REQUIRED");
                        return;
                    }

                    if (segments[3] == "dashboard") await DashboardAsync(route, call);
                    else await CountsAsync(route, call);
                    return;
                case ("GET", ["api", "v1", "orders", _, "assignable-drivers"]):
                    await FulfillAsync(route, new
                    {
                        items = new object[]
                        {
                            Driver(DriverVan, "DRV-9c0d1e2f", "VAN", 1, "DOCUMENT_EXPIRED"),
                            Driver(DriverCar, "DRV-5e6f7a8b", "CAR", 2),
                            Driver(DriverMoto, "DRV-1a2b3c4d", "MOTORCYCLE", 0),
                        },
                        next_cursor = (string?)null,
                    });
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "assignments"]):
                    await AssignAsync(route, call, orderId);
                    return;
                default:
                    await ProblemAsync(route, 404);
                    return;
            }
        }

        private static object Driver(string id, string reference, string vehicle, int active, params string[] reasons) => new
        {
            driver_id = id,
            driver_reference = reference,
            vehicle_type = vehicle,
            eligible = reasons.Length == 0,
            ineligibility_reasons = reasons,
            active_assignment_count = active,
        };

        private async Task DashboardAsync(IRoute route, InboxCall call)
        {
            if (TakeLimit(isCounts: false))
            {
                await TooManyRequestsAsync(route);
                return;
            }

            var query = new Dictionary<string, string>();
            foreach (var pair in call.Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !DashboardParameters.Contains(parts[0]) || !query.TryAdd(parts[0], Uri.UnescapeDataString(parts[1])))
                {
                    await ProblemAsync(route, 400);
                    return;
                }
            }

            InboxOrder[] matching;
            var organizationName = organizations.First(entry => entry.Id == call.Organization).Name;
            lock (_gate)
            {
                matching = _orders.GetValueOrDefault(call.Organization, [])
                    .Where(order => !query.ContainsKey("unassigned") ||
                        (order.UnassignedAlert && order.Status is "READY_FOR_PICKUP" or "RESCHEDULED"))
                    .Where(order => !query.ContainsKey("price_review") || PriceReviewOutsideRule ||
                        (order.CostWarning is not null && !InboxOrder.Finished.Contains(order.Status)))
                    .Where(order => !query.TryGetValue("status", out var status) || order.Status == status)
                    .Where(order => !query.TryGetValue("service_type", out var service) || order.ServiceType == service)
                    .Where(order => !query.TryGetValue("delivery_zone_id", out var zone) || order.ZoneId == zone)
                    .OrderByDescending(order => order.UpdatedAt, StringComparer.Ordinal)
                    .ThenByDescending(order => order.Id, StringComparer.Ordinal)
                    .ToArray();
            }

            var offset = query.TryGetValue("cursor", out var cursor) ? int.Parse(cursor[1..], System.Globalization.CultureInfo.InvariantCulture) : 0;
            await FulfillAsync(route, new
            {
                generated_at = GeneratedAt,
                items = matching.Skip(offset).Take(PageSize).Select(order => order.Json(call.Organization, organizationName)).ToArray(),
                next_cursor = offset + PageSize < matching.Length ? $"c{offset + PageSize}" : null,
            });
        }

        private bool TakeLimit(bool isCounts)
        {
            lock (_gate)
            {
                if (isCounts ? CountsTooManyRequests == 0 : DashboardTooManyRequests == 0) return false;
                if (isCounts) CountsTooManyRequests -= 1;
                else DashboardTooManyRequests -= 1;
                return true;
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

        private async Task CountsAsync(IRoute route, InboxCall call)
        {
            if (TakeLimit(isCounts: true))
            {
                await TooManyRequestsAsync(route);
                return;
            }

            InboxOrder[] orders;
            lock (_gate) orders = [.. _orders.GetValueOrDefault(call.Organization, [])];
            var byStatus = OrderStatuses.ToDictionary(status => status, status => orders.Count(order => order.Status == status));
            int Sum(params string[] statuses) => statuses.Sum(status => byStatus[status]);
            await FulfillAsync(route, new
            {
                generated_at = GeneratedAt,
                total = orders.Length,
                by_status = byStatus,
                queues = new
                {
                    unassigned = orders.Count(order => order.UnassignedAlert && order.Status is "READY_FOR_PICKUP" or "RESCHEDULED"),
                    needs_attention = Sum("FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "CLAIM_OPEN"),
                    price_review = orders.Count(order => order.CostWarning is not null && !InboxOrder.Finished.Contains(order.Status)),
                    delivered_not_closed = Sum("DELIVERED"),
                    en_route = Sum("IN_TRANSIT", "DELIVERING"),
                },
            });
        }

        private async Task AssignAsync(IRoute route, InboxCall call, string orderId)
        {
            using var body = JsonDocument.Parse(call.Body ?? "{}");
            var driverId = body.RootElement.GetProperty("driver_id").GetString()!;
            var cost = body.RootElement.GetProperty("cost_cents").GetInt64();
            lock (_gate)
            {
                var orders = _orders[call.Organization];
                var index = orders.FindIndex(order => order.Id == orderId);
                orders[index] = orders[index] with
                {
                    Status = "ASSIGNED",
                    DriverReference = "DRV-5e6f7a8b",
                    UnassignedAlert = false,
                    UpdatedAt = "2026-11-02T06:40:00Z",
                };
            }

            await FulfillAsync(
                route,
                new
                {
                    id = "a6000000-0000-4000-8000-000000000001",
                    order_id = orderId,
                    driver_id = driverId,
                    route_id = (string?)null,
                    status = "ACCEPTED",
                    cost = new { currency = "MXN", amount_cents = cost },
                },
                201);
        }

        /// <summary>SignalR negotiation (version 1), offering only WebSockets.</summary>
        internal Task NegotiateAsync(IRoute route) =>
            FulfillAsync(route, new
            {
                negotiateVersion = 1,
                connectionId = "synthetic-connection",
                connectionToken = "synthetic-connection-token",
                availableTransports = new[] { new { transport = "WebSockets", transferFormats = new[] { "Text", "Binary" } } },
            });

        /// <summary>The OperationsHub over the SignalR JSON protocol: handshake, pings and server invocations.</summary>
        internal void AttachHub(IWebSocketRoute socket)
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(socket.Url).Query);
            HubOrganization = query["organization_id"];
            _hub = socket;
            socket.OnMessage(frame =>
            {
                foreach (var record in (frame.Text ?? string.Empty).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (record.Contains("\"protocol\"", StringComparison.Ordinal)) socket.Send("{}\u001e");
                    else if (record.Contains("\"type\":6", StringComparison.Ordinal)) socket.Send("{\"type\":6}\u001e");
                }
            });
        }

        /// <summary>
        /// One AI-12 envelope pushed by the hub: the hub method is the event name, <c>event_type</c> its versioned type
        /// (realtime/event-types.ts) and the payload that event's AI-12 fields, with synthetic values.
        /// </summary>
        internal void SendNotice(string target, string aggregateId, int version, string eventId)
        {
            const string occurredAt = "2026-11-02T06:35:00Z";
            object payload = target switch
            {
                "OrderStatusChanged" => new { order_id = aggregateId, previous_status = "CONFIRMED", new_status = "READY_FOR_PICKUP", occurred_at = occurredAt },
                "AssignmentChanged" => new
                {
                    order_id = aggregateId,
                    assignment_id = "a6000000-0000-4000-8000-000000000002",
                    driver_id = DriverCar,
                    assignment_status = "CANCELLED",
                    occurred_at = occurredAt,
                },
                "IncidentCreated" => new
                {
                    incident_id = "e2000000-0000-4000-8000-000000000001",
                    order_id = aggregateId,
                    type = "FAILED_ATTEMPT",
                    severity = "MEDIUM",
                    status = "OPEN",
                    occurred_at = occurredAt,
                },
                "DriverLocationUpdated" => new { driver_id = aggregateId, lat = 23.2494, lng = -106.4111, accuracy_m = 12.5, captured_at = occurredAt },
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "No AI-12 payload is written for this event."),
            };
            var envelope = new
            {
                event_id = eventId,
                event_type = $"{target}.v1",
                occurred_at = occurredAt,
                aggregate_id = aggregateId,
                aggregate_version = version,
                payload,
            };
            _hub!.Send(JsonSerializer.Serialize(new { type = 1, target, arguments = new[] { envelope } }) + "\u001e");
        }
    }
}
