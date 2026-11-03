using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-001 "Playwright per role" for AI-07 <c>work_inbox</c> bulk actions (<c>/ops/inbox</c>,
/// UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11, UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11,
/// UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11, UI-PHASE3-ROUTES-DAILY-2026-10-12, SEC-004-RELOADS-AND-BULK-2026-10-13
/// Q10 and Q15) in a real browser against the production Next build (B5b; the list and the single assignment are
/// WorkInboxPwaPlaywrightTests). The API is fulfilled in the browser: the dashboard read model (OBS-001, ADR-OBS-001)
/// and the AI-05 shapes of getOperationsQueueCounts, listAssignableDrivers, assignDriver, listRoutes, getRoute,
/// addRouteStop, getOrder, listCities, createRoute and /me/organization-contexts.
/// "Asignar seleccionadas…" offers only the drivers eligible for every order of the frozen selection, with why the
/// others are kept out; the preview takes one cost per order in integer cents and their sum; the confirmation names
/// the driver, the orders and the sum; the orders are sent one after another, each with its own key, a 409 answers
/// for its order only, a server failure stops the run and "Reintentar las pendientes" resends with the same keys; a
/// 429 on one order's drivers leaves its "Reintentar" disabled until 0 and reads the others after the wait; the
/// assigned orders go to one of the driver's DRAFT routes, today's (America/Mazatlan) first, or to a route created
/// for the orders' city with today's Mazatlán date. An organization switch during a run closes the batch and sends none
/// of its pending orders. Nothing is stored in the browser, nothing is logged, no id is shown and every amount comes
/// from integer cents.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class WorkInboxBulkPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationBName = "Reparto Sintético del Norte";

    private const string ZoneCentro = "5a000000-0000-4000-8000-000000000001";
    private const string ZoneNorte = "5a000000-0000-4000-8000-000000000002";
    private const string CityId = "33333333-3333-4333-8333-333333333333";

    private const string DriverMoto = "d1000000-0000-4000-8000-000000000001";
    private const string DriverCar = "d1000000-0000-4000-8000-000000000002";
    private const string DriverVan = "d1000000-0000-4000-8000-000000000003";

    private const string RouteToday = "7a000000-0000-4000-8000-000000000001";
    private const string RouteOlder = "7a000000-0000-4000-8000-000000000002";
    private const string RouteUndated = "7a000000-0000-4000-8000-000000000003";
    private const string RouteCreated = "7a000000-0000-4000-8000-0000000000c1";

    /// <summary>22:30 on 2 November in Mazatlán; already 3 November in Tokyo and in UTC.</summary>
    private const string FixedNow = "2026-11-03T05:30:00Z";
    private const string TodayInMazatlan = "2026-11-02";

    private static readonly BulkOrder First = new("b1000000-0000-4000-8000-000000000001", "ORD_LoteSintetico000000001", "READY_FOR_PICKUP", ZoneCentro, 100_113, "2026-11-02T06:30:00Z");
    private static readonly BulkOrder Second = new("b1000000-0000-4000-8000-000000000002", "ORD_LoteSintetico000000002", "READY_FOR_PICKUP", ZoneNorte, 25_000, "2026-11-02T06:20:00Z");
    private static readonly BulkOrder Third = new("b1000000-0000-4000-8000-000000000003", "ORD_LoteSintetico000000003", "RESCHEDULED", ZoneCentro, 5_000, "2026-11-02T06:10:00Z");
    private static readonly BulkOrder OrderOfB = new("b2000000-0000-4000-8000-000000000001", "ORD_LoteSintetico000000004", "READY_FOR_PICKUP", ZoneNorte, 9_900, "2026-11-02T06:00:00Z", OrganizationB);

    private static readonly string[] NavigationOfDispatcher =
    [
        "/ops/inbox", "/ops/orders", "/ops/dashboard", "/ops/orders/new", "/ops/routes", "/ops/incidents",
        "/ops/orders/import", "/ops/drivers", "/finance/cod",
    ];

    private const string SelectionNote =
        "Hasta 20 órdenes por lote. Las acciones en lote siempre muestran una vista previa antes de ejecutarse.";

    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/operations/dashboard$"),
        new("^GET /api/v1/operations/queue-counts$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/assignable-drivers$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/assignments$"),
        new("^GET /api/v1/routes$"),
        new("^GET /api/v1/routes/[0-9a-f-]{36}$"),
        new("^POST /api/v1/routes/[0-9a-f-]{36}/stops$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}$"),
        new("^GET /api/v1/cities$"),
        new("^POST /api/v1/routes$"),
        new("^POST /hubs/operations/negotiate$"),
    ];

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Bulk_assignment_offers_drivers_for_every_order_previews_integer_cents_and_reports_each_order()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page);
        api.AssignAnswers[Second.Id] = new Queue<string>(["409:DRIVER_INELIGIBLE"]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectGuidesAsync(page, First, Second, Third);
        await ExpectNavigationAsync(page, "DISPATCHER");
        await ExpectFooterAsync(page, "Mostrando 3 de 3 sin asignar · seleccionadas: 0");
        Assert.Equal(SelectionNote, (await page.Locator("#inbox-selection-limit").InnerTextAsync()).Trim());
        Assert.True(await BulkButton(page).IsDisabledAsync());

        // "Seleccionar todas" takes the loaded selectable rows; the batch is that selection, frozen while it is open.
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Seleccionar todas", Exact = true }).CheckAsync();
        await ExpectFooterAsync(page, "Mostrando 3 de 3 sin asignar · seleccionadas: 3");
        await BulkButton(page).ClickAsync();
        var panel = Panel(page);
        await panel.GetByRole(AriaRole.Heading, new() { Name = "Asignar 3 órdenes seleccionadas", Exact = true }).WaitForAsync();
        await panel.Locator("ul.opsDriverChoices > li").First.WaitForAsync();
        Assert.Equal(
            new[] { $"/api/v1/orders/{First.Id}/assignable-drivers", $"/api/v1/orders/{Second.Id}/assignable-drivers", $"/api/v1/orders/{Third.Id}/assignable-drivers" },
            api.Paths("GET", "/assignable-drivers"));
        Assert.True(await RowCheckbox(page, First).IsDisabledAsync());
        Assert.True(await page.Locator($"#inbox-assign-{First.Id}").IsDisabledAsync());

        // Only a driver eligible for every order can be picked; the others say which orders keep them out and why.
        Assert.Equal(
            new[] { "DRV-1a2b3c4d · Motocicleta · 1 entrega en curso" },
            await TextsAsync(panel.Locator("fieldset.opsBulkDrivers ul.opsDriverChoices > li")));
        Assert.Equal(
            new[]
            {
                $"DRV-9c0d1e2f · Van · sin entregas en curso: No puede llevar {Third.PublicId} (no aparece en su lista).",
                $"DRV-5e6f7a8b · Automóvil · 2 entregas en curso: No puede llevar {Second.PublicId} (Tiene un documento vencido).",
            },
            await TextsAsync(panel.Locator(".opsBulkBlocked li")));
        Assert.Equal(
            new[]
            {
                $"{First.PublicId} | Centro | $1,001.13 MXN | [] | Lista para asignar | Quitar la orden {First.PublicId} del lote",
                $"{Second.PublicId} | Norte | $250.00 MXN | [] | Lista para asignar | Quitar la orden {Second.PublicId} del lote",
                $"{Third.PublicId} | Centro | $50.00 MXN | [] | Lista para asignar | Quitar la orden {Third.PublicId} del lote",
            },
            await PreviewRowsAsync(panel));
        await AssertNoIdentifiersAsync(page);

        // No driver, then a cost that is not pesos, are refused before anything is sent.
        await panel.GetByRole(AriaRole.Button, new() { Name = "Revisar y asignar 3 órdenes", Exact = true }).ClickAsync();
        await panel.Locator("p.notice[role=alert]").GetByText("Elige un repartidor que pueda llevar todas las órdenes.", new() { Exact = true }).WaitForAsync();
        await panel.Locator($"input[type=radio][value=\"{DriverMoto}\"]").CheckAsync();
        await CostInput(panel, First).FillAsync("4.35");
        await CostInput(panel, Second).FillAsync("mil");
        await CostInput(panel, Third).FillAsync("0");
        await ExpectTextAsync(panel, "Suma de los costos: escribe un costo válido para cada orden.");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Revisar y asignar 3 órdenes", Exact = true }).ClickAsync();
        await panel.Locator("p.notice[role=alert]").GetByText("Revisa el costo de 1 orden.", new() { Exact = true }).WaitForAsync();
        await panel.Locator($"#bulk-cost-{Second.Id}-error").GetByText("Escribe un costo válido en pesos, por ejemplo 45.00.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await OpenDialog(page).CountAsync());

        // 4.35 × 100 is 434.99999999999994 and 19.99 × 100 is 1998.9999999999998 in binary floating point, so a float
        // parse that truncates would send 434 and 1998; neither value is a row total. Integer parsing sends 435 and
        // 1999, and the sum is exact.
        await CostInput(panel, Second).FillAsync("19.99");
        await ExpectTextAsync(panel, "Suma de los costos: $24.34 MXN");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Revisar y asignar 3 órdenes", Exact = true }).ClickAsync();
        await ConfirmAsync(
            page,
            "¿Asignar 3 órdenes?",
            "Se asignarán 3 órdenes al repartidor DRV-1a2b3c4d (motocicleta); los costos suman $24.34 MXN. Se envían una por una y cada fila mostrará su resultado.",
            "Asignar 3 órdenes");

        // One order after another, each with its own key; the 409 answers for its order only.
        await panel.Locator("p.notice[role=status]").GetByText(
            "Se asignaron 2 de 3 órdenes. Revisa el motivo de las demás en su fila.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(
            new[]
            {
                $"{First.PublicId} | Centro | $1,001.13 MXN | $4.35 MXN | Asignada a DRV-1a2b3c4d",
                $"{Second.PublicId} | Norte | $250.00 MXN | $19.99 MXN | El repartidor ya no cumple los requisitos para esta orden. Revisa la lista actualizada.",
                $"{Third.PublicId} | Centro | $50.00 MXN | $0.00 MXN | Asignada a DRV-1a2b3c4d",
            },
            await PreviewRowsAsync(panel));
        var assignments = api.Calls("POST", "/assignments");
        Assert.Equal(
            new[] { $"/api/v1/orders/{First.Id}/assignments", $"/api/v1/orders/{Second.Id}/assignments", $"/api/v1/orders/{Third.Id}/assignments" },
            assignments.Select(call => call.Path));
        Assert.Equal(new long[] { 435, 1_999, 0 }, assignments.Select(call => CostCents(call.Body)));
        Assert.All(assignments, call => Assert.Matches(AssignmentKeyPattern(), call.IdempotencyKey ?? string.Empty));
        Assert.Equal(3, assignments.Select(call => call.IdempotencyKey).Distinct().Count());
        Assert.All(assignments, call => Assert.Contains($"\"driver_id\":\"{DriverMoto}\",\"assignment_type\":\"OWN\"", call.Body, StringComparison.Ordinal));
        Assert.Equal(0, await panel.GetByRole(AriaRole.Button, new() { Name = "Reintentar las pendientes", Exact = true }).CountAsync());

        // The inbox and the counts are read again; the assigned orders left the queue; a route is offered for them.
        await ExpectGuidesAsync(page, Second);
        Assert.Equal(new[] { "unassigned=true", "unassigned=true" }, api.Queries("GET", "/api/v1/operations/dashboard"));
        Assert.Equal(
            "Las 2 órdenes asignadas a DRV-1a2b3c4d pueden ir a una de sus rutas en borrador; antes verás una vista previa.",
            (await panel.Locator("#inbox-bulk-route-hint").InnerTextAsync()).Trim());
        await panel.GetByRole(AriaRole.Button, new() { Name = "Cerrar", Exact = true }).ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.False(await RowCheckbox(page, Second).IsDisabledAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// SEC-004-RELOADS-AND-BULK-2026-10-13 Q10/Q15 and AI-07 work_inbox.bulk_assignment.execution: a 429 on one order's
    /// driver list leaves its "Reintentar" disabled until 0 and never sent by itself, while the orders not read yet are
    /// read after the wait; a server failure stops the run, the rest stay "Sin enviar", and "Reintentar las pendientes"
    /// resends them with the same keys and payloads.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task A_429_driver_list_waits_for_its_Reintentar_and_a_stopped_run_resends_the_pending_orders_with_their_keys()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page);
        api.DriverListTooManyRequests[Second.Id] = 2;
        api.AssignAnswers[Second.Id] = new Queue<string>(["503"]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectGuidesAsync(page, First, Second, Third);
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Seleccionar todas", Exact = true }).CheckAsync();
        await BulkButton(page).ClickAsync();
        var panel = Panel(page);
        var countdown = panel.GetByRole(AriaRole.Timer);
        var secondRow = panel.Locator("table.opsBulkPreview tbody tr").Nth(1);
        var retryList = secondRow.GetByRole(AriaRole.Button, new() { Name = "Reintentar", Exact = true });

        // The second order's list meets the limit; the third, not read yet, waits for the server's wait. The visible
        // countdown moves on its own; only the shell's announcer, written once, holds the server's exact wait.
        await countdown.GetByText(TooManyRequestsPattern()).WaitForAsync();
        Assert.Equal(TooManyRequestsText(6), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.Contains("No fue posible cargar los repartidores de esta orden. Intenta de nuevo.", await secondRow.InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(new[] { $"/api/v1/orders/{First.Id}/assignable-drivers", $"/api/v1/orders/{Second.Id}/assignable-drivers" }, api.Paths("GET", "/assignable-drivers"));
        Assert.Contains("Revisando repartidores…", await panel.Locator("table.opsBulkPreview tbody tr").Nth(2).InnerTextAsync(), StringComparison.Ordinal);

        // At 0 the third is read by itself; the second is never read again by itself.
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });
        await WaitForAsync(() => api.Paths("GET", "/assignable-drivers").Length == 3, "the third order was not read after the wait");
        Assert.Equal($"/api/v1/orders/{Third.Id}/assignable-drivers", api.Paths("GET", "/assignable-drivers")[2]);
        await retryList.WaitForAsync();
        await Task.Delay(1_000);
        Assert.Equal(3, api.Paths("GET", "/assignable-drivers").Length);

        // "Reintentar" meets the limit again: it stays, disabled until 0, and is not sent by itself.
        await retryList.ClickAsync();
        await countdown.GetByText(TooManyRequestsPattern()).WaitForAsync();
        await retryList.WaitForAsync();
        Assert.True(await retryList.IsDisabledAsync());
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 15_000 });
        await WaitForAsync(async () => !await retryList.IsDisabledAsync(), "Reintentar did not re-enable itself");
        await Task.Delay(1_000);
        Assert.Equal(4, api.Paths("GET", "/assignable-drivers").Length);
        await retryList.ClickAsync();
        await panel.Locator("ul.opsDriverChoices > li").First.WaitForAsync();
        Assert.Equal(
            new[] { First.Id, Second.Id, Third.Id, Second.Id, Second.Id },
            api.Paths("GET", "/assignable-drivers").Select(path => path.Split('/')[4]));

        // A server failure on the second order stops the run: the third is not sent.
        await panel.Locator($"input[type=radio][value=\"{DriverMoto}\"]").CheckAsync();
        // 1.15 × 100 is 114.99999999999999 in binary floating point; the retry resends 115.
        await CostInput(panel, First).FillAsync("40");
        await CostInput(panel, Second).FillAsync("1.15");
        await CostInput(panel, Third).FillAsync("42.25");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Revisar y asignar 3 órdenes", Exact = true }).ClickAsync();
        await ConfirmAsync(page, "¿Asignar 3 órdenes?", null, "Asignar 3 órdenes");
        await panel.Locator("p.notice[role=status]").GetByText(
            "Se asignaron 1 de 3 órdenes. Revisa el motivo de las demás en su fila. El envío se detuvo porque el servidor no respondió. Puedes reintentar las órdenes pendientes: se reenvían sin duplicarse.",
            new() { Exact = true }).WaitForAsync();
        Assert.Equal(
            new[]
            {
                $"{First.PublicId} | Centro | $1,001.13 MXN | $40.00 MXN | Asignada a DRV-1a2b3c4d",
                $"{Second.PublicId} | Norte | $250.00 MXN | $1.15 MXN | Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.",
                $"{Third.PublicId} | Centro | $50.00 MXN | $42.25 MXN | Sin enviar",
            },
            await PreviewRowsAsync(panel));
        Assert.Equal(2, api.Calls("POST", "/assignments").Length);

        // "Reintentar las pendientes": the second with its key and payload, then the third.
        await panel.GetByRole(AriaRole.Button, new() { Name = "Reintentar las pendientes", Exact = true }).ClickAsync();
        await panel.Locator("p.notice[role=status]").GetByText("Se asignaron las 3 órdenes.", new() { Exact = true }).WaitForAsync();
        var assignments = api.Calls("POST", "/assignments");
        Assert.Equal(
            new[] { First.Id, Second.Id, Second.Id, Third.Id },
            assignments.Select(call => call.Path.Split('/')[4]));
        Assert.Equal(assignments[1].IdempotencyKey, assignments[2].IdempotencyKey);
        Assert.Equal(assignments[1].Body, assignments[2].Body);
        Assert.Equal(new long[] { 4_000, 115, 115, 4_225 }, assignments.Select(call => CostCents(call.Body)));
        Assert.Equal(3, assignments.Select(call => call.IdempotencyKey).Distinct().Count());
        await ExpectEmptyAsync(page);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 work_inbox.bulk_add_to_route with UI-PHASE3-ROUTES-DAILY-2026-10-12: only the orders the batch assigned, to
    /// one of the driver's DRAFT routes, named by date and stops (today's in America/Mazatlan first, marked "hoy"), never
    /// by id; a preview and a confirmation; addRouteStop one order after another with the version of each answer.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Assigned_orders_go_to_the_drivers_route_of_today_in_Mazatlan_one_after_another()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // Tokyo is already on 3 November: today's route is the one of 2 November in Mazatlán.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.Clock.SetFixedTimeAsync(FixedNow);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page);
        api.Routes.AddRange(
        [
            new RouteRecord(RouteOlder, "2026-10-30", 2, ["c9000000-0000-4000-8000-000000000001"]),
            new RouteRecord(RouteToday, TodayInMazatlan, 4, ["c9000000-0000-4000-8000-000000000002"]),
            new RouteRecord(RouteUndated, null, 1, []),
        ]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        var panel = await AssignFirstAndThirdAsync(page);
        await panel.GetByRole(AriaRole.Button, new() { Name = "Agregar estas órdenes a una ruta…", Exact = true }).ClickAsync();
        var step = panel.Locator("section.opsBulkRoute");
        await step.GetByRole(AriaRole.Heading, new() { Name = "Agregar 2 órdenes de DRV-1a2b3c4d a una ruta", Exact = true }).WaitForAsync();
        await step.Locator("ul.opsDriverChoices > li").Nth(2).WaitForAsync();
        Assert.Equal(new[] { $"status=DRAFT&driver_id={DriverMoto}" }, api.Queries("GET", "/api/v1/routes"));

        // Today's route first, marked "hoy"; then the server's order, newest first; never an id or an amount.
        Assert.Equal(
            new[]
            {
                "Ruta del 2 de noviembre de 2026 (hoy) · 1 parada",
                "Ruta del 30 de octubre de 2026 · 1 parada",
                "Ruta sin fecha · sin paradas",
            },
            await TextsAsync(step.Locator("ul.opsDriverChoices > li")));
        Assert.Equal(
            "Primero las de hoy (hora de Mazatlán); las demás, de la más reciente a la más antigua.",
            (await step.Locator("fieldset p.fieldHint").First.InnerTextAsync()).Trim());
        Assert.Equal(0, await step.GetByRole(AriaRole.Button, new() { Name = "Crear ruta nueva", Exact = true }).CountAsync());
        await AssertNoIdentifiersAsync(page);

        await step.Locator($"input[type=radio][value=\"{RouteToday}\"]").CheckAsync();
        Assert.Contains(
            "2 órdenes de DRV-1a2b3c4d a «Ruta del 2 de noviembre de 2026». Se agregan al final de la ruta, una por una y en el orden de esta tabla.",
            await step.Locator("table caption").InnerTextAsync(),
            StringComparison.Ordinal);
        await step.GetByRole(AriaRole.Button, new() { Name = "Revisar y agregar 2 órdenes", Exact = true }).ClickAsync();
        await ConfirmAsync(
            page,
            "¿Agregar 2 órdenes a la ruta?",
            "Se agregarán 2 órdenes de DRV-1a2b3c4d al final de «Ruta del 2 de noviembre de 2026», que hoy tiene 1 parada, en el orden de la vista previa. Se envían una por una y cada fila mostrará su resultado.",
            "Agregar 2 órdenes");
        await step.Locator("p.notice[role=status]").GetByText("Se agregaron las 2 órdenes a la ruta.", new() { Exact = true }).WaitForAsync();
        var stops = api.Calls("POST", "/stops");
        Assert.All(stops, call => Assert.Equal($"/api/v1/routes/{RouteToday}/stops", call.Path));
        Assert.Equal(
            new[]
            {
                JsonSerializer.Serialize(new { order_id = First.Id, expected_version = 4 }),
                JsonSerializer.Serialize(new { order_id = Third.Id, expected_version = 5 }),
            },
            stops.Select(call => call.Body));
        Assert.All(stops, call => Assert.Matches(StopKeyPattern(), call.IdempotencyKey ?? string.Empty));
        Assert.NotEqual(stops[0].IdempotencyKey, stops[1].IdempotencyKey);
        Assert.Equal(new[] { "Agregada a la ruta", "Agregada a la ruta" }, await StepResultsAsync(step));
        Assert.Equal(
            $"/ops/routes?routeId={RouteToday}",
            await step.GetByRole(AriaRole.Link, new() { Name = "Ver la ruta en Rutas", Exact = true }).GetAttributeAsync("href"));
        Assert.Empty(api.Calls("POST", "/api/v1/routes"));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 work_inbox.bulk_add_to_route.create_route (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11,
    /// UI-PHASE3-ROUTES-DAILY-2026-10-12): the driver has only an older DRAFT route, the orders share one city (each read
    /// with getOrder, the city named by listCities), so "Crear ruta nueva" creates a DRAFT route for that driver, city
    /// and today's date in Mazatlán (never the device's nor the UTC date), selects it and goes on to its preview.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Without_a_route_of_today_the_step_creates_one_for_the_orders_city_dated_today_in_Mazatlan()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.Clock.SetFixedTimeAsync(FixedNow);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page);
        api.Routes.Add(new RouteRecord(RouteOlder, "2026-10-30", 2, ["c9000000-0000-4000-8000-000000000001"]));

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        var panel = await AssignFirstAndThirdAsync(page);
        await panel.GetByRole(AriaRole.Button, new() { Name = "Agregar estas órdenes a una ruta…", Exact = true }).ClickAsync();
        var step = panel.Locator("section.opsBulkRoute");
        var create = step.GetByRole(AriaRole.Button, new() { Name = "Crear ruta nueva", Exact = true });
        await create.WaitForAsync();
        Assert.Equal(new[] { "Ruta del 30 de octubre de 2026 · 1 parada" }, await TextsAsync(step.Locator("ul.opsDriverChoices > li")));
        Assert.Equal("DRV-1a2b3c4d no tiene una ruta en borrador con la fecha de hoy.", (await step.Locator(".opsBulkRouteCreate .emptyState").InnerTextAsync()).Trim());
        Assert.Equal(
            "«Crear ruta nueva» crea una ruta en borrador para DRV-1a2b3c4d en Mazatlán, SIN, la ciudad de las órdenes, con la fecha de hoy (hora de Mazatlán), y sigue a la vista previa. Ninguna orden se agrega hasta que confirmes.",
            (await step.Locator("#" + await create.GetAttributeAsync("aria-describedby")).InnerTextAsync()).Trim());
        // Each order was read for its city, one after another; the city is named by listCities (name and state), never
        // shown by its id.
        Assert.Equal(new[] { $"/api/v1/orders/{First.Id}", $"/api/v1/orders/{Third.Id}" }, api.Paths("GET", "/api/v1/orders/", exact: false, suffixFree: true));
        Assert.Single(api.Calls("GET", "/api/v1/cities"));
        await AssertNoIdentifiersAsync(page);

        await create.ClickAsync();
        await step.Locator("p.notice[role=status]").GetByText(
            "Se creó «Ruta del 2 de noviembre de 2026» en borrador para DRV-1a2b3c4d. Revisa la vista previa y confirma para agregar las órdenes.",
            new() { Exact = true }).WaitForAsync();
        var created = Assert.Single(api.Calls("POST", "/api/v1/routes"));
        Assert.Equal(
            JsonSerializer.Serialize(new { driver_id = DriverMoto, city_id = CityId, service_area_id = (string?)null, scheduled_for = TodayInMazatlan }),
            created.Body);
        Assert.Matches(CreateKeyPattern(), created.IdempotencyKey ?? string.Empty);
        // The created route is listed first as today's and selected; the older one is not.
        Assert.Equal(
            new[] { "Ruta del 2 de noviembre de 2026 (hoy) · sin paradas", "Ruta del 30 de octubre de 2026 · 1 parada" },
            await TextsAsync(step.Locator("ul.opsDriverChoices > li")));
        Assert.True(await step.Locator($"input[type=radio][value=\"{RouteCreated}\"]").IsCheckedAsync());
        Assert.Equal(0, await create.CountAsync());

        await step.GetByRole(AriaRole.Button, new() { Name = "Revisar y agregar 2 órdenes", Exact = true }).ClickAsync();
        await ConfirmAsync(
            page,
            "¿Agregar 2 órdenes a la ruta?",
            "Se agregarán 2 órdenes de DRV-1a2b3c4d al final de «Ruta del 2 de noviembre de 2026», que hoy no tiene paradas, en el orden de la vista previa. Se envían una por una y cada fila mostrará su resultado.",
            "Agregar 2 órdenes");
        await step.Locator("p.notice[role=status]").GetByText("Se agregaron las 2 órdenes a la ruta.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(
            new[]
            {
                JsonSerializer.Serialize(new { order_id = First.Id, expected_version = 1 }),
                JsonSerializer.Serialize(new { order_id = Third.Id, expected_version = 2 }),
            },
            api.Calls("POST", "/stops").Select(call => call.Body));
        Assert.All(api.Calls("POST", "/stops"), call => Assert.Equal($"/api/v1/routes/{RouteCreated}/stops", call.Path));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// UI-001 "tenant switch clears prior state" for a batch (AI-07 work_inbox.bulk_assignment, privacy): a switch while
    /// the run waits on an order closes the panel and clears the rows and the selection before the next organization
    /// answers; the orders not sent yet are never sent, under either organization, and the late answer shows nowhere.
    /// The next organization's role, PLATFORM_ADMIN with MFA (D5: the API answers it), starts with nothing selected
    /// and its own batch reads its own order's drivers.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_during_a_run_closes_the_batch_and_never_sends_its_pending_orders()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(
            page,
            (OrganizationA, OrganizationAName, "DISPATCHER"),
            (OrganizationB, OrganizationBName, "PLATFORM_ADMIN"));
        api.Seed(OrderOfB);
        var releaseSecond = api.HoldAssignment(Second.Id);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/inbox").AbsoluteUri);
        await ExpectGuidesAsync(page, First, Second, Third);
        await ExpectNavigationAsync(page, "DISPATCHER");
        Assert.Equal(new[] { "Todas", "Centro", "Norte" }, await ChipsAsync(page, "Zona de entrega"));
        await WaitForAsync(async () => await FirstTabAsync(page) == "Sin asignar 3 órdenes", "A's counts were not shown");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Seleccionar todas", Exact = true }).CheckAsync();
        await BulkButton(page).ClickAsync();
        var panel = Panel(page);
        await panel.Locator($"input[type=radio][value=\"{DriverMoto}\"]").CheckAsync();
        await CostInput(panel, First).FillAsync("40");
        await CostInput(panel, Second).FillAsync("41.10");
        await CostInput(panel, Third).FillAsync("42.25");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Revisar y asignar 3 órdenes", Exact = true }).ClickAsync();
        await ConfirmAsync(page, "¿Asignar 3 órdenes?", null, "Asignar 3 órdenes");

        // The first order is assigned; the run waits on the second.
        await WaitForAsync(() => api.Calls("POST", "/assignments").Length == 2, "the second order was not sent");
        await WaitForAsync(
            async () => (await PreviewRowsAsync(panel)).FirstOrDefault()?.EndsWith("Asignada a DRV-1a2b3c4d", StringComparison.Ordinal) == true,
            "the first order was not reported assigned");

        // The switch clears rows, zones, counts, the selection and the batch (AI-07 work_inbox.privacy) before B answers.
        var releaseOrganizationB = api.HoldReads(OrganizationB);
        await SwitchOrganizationAsync(page, OrganizationBName);
        await WaitForAsync(
            () => api.AllCalls.Any(call => call.Organization == OrganizationB && call.Path == "/api/v1/operations/dashboard"),
            "organization B was never read");
        Assert.Equal(0, await Panel(page).CountAsync());
        var during = await page.Locator("main").InnerTextAsync();
        Assert.All(new[] { First.PublicId, Second.PublicId, Third.PublicId }, guide => Assert.DoesNotContain(guide, during, StringComparison.Ordinal));
        Assert.DoesNotContain("seleccionadas: 3", during, StringComparison.Ordinal);
        Assert.DoesNotContain("3 órdenes", during, StringComparison.Ordinal);
        Assert.DoesNotContain("DRV-1a2b3c4d", during, StringComparison.Ordinal);
        Assert.Equal(0, await ZoneChipGroup(page).CountAsync());

        // The second order's late answer and the run's end show nowhere; the third order is never sent.
        releaseSecond.TrySetResult();
        releaseOrganizationB.TrySetResult();
        await ExpectGuidesAsync(page, OrderOfB);
        await ExpectFooterAsync(page, "Mostrando 1 de 1 sin asignar · seleccionadas: 0");
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        // Only B's zone and B's count: A's "Centro" and its counts went with the switch.
        Assert.Equal(new[] { "Todas", "Norte" }, await ChipsAsync(page, "Zona de entrega"));
        await WaitForAsync(async () => await FirstTabAsync(page) == "Sin asignar 1 orden", "B's counts were not shown");
        await Task.Delay(1_000);
        var assignments = api.Calls("POST", "/assignments");
        Assert.Equal(new[] { First.Id, Second.Id }, assignments.Select(call => call.Path.Split('/')[4]));
        Assert.All(assignments, call => Assert.Equal(OrganizationA, call.Organization));
        Assert.Equal(0, await Panel(page).CountAsync());
        Assert.True(await BulkButton(page).IsDisabledAsync());
        var after = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("Se asignaron", after, StringComparison.Ordinal);
        Assert.DoesNotContain("DRV-1a2b3c4d", after, StringComparison.Ordinal);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationB && call.Path.EndsWith("/assignable-drivers", StringComparison.Ordinal));

        // PLATFORM_ADMIN's own batch reads only its order's drivers, under B.
        await RowCheckbox(page, OrderOfB).CheckAsync();
        await BulkButton(page).ClickAsync();
        await panel.GetByRole(AriaRole.Heading, new() { Name = "Asignar 1 orden seleccionada", Exact = true }).WaitForAsync();
        await panel.Locator("ul.opsDriverChoices > li").First.WaitForAsync();
        var driverReads = api.AllCalls.Where(call => call.Path.EndsWith("/assignable-drivers", StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            new[] { (OrganizationA, First.Id), (OrganizationA, Second.Id), (OrganizationA, Third.Id), (OrganizationB, OrderOfB.Id) },
            driverReads.Select(call => (call.Organization, call.Path.Split('/')[4])));
        Assert.Equal(
            new[] { $"{OrderOfB.PublicId} | Norte | $99.00 MXN | [] | Lista para asignar | Quitar la orden {OrderOfB.PublicId} del lote" },
            await PreviewRowsAsync(panel));
        await panel.GetByRole(AriaRole.Button, new() { Name = "Cerrar", Exact = true }).ClickAsync();
        await panel.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Equal(2, api.Calls("POST", "/assignments").Length);

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

    /// <summary>Selects the first and third orders, assigns both to DRV-1a2b3c4d and returns the batch panel.</summary>
    private static async Task<ILocator> AssignFirstAndThirdAsync(IPage page)
    {
        await ExpectGuidesAsync(page, First, Second, Third);
        await RowCheckbox(page, First).CheckAsync();
        await RowCheckbox(page, Third).CheckAsync();
        await BulkButton(page).ClickAsync();
        var panel = Panel(page);
        await panel.Locator($"input[type=radio][value=\"{DriverMoto}\"]").CheckAsync();
        await CostInput(panel, First).FillAsync("45");
        await CostInput(panel, Third).FillAsync("30");
        await panel.GetByRole(AriaRole.Button, new() { Name = "Revisar y asignar 2 órdenes", Exact = true }).ClickAsync();
        await ConfirmAsync(page, "¿Asignar 2 órdenes?", null, "Asignar 2 órdenes");
        await panel.Locator("p.notice[role=status]").GetByText("Se asignaron las 2 órdenes.", new() { Exact = true }).WaitForAsync();
        return panel;
    }

    private static ILocator Panel(IPage page) => page.Locator("#inbox-bulk-assignment");

    /// <summary>The first queue tab as "label count", whitespace collapsed.</summary>
    private static async Task<string> FirstTabAsync(IPage page) =>
        Regex.Replace(await page.Locator("nav.opsInboxQueues a.opsInboxQueue").First.TextContentAsync() ?? string.Empty, "\\s+", " ").Trim();

    private static ILocator ZoneChipGroup(IPage page) =>
        page.Locator("fieldset.opsChipGroup:has(legend:text-is('Zona de entrega'))");

    private static async Task<string[]> ChipsAsync(IPage page, string group) =>
        [.. (await page.Locator($"fieldset.opsChipGroup:has(legend:text-is('{group}')) button.opsChip").AllInnerTextsAsync())
            .Select(text => text.Trim())];

    private static Task SwitchOrganizationAsync(IPage page, string name) =>
        page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = name });

    /// <summary>The shell's items (nav-items.ts over D5-CAPABILITY-MATRIX) for the two roles with the inbox, "Bandeja" current.</summary>
    private static async Task ExpectNavigationAsync(IPage page, string role)
    {
        var expected = role switch
        {
            "DISPATCHER" => NavigationOfDispatcher,
            "PLATFORM_ADMIN" => [.. NavigationOfDispatcher, "/finance/settlements"],
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "only the roles with the inbox"),
        };
        await WaitForAsync(
            async () => (await page.Locator("nav.appNav a").EvaluateAllAsync<string[]>(
                "links => links.map(link => link.getAttribute(\"href\"))")).SequenceEqual(expected),
            $"the shell did not offer the {role} items");
        Assert.Equal("page", await page.Locator("nav.appNav a[href=\"/ops/inbox\"]").GetAttributeAsync("aria-current"));
    }

    private static ILocator BulkButton(IPage page) => page.Locator("#inbox-bulk-assign");

    private static ILocator RowCheckbox(IPage page, BulkOrder order) =>
        page.GetByRole(AriaRole.Checkbox, new() { Name = $"Seleccionar la orden {order.PublicId}", Exact = true });

    private static ILocator CostInput(ILocator panel, BulkOrder order) => panel.Locator($"#bulk-cost-{order.Id}");

    private static ILocator OpenDialog(IPage page) => page.Locator("dialog.confirmDialog[open]");

    private static long CostCents(string? body)
    {
        using var document = JsonDocument.Parse(body ?? "{}");
        return document.RootElement.GetProperty("cost_cents").GetInt64();
    }

    private static async Task ConfirmAsync(IPage page, string title, string? description, string confirmLabel)
    {
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal(title, (await dialog.Locator("h2").InnerTextAsync()).Trim());
        if (description is not null) Assert.Equal(description, (await dialog.Locator("p").InnerTextAsync()).Trim());
        await dialog.GetByRole(AriaRole.Button, new() { Name = confirmLabel, Exact = true }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    private static async Task<string[]> TextsAsync(ILocator items) =>
        await items.EvaluateAllAsync<string[]>("items => items.map(item => item.textContent.replace(/\\s+/g, ' ').trim())");

    /// <summary>The preview rows: Guía, Destino, Total, the cost (typed field empty, or the sent amount), Resultado and the action.</summary>
    private static async Task<string[]> PreviewRowsAsync(ILocator panel) =>
        await panel.Locator("table.opsBulkPreview tbody tr").EvaluateAllAsync<string[]>(
            """
            rows => rows.map(row => [...row.cells].map(cell => {
              const input = cell.querySelector("input");
              const text = cell.textContent.replace(/\s+/g, " ").trim();
              const error = cell.querySelector("p.fieldError");
              return input === null ? text : `[${input.value}]` + (error === null ? "" : ` ${error.textContent.trim()}`);
            }).join(" | "))
            """);

    private static async Task<string[]> StepResultsAsync(ILocator step) =>
        await step.Locator("table tbody tr").EvaluateAllAsync<string[]>(
            "rows => rows.map(row => row.cells[row.cells.length - 1].textContent.replace(/\\s+/g, ' ').trim())");

    private static async Task ExpectGuidesAsync(IPage page, params BulkOrder[] orders)
    {
        var expected = orders.Select(order => order.PublicId).ToArray();
        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = [.. (await page.Locator("table.opsInboxTable a.opsInboxGuide").AllInnerTextsAsync()).Select(text => text.Trim())];
            if (actual.SequenceEqual(expected, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The inbox rows did not match: {string.Join(" | ", actual)}");
    }

    private static async Task ExpectEmptyAsync(IPage page) =>
        await page.Locator("main div.emptyState strong").GetByText("No hay órdenes sin asignar.", new() { Exact = true }).WaitForAsync();

    private static async Task ExpectFooterAsync(IPage page, string text) =>
        await page.Locator("main .opsInboxFooter p.opsSummaryNote").First.GetByText(text, new() { Exact = true }).WaitForAsync();

    private static Task ExpectTextAsync(ILocator scope, string text) =>
        scope.GetByText(text, new() { Exact = true }).WaitForAsync();

    /// <summary>
    /// No internal id is visible anywhere on the page: no UUID, nor the first 8 characters of any synthetic organization,
    /// zone, city, driver, route or order id. Only guías, DRV- references and route dates are shown.
    /// </summary>
    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        foreach (var id in new[]
                 {
                     OrganizationA, OrganizationB, ZoneCentro, ZoneNorte, CityId, DriverMoto, DriverCar, DriverVan,
                     RouteToday, RouteOlder, RouteUndated, RouteCreated, First.Id, Second.Id, Third.Id, OrderOfB.Id,
                 })
        {
            Assert.DoesNotContain(id[..8], visible, StringComparison.Ordinal);
        }
    }

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

    /// <summary>
    /// The in-memory operations session (Mock mode, as the other panel suites), by default DISPATCHER of organization A
    /// only, and the fulfilled API with A's three orders. Playwright runs the last matching route first, so the
    /// catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<BulkApi> InstallAsync(IPage page, params (string Id, string Name, string Role)[] organizations)
    {
        if (organizations.Length == 0) organizations = [(OrganizationA, OrganizationAName, "DISPATCHER")];
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-work-inbox-bulk-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        var api = new BulkApi();
        api.Seed(First, Second, Third);
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", route => FulfillAsync(route, organizations.Select((organization, index) => new
        {
            organization_id = organization.Id,
            display_name = organization.Name,
            role = organization.Role,
            is_default = index == 0,
        }).ToArray()));
        await page.RouteAsync("**/api/v1/operations/**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/orders/**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/routes**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/cities**", api.HandleAsync);
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

    [GeneratedRegex("^assign-driver-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex AssignmentKeyPattern();

    [GeneratedRegex("^add-route-stop-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex StopKeyPattern();

    [GeneratedRegex("^create-route-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex CreateKeyPattern();

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    private sealed record BulkOrder(
        string Id,
        string PublicId,
        string Status,
        string ZoneId,
        long TotalCents,
        string UpdatedAt,
        string Organization = OrganizationA)
    {
        internal bool Assigned { get; set; }

        internal object Json() => new
        {
            order_id = Id,
            aggregate_version = 3,
            public_id = PublicId,
            owner = new { organization_id = Organization, display_name = Organization == OrganizationA ? OrganizationAName : OrganizationBName },
            @operator = (object?)null,
            client = (object?)null,
            status = Assigned ? "ASSIGNED" : Status,
            created_at = "2026-11-01T18:00:00Z",
            updated_at = UpdatedAt,
            service_type = "SAME_DAY",
            pickup_window = (object?)null,
            delivery_window = (object?)null,
            delivery_zone = new { operating_zone_id = ZoneId, name = ZoneId == ZoneCentro ? "Centro" : "Norte", zone_type = "CORE" },
            assignment = Assigned
                ? new
                {
                    assignment_id = $"a6{Id[2..]}",
                    assignment_type = "OWN",
                    status = "ACCEPTED",
                    driver_id = DriverMoto,
                    driver_reference = "DRV-1a2b3c4d",
                }
                : null,
            latest_driver_location = (object?)null,
            total = new { currency = "MXN", amount_cents = TotalCents },
            cost_warning = (string?)null,
            unassigned_alert = !Assigned,
        };

        /// <summary>AI-05 Order as getOrder returns it: the screen reads only its city_id.</summary>
        internal object Detail() => new
        {
            id = Id,
            public_id = PublicId,
            owner_org_id = Organization,
            operator_org_id = (string?)null,
            status = Assigned ? "ASSIGNED" : Status,
            price_net = new { currency = "MXN", amount_cents = TotalCents },
            version = 4,
            origin_location_id = "61000000-0000-4000-8000-000000000001",
            destination_location_id = "62000000-0000-4000-8000-000000000001",
            service_type = "SAME_DAY",
            quote_id = "63000000-0000-4000-8000-000000000001",
            city_id = CityId,
            service_area_id = (string?)null,
            pricing_tier = "OCCASIONAL",
            total = new { currency = "MXN", amount_cents = TotalCents },
            claim_window_ends_at = (string?)null,
            finalized_at = (string?)null,
            service_window = (object?)null,
            timeline = Array.Empty<object>(),
        };
    }

    /// <summary>A DRAFT route of DRV-1a2b3c4d, its stops by order id; addRouteStop appends one and moves the version.</summary>
    private sealed record RouteRecord(string Id, string? ScheduledFor, int Version, List<string> OrderIds)
    {
        internal int Version { get; set; } = Version;

        internal object Summary() => new
        {
            id = Id,
            status = "DRAFT",
            version = Version,
            driver_id = DriverMoto,
            city_id = CityId,
            service_area_id = (string?)null,
            scheduled_for = ScheduledFor,
            assignment_cost_cents_total = OrderIds.Count * 1_000L,
            stop_count = OrderIds.Count,
        };

        internal object Detail() => new
        {
            id = Id,
            status = "DRAFT",
            version = Version,
            driver_id = DriverMoto,
            city_id = CityId,
            service_area_id = (string?)null,
            scheduled_for = ScheduledFor,
            assignment_cost_cents_total = OrderIds.Count * 1_000L,
            stop_count = OrderIds.Count,
            stops = OrderIds.Select((orderId, index) => new
            {
                id = $"e{index + 1:x7}-0000-4000-8000-{orderId[^12..]}",
                order_id = orderId,
                sequence = index + 1,
                stop_type = "DELIVERY",
                status = "PENDING",
            }).ToArray(),
        };
    }

    private sealed record BulkCall(string Organization, string Method, string Path, string Query, string? IdempotencyKey, string? Body);

    /// <summary>The inbox read model and counts, the assignment operations and the Routing and Locations reads and writes.</summary>
    private sealed class BulkApi
    {
        private static readonly string[] OrderStatuses =
        [
            "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING",
            "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED", "DELIVERED", "CLOSED", "CLAIM_OPEN",
            "CLAIM_RESOLVED", "CANCELLED",
        ];

        private readonly object _gate = new();
        private readonly List<BulkCall> _calls = [];
        private readonly List<BulkOrder> _orders = [];
        private readonly Dictionary<string, (string Body, object Answer, int Status)> _replays = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _readHolds = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _assignHolds = new();

        /// <summary>Scripted assignDriver answers per order ("503", "409:CODE"); an empty queue assigns.</summary>
        internal Dictionary<string, Queue<string>> AssignAnswers { get; } = [];

        /// <summary>How many of an order's next driver lists answer 429 with Retry-After: 6.</summary>
        internal Dictionary<string, int> DriverListTooManyRequests { get; } = [];

        internal List<RouteRecord> Routes { get; } = [];

        internal void Seed(params BulkOrder[] orders)
        {
            lock (_gate) _orders.AddRange(orders.Select(order => order with { }));
        }

        internal BulkCall[] AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        /// <summary>The organization's inbox and counts reads wait until the returned source is set.</summary>
        internal TaskCompletionSource HoldReads(string organization) =>
            _readHolds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>The order's next assignDriver answer waits until the returned source is set.</summary>
        internal TaskCompletionSource HoldAssignment(string orderId) =>
            _assignHolds.GetOrAdd(orderId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal BulkCall[] Calls(string method, string pathPart)
        {
            lock (_gate)
            {
                return [.. _calls.Where(call => call.Method == method &&
                    (pathPart.StartsWith("/api/", StringComparison.Ordinal) ? call.Path == pathPart : call.Path.EndsWith(pathPart, StringComparison.Ordinal)))];
            }
        }

        /// <summary>The paths of the calls; <paramref name="suffixFree"/> keeps only paths that end right after an id.</summary>
        internal string[] Paths(string method, string pathPart, bool exact = false, bool suffixFree = false)
        {
            lock (_gate)
            {
                return [.. _calls
                    .Where(call => call.Method == method)
                    .Where(call => suffixFree
                        ? Regex.IsMatch(call.Path, $"^{Regex.Escape(pathPart)}[0-9a-f-]{{36}}$")
                        : exact ? call.Path == pathPart : call.Path.EndsWith(pathPart, StringComparison.Ordinal))
                    .Select(call => call.Path)];
            }
        }

        internal string[] Queries(string method, string path)
        {
            lock (_gate) return [.. _calls.Where(call => call.Method == method && call.Path == path).Select(call => call.Query)];
        }

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var headers = await request.AllHeadersAsync();
            var organization = headers.GetValueOrDefault("x-organization-id") ?? string.Empty;
            var call = new BulkCall(
                organization,
                request.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                headers.GetValueOrDefault("idempotency-key"),
                request.PostData);
            lock (_gate) _calls.Add(call);
            var segments = call.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Like RLS, an order of another organization is not found.
            if (segments is ["api", "v1", "orders", var scoped, ..] && Find(organization, scoped) is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            switch (call.Method, segments)
            {
                // The suite stays on "Sin asignar": its one dashboard query is unassigned=true, and AI-05
                // getOperationsQueueCounts takes no query string (any is 400). Anything else is refused, so a wrong
                // query fails the screen instead of passing unseen.
                case ("GET", ["api", "v1", "operations", "dashboard"]) when call.Query != "unassigned=true":
                case ("GET", ["api", "v1", "operations", "queue-counts"]) when call.Query.Length > 0:
                    await ProblemAsync(route, 400);
                    return;
                case ("GET", ["api", "v1", "operations", "dashboard"]):
                    await WaitForReadsAsync(organization);
                    await DashboardAsync(route, organization);
                    return;
                case ("GET", ["api", "v1", "operations", "queue-counts"]):
                    await WaitForReadsAsync(organization);
                    await CountsAsync(route, organization);
                    return;
                case ("GET", ["api", "v1", "orders", var orderId, "assignable-drivers"]):
                    await DriversAsync(route, orderId);
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "assignments"]):
                    await AssignAsync(route, call, orderId);
                    return;
                case ("GET", ["api", "v1", "orders", var orderId]):
                    await FulfillAsync(route, Find(organization, orderId)!.Detail());
                    return;
                case ("GET", ["api", "v1", "cities"]):
                    await FulfillAsync(route, new[]
                    {
                        new { id = CityId, name = "Mazatlán", state_code = "SIN", country_code = "MX", timezone = "America/Mazatlan" },
                    });
                    return;
                case ("GET", ["api", "v1", "routes"]):
                    RouteRecord[] routes;
                    lock (_gate) routes = [.. Routes];
                    if (call.Query != $"status=DRAFT&driver_id={DriverMoto}")
                    {
                        await ProblemAsync(route, 409, "INVALID_REQUEST");
                        return;
                    }

                    await FulfillAsync(route, new { items = routes.Select(item => item.Summary()).ToArray(), next_cursor = (string?)null });
                    return;
                case ("GET", ["api", "v1", "routes", var routeId]):
                    RouteRecord? found;
                    lock (_gate) found = Routes.FirstOrDefault(item => item.Id == routeId);
                    if (found is null) await ProblemAsync(route, 404);
                    else await FulfillAsync(route, found.Detail());
                    return;
                case ("POST", ["api", "v1", "routes"]):
                    await CreateRouteAsync(route, call);
                    return;
                case ("POST", ["api", "v1", "routes", var routeId, "stops"]):
                    await AddStopAsync(route, call, routeId);
                    return;
                default:
                    await ProblemAsync(route, 404);
                    return;
            }
        }

        private BulkOrder? Find(string organization, string orderId)
        {
            lock (_gate) return _orders.FirstOrDefault(order => order.Id == orderId && order.Organization == organization);
        }

        private async Task WaitForReadsAsync(string organization)
        {
            if (_readHolds.TryGetValue(organization, out var hold)) await hold.Task;
        }

        private async Task DashboardAsync(IRoute route, string organization)
        {
            BulkOrder[] listed;
            lock (_gate) listed = [.. _orders.Where(order => order.Organization == organization && !order.Assigned)];
            await FulfillAsync(route, new
            {
                generated_at = "2026-11-02T06:40:00Z",
                items = listed.Select(order => order.Json()).ToArray(),
                next_cursor = (string?)null,
            });
        }

        private async Task CountsAsync(IRoute route, string organization)
        {
            BulkOrder[] orders;
            lock (_gate) orders = [.. _orders.Where(order => order.Organization == organization)];
            var byStatus = OrderStatuses.ToDictionary(
                status => status,
                status => orders.Count(order => (order.Assigned ? "ASSIGNED" : order.Status) == status));
            await FulfillAsync(route, new
            {
                generated_at = "2026-11-02T06:40:00Z",
                total = orders.Length,
                by_status = byStatus,
                queues = new
                {
                    unassigned = orders.Count(order => !order.Assigned),
                    needs_attention = byStatus["RESCHEDULED"],
                    price_review = 0,
                    delivered_not_closed = 0,
                    en_route = 0,
                },
            });
        }

        /// <summary>
        /// Every order lists the motorcycle (eligible) and the car, which has an expired document for the second order;
        /// the van is eligible where it is listed, but the third order's list does not name it.
        /// </summary>
        private async Task DriversAsync(IRoute route, string orderId)
        {
            bool limited;
            lock (_gate)
            {
                limited = DriverListTooManyRequests.GetValueOrDefault(orderId) > 0;
                if (limited) DriverListTooManyRequests[orderId] -= 1;
            }

            if (limited)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 429,
                    ContentType = "application/problem+json",
                    Headers = new Dictionary<string, string>
                    {
                        ["Retry-After"] = "6",
                        ["Cache-Control"] = "no-store",
                        ["Access-Control-Expose-Headers"] = "Retry-After",
                    },
                    Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Too Many Requests", status = 429, traceId = new string('0', 32) }),
                });
                return;
            }

            var drivers = new List<object>
            {
                Driver(DriverMoto, "DRV-1a2b3c4d", "MOTORCYCLE", orderId == Third.Id ? 1 : 0),
                orderId == Second.Id
                    ? Driver(DriverCar, "DRV-5e6f7a8b", "CAR", 2, "DOCUMENT_EXPIRED")
                    : Driver(DriverCar, "DRV-5e6f7a8b", "CAR", 2),
            };
            if (orderId != Third.Id) drivers.Add(Driver(DriverVan, "DRV-9c0d1e2f", "VAN", 0));
            await FulfillAsync(route, new { items = drivers, next_cursor = (string?)null });
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

        private async Task AssignAsync(IRoute route, BulkCall call, string orderId)
        {
            string? answer = null;
            lock (_gate)
            {
                if (AssignAnswers.TryGetValue(orderId, out var answers) && answers.Count > 0) answer = answers.Dequeue();
            }

            if (answer is not null)
            {
                var parts = answer.Split(':', 2);
                await ProblemAsync(route, int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts.Length == 2 ? parts[1] : null);
                return;
            }

            if (_assignHolds.TryRemove(orderId, out var hold)) await hold.Task;
            if (await ReplayAsync(route, call)) return;
            using var body = JsonDocument.Parse(call.Body ?? "{}");
            var cost = body.RootElement.GetProperty("cost_cents").GetInt64();
            var assignment = new
            {
                id = $"a6{orderId[2..]}",
                order_id = orderId,
                driver_id = body.RootElement.GetProperty("driver_id").GetString(),
                route_id = (string?)null,
                status = "ACCEPTED",
                cost = new { currency = "MXN", amount_cents = cost },
            };
            lock (_gate)
            {
                _orders.First(order => order.Id == orderId).Assigned = true;
                _replays[call.IdempotencyKey!] = (call.Body!, assignment, 201);
            }

            try
            {
                await FulfillAsync(route, assignment, 201);
            }
            catch (PlaywrightException)
            {
                // The page gave up on a held request (the batch closed); the server still assigned the order.
            }
        }

        private async Task CreateRouteAsync(IRoute route, BulkCall call)
        {
            if (await ReplayAsync(route, call)) return;
            using var body = JsonDocument.Parse(call.Body ?? "{}");
            var created = new RouteRecord(RouteCreated, body.RootElement.GetProperty("scheduled_for").GetString(), 1, []);
            lock (_gate)
            {
                Routes.Insert(0, created);
                _replays[call.IdempotencyKey!] = (call.Body!, created.Summary(), 201);
            }

            await FulfillAsync(route, created.Summary(), 201);
        }

        private async Task AddStopAsync(IRoute route, BulkCall call, string routeId)
        {
            if (await ReplayAsync(route, call)) return;
            using var body = JsonDocument.Parse(call.Body ?? "{}");
            RouteRecord? found;
            object answer;
            lock (_gate)
            {
                found = Routes.FirstOrDefault(item => item.Id == routeId);
                if (found is null || body.RootElement.GetProperty("expected_version").GetInt32() != found.Version)
                {
                    answer = null!;
                }
                else
                {
                    found.OrderIds.Add(body.RootElement.GetProperty("order_id").GetString()!);
                    found.Version += 1;
                    answer = found.Detail();
                    _replays[call.IdempotencyKey!] = (call.Body!, answer, 201);
                }
            }

            if (found is null) await ProblemAsync(route, 404);
            else if (answer is null) await ProblemAsync(route, 409, "VERSION_CONFLICT");
            else await FulfillAsync(route, answer, 201);
        }

        /// <summary>A key already answered replays its answer for the same body and is IDEMPOTENCY_CONFLICT otherwise.</summary>
        private async Task<bool> ReplayAsync(IRoute route, BulkCall call)
        {
            if (call.IdempotencyKey is null)
            {
                await ProblemAsync(route, 409, "INVALID_REQUEST");
                return true;
            }

            (string Body, object Answer, int Status) replay;
            lock (_gate)
            {
                if (!_replays.TryGetValue(call.IdempotencyKey, out replay)) return false;
            }

            if (replay.Body == call.Body) await FulfillAsync(route, replay.Answer, replay.Status);
            else await ProblemAsync(route, 409, "IDEMPOTENCY_CONFLICT");
            return true;
        }
    }
}
