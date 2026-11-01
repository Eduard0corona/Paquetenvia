using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for AI-07 <c>settlement_desk</c>
/// (<c>/finance/settlements</c>, UI-PHASE3-DRIVERS-2026-10-11, SET-001, D7-SETTLEMENT-RULES, D7-SETTLEMENT-MFA) in a
/// real browser against the production Next build. The API is fulfilled in the browser with AI-05 shapes
/// (listSettlements, getSettlement, createSettlement, listSettlementPayees, addSettlementAdjustment,
/// approveSettlement, markSettlementPaid, voidSettlement, exportSettlementCsv, /me/organization-contexts): FINANCE
/// calculates "Semana pasada" and "Quincena pasada" computed in Mazatlán and spelled out in readable dates on a device
/// in another zone, picks the payee by its DRV- reference (the id is sent, never shown), confirms in the dialog, adds
/// adjustments typed in pesos and sent as integer cents, sees a negative total refused, approves and pays only after
/// the MFA step-up, voids keeping the lines and downloads the CSV export; a 429 on the export counts down on the export
/// only and is never sent again by itself; a failed payee list says so and names payees "sin referencia"; DISPATCHER,
/// VIEWER and DRIVER have no access; an organization switch clears settlements, payees and keys. The screen calls only
/// AI-05 paths, keeps nothing in browser storage, logs nothing, shows every time in America/Mazatlan and /finance is
/// never stored.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class SettlementDeskPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    private static readonly SettlementPayee Motorcycle = new("9a7e0001-0000-4000-8000-0000000000e1", "DRV-9a8b7c6d", "MOTORCYCLE");
    private static readonly SettlementPayee Car = new("9a7e0002-0000-4000-8000-0000000000e2", "DRV-1f2e3d4c", "CAR");
    private static readonly SettlementPayee OtherOrganizationVan = new("9a7e00b1-0000-4000-8000-0000000000f1", "DRV-77aa88bb", "VAN");

    /// <summary>A payee listSettlementPayees does not return (the screen names it "sin referencia").</summary>
    private const string UnlistedPayeeId = "9a7e00ff-0000-4000-8000-0000000000ff";

    private const string CalculatedId = "5e771e01-0000-4000-8000-000000000001";
    private const string PaidId = "5e771e02-0000-4000-8000-000000000002";
    private const string UnlistedPayeeSettlementId = "5e771e03-0000-4000-8000-000000000003";
    private const string VoidableId = "5e771e04-0000-4000-8000-000000000004";
    private const string CreatedId = "5e771e05-0000-4000-8000-000000000005";
    private const string OtherOrganizationSettlementId = "5e771eb1-0000-4000-8000-0000000000b1";

    /// <summary>
    /// 05:30 UTC on Monday 16 November 2026: still Sunday 15 November, 22:30, in Mazatlán and already Monday 16 November,
    /// 14:30, in Tokyo, so both presets differ between the two zones.
    /// </summary>
    private const string FixedNow = "2026-11-16T05:30:00Z";

    private const string RetryMessage =
        "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.";

    /// <summary>The AI-05 operations of settlement_desk and the shell's role read; any other call is a failure.</summary>
    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/settlements$"),
        new("^POST /api/v1/settlements$"),
        new("^GET /api/v1/settlement-payees$"),
        new("^GET /api/v1/settlements/[0-9a-f-]{36}$"),
        new("^POST /api/v1/settlements/[0-9a-f-]{36}/(adjustments|approve|pay|void)$"),
        new("^GET /api/v1/settlements/[0-9a-f-]{36}/export\\.csv$"),
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

    private static readonly string[][] OrganizationARows =
    [
        ["20–26 oct 2026", "Calculada · $75.50 MXN", "Repartidor DRV-9a8b7c6d · 2 línea(s)"],
        ["2–14 oct 2026", "Pagada · $120.00 MXN", "Repartidor DRV-1f2e3d4c · 1 línea(s)"],
        ["17–29 sep 2026", "Aprobada · $99.00 MXN", "Repartidor sin referencia · 1 línea(s)"],
        ["2–14 sep 2026", "Calculada · $120.00 MXN", "Repartidor DRV-1f2e3d4c · 1 línea(s)"],
    ];

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Finance_calculates_Mazatlan_presets_by_DRV_reference_and_adjusts_in_integer_cents()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale, at a moment when Tokyo is already a day ahead of Mazatlán.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.Clock.SetFixedTimeAsync(FixedNow);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "FINANCE"));
        SeedOrganizationA(api);

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        Assert.NotNull(response);
        // /finance is never stored (next.config.ts privateNoStore).
        Assert.Contains("no-store", (await response.AllHeadersAsync())["cache-control"], StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidaciones", Exact = true, Level = 1 }).WaitForAsync();
        await ExpectNavigationAsync(page, "FINANCE");
        await ExpectRowsAsync(page, OrganizationARows);

        // The payee picker and the filter offer the payees by DRV- reference and vehicle, sorted by reference.
        var create = CreateForm(page);
        await ExpectOptionsAsync(
            create.Locator("select[name=driver_id]"),
            "Elige un repartidor", "DRV-1f2e3d4c · Automóvil", "DRV-9a8b7c6d · Motocicleta");
        await create.GetByText("Los repartidores se muestran por su referencia y su vehículo.", new() { Exact = true }).WaitForAsync();
        await ExpectOptionsAsync(
            FiltersForm(page).Locator("select[name=payee_id]"),
            "Todos", "DRV-1f2e3d4c · Automóvil", "DRV-9a8b7c6d · Motocicleta");

        // The presets are the closed week and half month before today in Mazatlán, spelled out before anything is sent.
        Assert.Equal(
            new[] { "Semana pasada: 2–8 nov 2026 (lunes a domingo)", "Quincena pasada: 16–31 oct 2026", "Fechas a mano" },
            (await create.Locator("label.opsCheckbox").AllInnerTextsAsync()).Select(text => text.Trim()));

        // The filter sends the picked payee's id and the status; "Todos" goes back to every settlement.
        var filters = FiltersForm(page);
        await filters.Locator("select[name=status]").SelectOptionAsync(new SelectOptionValue { Label = "Pagada" });
        await filters.Locator("select[name=payee_id]")
            .SelectOptionAsync(new SelectOptionValue { Label = "DRV-1f2e3d4c · Automóvil" });
        await filters.GetByRole(AriaRole.Button, new() { Name = "Aplicar filtros", Exact = true }).ClickAsync();
        await ExpectRowsAsync(page, OrganizationARows[1]);
        Assert.Equal($"payee_id={Car.Id}&status=PAID", api.Calls(OrganizationA, "GET", "/api/v1/settlements")[^1].Query);
        await filters.Locator("select[name=status]").SelectOptionAsync(new SelectOptionValue { Label = "Todos" });
        await filters.Locator("select[name=payee_id]").SelectOptionAsync(new SelectOptionValue { Label = "Todos" });
        await filters.GetByRole(AriaRole.Button, new() { Name = "Aplicar filtros", Exact = true }).ClickAsync();
        await ExpectRowsAsync(page, OrganizationARows);

        // Typed dates whose end comes before the start are refused before any dialog or request.
        await create.Locator("select[name=driver_id]")
            .SelectOptionAsync(new SelectOptionValue { Label = "DRV-9a8b7c6d · Motocicleta" });
        await create.Locator("input[name=period][value=manual]").CheckAsync();
        await create.Locator("input[name=period_from]").FillAsync("2026-11-10");
        await create.Locator("input[name=period_to]").FillAsync("2026-11-09");
        await create.GetByRole(AriaRole.Button, new() { Name = "Calcular", Exact = true }).ClickAsync();
        await ExpectErrorsAsync(page, "El fin del periodo no puede ser anterior al inicio.");
        Assert.Equal(0, await OpenDialog(page).CountAsync());

        // "Quincena pasada" names the payee and the exact period; cancelling sends nothing.
        await create.Locator("input[name=period][value=last_fortnight]").CheckAsync();
        await create.GetByRole(AriaRole.Button, new() { Name = "Calcular", Exact = true }).ClickAsync();
        await ExpectDialogAsync(
            page,
            "¿Calcular liquidación?",
            "Se calculará la liquidación del repartidor DRV-9a8b7c6d (motocicleta) del 16 de octubre de 2026 al " +
            "31 de octubre de 2026 (días completos, hora de Mazatlán).");
        await OpenDialog(page).GetByRole(AriaRole.Button, new() { Name = "Cancelar", Exact = true }).ClickAsync();
        await OpenDialog(page).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Empty(api.Calls(OrganizationA, "POST", "/api/v1/settlements"));

        // "Semana pasada" is calculated: the id of the picked payee is sent, never shown.
        await create.Locator("input[name=period][value=last_week]").CheckAsync();
        await create.GetByRole(AriaRole.Button, new() { Name = "Calcular", Exact = true }).ClickAsync();
        await ExpectDialogAsync(
            page,
            "¿Calcular liquidación?",
            "Se calculará la liquidación del repartidor DRV-9a8b7c6d (motocicleta) del 2 de noviembre de 2026 al " +
            "8 de noviembre de 2026 (días completos, hora de Mazatlán).");
        await ConfirmOpenDialogAsync(page, "Calcular");
        await ExpectMessageAsync(page, "Cambio guardado.");
        var created = Assert.Single(api.Calls(OrganizationA, "POST", "/api/v1/settlements"));
        Assert.Matches(UuidPattern(), created.IdempotencyKey ?? string.Empty);
        Assert.Equal(
            $"{{\"driver_id\":\"{Motorcycle.Id}\",\"period_from\":\"2026-11-02\",\"period_to\":\"2026-11-08\"}}",
            created.Body);
        await ExpectDetailAsync(
            page,
            "Liquidación 5e771e05",
            "Calculada · Repartidor DRV-9a8b7c6d · 2–8 nov 2026 · creada 15 nov 2026, 22:31",
            "Total $42.00 MXN",
            ["Entrega", "7a000005", "$42.00 MXN", "15 nov 2026, 22:31"]);
        await ExpectRowsAsync(page, [["2–8 nov 2026", "Calculada · $42.00 MXN", "Repartidor DRV-9a8b7c6d · 1 línea(s)"], .. OrganizationARows]);
        await page.Locator("main p.noticeInfo")
            .GetByText("Aprobar y marcar pagada requieren verificar tu identidad (MFA).", new() { Exact = true })
            .WaitForAsync();

        // An adjustment must be a non-zero amount and its reason may not start or end with spaces; nothing is sent.
        await SubmitAdjustmentAsync(page, "0", "Motivo sintético");
        await ExpectErrorsAsync(page, "El ajuste debe ser un monto en MXN distinto de cero, con signo opcional y hasta 2 decimales.");
        await SubmitAdjustmentAsync(page, "5", " Motivo sintético");
        await ExpectDialogAsync(
            page,
            "¿Agregar ajuste?",
            "Se sumará $5.00 MXN a la liquidación del repartidor DRV-9a8b7c6d (2–8 nov 2026).");
        await ConfirmOpenDialogAsync(page, "Agregar ajuste");
        await ExpectErrorsAsync(
            page,
            "El motivo es obligatorio (máximo 500 caracteres) y no puede iniciar ni terminar con espacios.");
        Assert.Empty(api.Calls(OrganizationA, "POST", "/adjustments"));

        // -12.35 pesos is -1235 cents exactly (-12.35 × 100 is -1234.9999999999998 in floating point).
        await SubmitAdjustmentAsync(page, "-12.35", "Descuento sintético");
        await ExpectDialogAsync(
            page,
            "¿Agregar ajuste?",
            "Se descontará $12.35 MXN a la liquidación del repartidor DRV-9a8b7c6d (2–8 nov 2026).");
        await ConfirmOpenDialogAsync(page, "Agregar ajuste");
        await ExpectMessageAsync(page, "Cambio guardado.");
        var adjustment = Assert.Single(api.Calls(OrganizationA, "POST", "/adjustments"));
        Assert.Equal($"/api/v1/settlements/{CreatedId}/adjustments", adjustment.Path);
        Assert.Equal("{\"amount_cents\":-1235,\"reason\":\"Descuento sintético\"}", adjustment.Body);
        await ExpectDetailAsync(
            page,
            "Liquidación 5e771e05",
            "Calculada · Repartidor DRV-9a8b7c6d · 2–8 nov 2026 · creada 15 nov 2026, 22:31",
            "Total $29.65 MXN",
            ["Entrega", "7a000005", "$42.00 MXN", "15 nov 2026, 22:31"],
            ["Ajuste", "—", "-$12.35 MXN", "15 nov 2026, 22:40"]);

        // A total below zero is refused by the API with its own message; the settlement is read again, unchanged.
        await SubmitAdjustmentAsync(page, "-100", "Descuento excesivo sintético");
        await ExpectDialogAsync(
            page,
            "¿Agregar ajuste?",
            "Se descontará $100.00 MXN a la liquidación del repartidor DRV-9a8b7c6d (2–8 nov 2026).");
        await ConfirmOpenDialogAsync(page, "Agregar ajuste");
        await ExpectMessageAsync(page, "El total de la liquidación no puede quedar negativo.");
        Assert.Equal(
            "{\"amount_cents\":-10000,\"reason\":\"Descuento excesivo sintético\"}",
            api.Calls(OrganizationA, "POST", "/adjustments")[^1].Body);
        await WaitForAsync(
            () => api.Calls(OrganizationA, "GET", $"/api/v1/settlements/{CreatedId}").Length >= 1,
            "the settlement was not read again after the conflict");
        await page.Locator("main section.opsRouteDetail header strong")
            .GetByText("Total $29.65 MXN", new() { Exact = true }).WaitForAsync();

        await AssertNoPayeeIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        Assert.All(api.AllCalls, call => Assert.Equal(OrganizationA, call.Organization));
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Approve_and_pay_ask_for_MFA_and_state_the_amount_void_keeps_the_lines_and_the_CSV_downloads()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            TimezoneId = "America/New_York",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "FINANCE"));
        SeedOrganizationA(api);
        api.WritesNeedMfa = true;

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        await ExpectRowsAsync(page, OrganizationARows);
        await SelectAsync(page, 0);
        await ExpectDetailAsync(
            page,
            "Liquidación 5e771e01",
            "Calculada · Repartidor DRV-9a8b7c6d · 20–26 oct 2026 · creada 27 oct 2026, 23:30",
            "Total $75.50 MXN",
            ["Entrega", "7a000001", "$45.50 MXN", "27 oct 2026, 23:30"],
            ["Entrega", "7a000002", "$30.00 MXN", "27 oct 2026, 23:30"]);
        Assert.Equal(new[] { "Aprobar", "Exportar CSV", "Cerrar" }, await DetailButtonsAsync(page));

        // Approving needs a second factor: the dialog states the total and the step-up returns to this settlement.
        await DetailButton(page, "Aprobar").ClickAsync();
        await ExpectDialogAsync(
            page,
            "¿Aprobar liquidación?",
            "Se aprobará la liquidación del repartidor DRV-9a8b7c6d (20–26 oct 2026) por un total de $75.50 MXN. " +
            "Después ya no se podrán agregar ajustes.");
        await ConfirmOpenDialogAsync(page, "Aprobar");
        await ExpectMessageAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        Assert.Equal(
            $"/login?mfa=required&return_url=%2Ffinance%2Fsettlements%3Fsettlement%3D{CalculatedId}",
            await page.Locator("main p.notice[role=status]")
                .GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true })
                .GetAttributeAsync("href"));
        await ExpectStatusLineAsync(page, "Calculada · ");

        // With the second factor verified, the return URL reopens the settlement by itself.
        api.WritesNeedMfa = false;
        await page.GotoAsync(new Uri(server.BaseAddress, $"/finance/settlements?settlement={CalculatedId}").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidación 5e771e01", Exact = true }).WaitForAsync();
        await DetailButton(page, "Aprobar").ClickAsync();
        await ConfirmOpenDialogAsync(page, "Aprobar");
        await ExpectStatusLineAsync(page, "Aprobada · ");
        Assert.Equal(new[] { "Marcar pagada", "Exportar CSV", "Cerrar" }, await DetailButtonsAsync(page));
        Assert.Equal(0, await AdjustmentForm(page).CountAsync());

        // Paying needs the second factor as well (D7-SETTLEMENT-MFA); the dialog states the amount paid.
        api.WritesNeedMfa = true;
        await DetailButton(page, "Marcar pagada").ClickAsync();
        await ExpectDialogAsync(
            page,
            "¿Marcar liquidación como pagada?",
            "Confirma que ya se pagaron $75.50 MXN en la liquidación del repartidor DRV-9a8b7c6d (20–26 oct 2026).");
        await ConfirmOpenDialogAsync(page, "Marcar pagada");
        await ExpectMessageAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        await ExpectStatusLineAsync(page, "Aprobada · ");
        api.WritesNeedMfa = false;
        await DetailButton(page, "Marcar pagada").ClickAsync();
        await ConfirmOpenDialogAsync(page, "Marcar pagada");
        await ExpectStatusLineAsync(page, "Pagada · ");
        Assert.Equal(new[] { "Exportar CSV", "Cerrar" }, await DetailButtonsAsync(page));
        Assert.Equal(0, await VoidForm(page).CountAsync());
        var approvals = api.Calls(OrganizationA, "POST", "/approve");
        Assert.Equal(2, approvals.Length);
        var payments = api.Calls(OrganizationA, "POST", "/pay");
        Assert.Equal(2, payments.Length);
        Assert.All(approvals.Concat(payments), call =>
        {
            Assert.True(string.IsNullOrEmpty(call.Body));
            Assert.Matches(UuidPattern(), call.IdempotencyKey ?? string.Empty);
        });

        // The CSV is generated by the API, held in memory and handed to the browser as a download.
        var download = await page.RunAndWaitForDownloadAsync(() => DetailButton(page, "Exportar CSV").ClickAsync());
        Assert.Equal($"settlement-{CalculatedId}.csv", download.SuggestedFilename);
        Assert.Equal(api.Csv(OrganizationA, CalculatedId), await File.ReadAllTextAsync((await download.PathAsync())!));
        await ExpectMessageAsync(page, "Exportación generada; queda registrada en la bitácora de auditoría.");
        Assert.Equal("text/csv", Assert.Single(api.Calls(OrganizationA, "GET", "/export.csv")).Accept);
        await page.GetByText("Cada exportación a CSV queda registrada en la bitácora de auditoría.", new() { Exact = true })
            .WaitForAsync();

        // Voiding keeps every line and the total; it only releases the sources.
        await SelectAsync(page, 3);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidación 5e771e04", Exact = true }).WaitForAsync();
        var voidForm = VoidForm(page);
        await voidForm.GetByText("Las líneas y el total se conservan; anular solo libera sus fuentes para otra liquidación.", new() { Exact = true })
            .WaitForAsync();
        await voidForm.Locator("textarea[name=reason]").FillAsync("Periodo duplicado sintético");
        await voidForm.GetByRole(AriaRole.Button, new() { Name = "Anular", Exact = true }).ClickAsync();
        await ExpectDialogAsync(
            page,
            "¿Anular liquidación?",
            "Se anulará la liquidación del repartidor DRV-1f2e3d4c (2–14 sep 2026) por $120.00 MXN. " +
            "Esta acción no se puede deshacer.");
        await ConfirmOpenDialogAsync(page, "Anular");
        await ExpectDetailAsync(
            page,
            "Liquidación 5e771e04",
            "Anulada · Repartidor DRV-1f2e3d4c · 2–14 sep 2026 · creada 15 sep 2026, 23:00",
            "Total $120.00 MXN",
            ["Entrega", "7a000004", "$120.00 MXN", "15 sep 2026, 23:00"]);
        Assert.Equal(
            "{\"reason\":\"Periodo duplicado sintético\"}",
            Assert.Single(api.Calls(OrganizationA, "POST", "/void")).Body);
        Assert.Equal(0, await VoidForm(page).CountAsync());

        await AssertNoPayeeIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// SEC-004-PANEL-SCREENS-2026-10-13, AI-07 settlement_desk.rate_limit: exportSettlementCsv answers 429 with
    /// Retry-After: 2. The common message counts down, "Exportar CSV" is disabled and re-enables itself, the export is
    /// never sent by itself and the other actions are not limited. Before it, a failed payee list says so, names every
    /// payee "sin referencia" and "Actualizar" reads it again.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Export_waits_out_a_429_on_the_export_only_and_a_failed_payee_list_reads_sin_referencia()
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "FINANCE"));
        SeedOrganizationA(api);
        api.PayeeFailures = 1;

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        await page.Locator("main p.notice[role=status]")
            .GetByText("No fue posible cargar la lista de repartidores. Pulsa Actualizar para intentarlo de nuevo.", new() { Exact = true })
            .WaitForAsync();
        await ExpectRowsAsync(
            page,
            OrganizationARows.Select(row => new[] { row[0], row[1], Regex.Replace(row[2], "DRV-[0-9a-f]{8}", "sin referencia") }).ToArray());
        Assert.True(await CreateForm(page).GetByRole(AriaRole.Button, new() { Name = "Calcular", Exact = true }).IsDisabledAsync());

        // "Actualizar" reads the settlements and the payee list again.
        await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).ClickAsync();
        await ExpectRowsAsync(page, OrganizationARows);
        Assert.Equal(0, await page.GetByText("No fue posible cargar la lista de repartidores.", new() { Exact = false }).CountAsync());
        Assert.Equal(2, api.Calls(OrganizationA, "GET", "/api/v1/settlement-payees").Length);
        Assert.Equal(2, api.Calls(OrganizationA, "GET", "/api/v1/settlements").Length);

        // The export meets the limit: the common message counts down next to it and only it is held.
        api.ExportTooManyRequests = 1;
        await SelectAsync(page, 1);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidación 5e771e02", Exact = true }).WaitForAsync();
        await DetailButton(page, "Exportar CSV").ClickAsync();
        var countdown = page.Locator("main").GetByRole(AriaRole.Timer);
        await countdown.GetByText(TooManyRequestsText(2), new() { Exact = true }).WaitForAsync();
        Assert.True(await DetailButton(page, "Exportar CSV").IsDisabledAsync());
        Assert.Equal(TooManyRequestsText(2), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.Null(await countdown.GetAttributeAsync("aria-live"));
        Assert.Equal(0, await page.Locator("main p.notice[role=status]").CountAsync());

        // Another settlement waits for the same export, while its other actions stay available.
        await SelectAsync(page, 0);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidación 5e771e01", Exact = true }).WaitForAsync();
        Assert.True(await DetailButton(page, "Exportar CSV").IsDisabledAsync());
        Assert.False(await DetailButton(page, "Aprobar").IsDisabledAsync());
        Assert.False(await AdjustmentForm(page).GetByRole(AriaRole.Button, new() { Name = "Agregar ajuste", Exact = true }).IsDisabledAsync());

        // At 0 the button re-enables itself, the message goes and nothing was sent again by itself.
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.False(await DetailButton(page, "Exportar CSV").IsDisabledAsync());
        await Task.Delay(2_500);
        Assert.Single(api.Calls(OrganizationA, "GET", "/export.csv"));
        var download = await page.RunAndWaitForDownloadAsync(() => DetailButton(page, "Exportar CSV").ClickAsync());
        Assert.Equal($"settlement-{CalculatedId}.csv", download.SuggestedFilename);
        Assert.Equal(2, api.Calls(OrganizationA, "GET", "/export.csv").Length);

        await AssertNoPayeeIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_clears_settlements_payees_and_the_pending_keys()
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
        var api = await InstallAsync(
            page,
            (OrganizationA, OrganizationAName, "FINANCE"),
            (OrganizationB, OrganizationBName, "PLATFORM_ADMIN"));
        SeedOrganizationA(api);
        api.AddPayee(OrganizationB, OtherOrganizationVan);
        api.Add(OrganizationB, new SettlementModel(OtherOrganizationSettlementId, OtherOrganizationVan.Id, "CALCULATED", "2026-10-06", "2026-10-12", "2026-10-13T07:00:00Z")
            .WithDelivery("7a0000b1-0000-4000-8000-0000000000b1", 5_000));

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        await ExpectRowsAsync(page, OrganizationARows);
        await SelectAsync(page, 0);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidación 5e771e01", Exact = true }).WaitForAsync();
        // An adjustment that met a 503 keeps its Idempotency-Key for a retry...
        api.AdjustmentFailures.Enqueue(503);
        await SubmitAdjustmentAsync(page, "-1.15", "Ajuste sintético");
        await ConfirmOpenDialogAsync(page, "Agregar ajuste");
        await ExpectMessageAsync(page, RetryMessage);
        var firstKey = Assert.Single(api.Calls(OrganizationA, "POST", "/adjustments")).IdempotencyKey;

        // ...until the organization changes: settlements, the selection and the payees go before B answers.
        var releaseOrganizationB = api.Hold(OrganizationB);
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationBName });
        await WaitForAsync(() => api.AllCalls.Any(call => call.Organization == OrganizationB), "organization B was never read");
        Assert.Equal(0, await page.Locator("main ul.opsRouteList li").CountAsync());
        var during = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("20–26 oct 2026", during, StringComparison.Ordinal);
        Assert.DoesNotContain("DRV-9a8b7c6d", during, StringComparison.Ordinal);
        Assert.DoesNotContain(RetryMessage, during, StringComparison.Ordinal);
        await page.Locator("main").GetByText("Selecciona una liquidación para ver sus líneas.", new() { Exact = true }).WaitForAsync();
        await ExpectOptionsAsync(CreateForm(page).Locator("select[name=driver_id]"), "Elige un repartidor");
        releaseOrganizationB.TrySetResult();

        // PLATFORM_ADMIN in B, whose session has a satisfied MFA challenge (the API answers its reads; without MFA they are
        // 403 MFA_REQUIRED, Platform_admin_without_MFA_gets_the_step_up_before_any_settlement_or_payee_is_shown): its own
        // settlement and payee, and the hint that every settlement operation needs MFA.
        await ExpectRowsAsync(page, ["6–12 oct 2026", "Calculada · $50.00 MXN", "Repartidor DRV-77aa88bb · 1 línea(s)"]);
        await ExpectOptionsAsync(CreateForm(page).Locator("select[name=driver_id]"), "Elige un repartidor", "DRV-77aa88bb · Van");
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        await SelectAsync(page, 0);
        await page.Locator("main p.noticeInfo")
            .GetByText("Operar liquidaciones requiere verificar tu identidad (MFA).", new() { Exact = true })
            .WaitForAsync();

        // Back in A, the same adjustment is a new submission with a new key.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationAName });
        await ExpectRowsAsync(page, OrganizationARows);
        await SelectAsync(page, 0);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Liquidación 5e771e01", Exact = true }).WaitForAsync();
        await SubmitAdjustmentAsync(page, "-1.15", "Ajuste sintético");
        await ConfirmOpenDialogAsync(page, "Agregar ajuste");
        await ExpectMessageAsync(page, "Cambio guardado.");
        var adjustments = api.Calls(OrganizationA, "POST", "/adjustments");
        Assert.Equal(2, adjustments.Length);
        // -1.15 pesos is -115 cents exactly (-1.15 × 100 is -114.99999999999999 in floating point).
        Assert.All(adjustments, call => Assert.Equal("{\"amount_cents\":-115,\"reason\":\"Ajuste sintético\"}", call.Body));
        Assert.NotEqual(firstKey, adjustments[1].IdempotencyKey);
        await page.Locator("main section.opsRouteDetail header strong")
            .GetByText("Total $74.35 MXN", new() { Exact = true }).WaitForAsync();
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationB && call.Method == "POST");

        await AssertNoPayeeIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 settlement_desk roles.operate "PLATFORM_ADMIN with MFA" (AI-05 listSettlements, getSettlement and
    /// listSettlementPayees: PLATFORM_ADMIN members with a satisfied MFA challenge): without MFA the reads answer 403
    /// MFA_REQUIRED, the screen offers the step-up and shows no settlement, payee or action; with the second factor
    /// verified the same screen lists them and tells that every settlement operation needs MFA.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Platform_admin_without_MFA_gets_the_step_up_before_any_settlement_or_payee_is_shown()
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "PLATFORM_ADMIN"));
        SeedOrganizationA(api);
        api.ReadsNeedMfa = true;

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        await ExpectMessageAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        Assert.Equal(
            "/login?mfa=required&return_url=%2Ffinance%2Fsettlements",
            await page.Locator("main p.notice[role=status]")
                .GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true })
                .GetAttributeAsync("href"));
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        await WaitForAsync(
            () => api.Calls(OrganizationA, "GET", "/api/v1/settlement-payees").Length == 1,
            "the payee list was not requested");
        Assert.Equal(0, await page.Locator("main ul.opsRouteList li").CountAsync());
        await ExpectOptionsAsync(CreateForm(page).Locator("select[name=driver_id]"), "Elige un repartidor");
        Assert.True(await CreateForm(page).GetByRole(AriaRole.Button, new() { Name = "Calcular", Exact = true }).IsDisabledAsync());
        Assert.DoesNotContain("DRV-", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(api.AllCalls, call => call.Method == "POST");

        // With the second factor verified, the same screen lists the settlements and the payees.
        api.ReadsNeedMfa = false;
        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/settlements").AbsoluteUri);
        await ExpectRowsAsync(page, OrganizationARows);
        await ExpectOptionsAsync(
            CreateForm(page).Locator("select[name=driver_id]"),
            "Elige un repartidor", "DRV-1f2e3d4c · Automóvil", "DRV-9a8b7c6d · Motocicleta");
        await SelectAsync(page, 0);
        await page.Locator("main p.noticeInfo")
            .GetByText("Operar liquidaciones requiere verificar tu identidad (MFA).", new() { Exact = true })
            .WaitForAsync();
        Assert.Equal(new[] { "Aprobar", "Exportar CSV", "Cerrar" }, await DetailButtonsAsync(page));
        Assert.Equal(1, await AdjustmentForm(page).CountAsync());
        Assert.Equal(1, await VoidForm(page).CountAsync());

        await AssertNoPayeeIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>AI-07 settlement_desk roles: DISPATCHER, DRIVER and VIEWER never reach the screen.</summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("DISPATCHER")]
    [InlineData("VIEWER")]
    [InlineData("DRIVER")]
    public async Task Roles_without_settlements_are_not_offered_them_and_never_read_them(string role)
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, role));
        SeedOrganizationA(api);

        await page.GotoAsync(new Uri(server.BaseAddress, $"/finance/settlements?settlement={CalculatedId}").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await page.Locator("main").GetByText("Tu rol en la organización activa no opera liquidaciones.", new() { Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, role);
        await Task.Delay(500);
        Assert.Empty(api.AllCalls);
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).IsDisabledAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    private static string TooManyRequestsText(int seconds) => seconds == 1
        ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
        : $"Demasiadas solicitudes. Espera {seconds} segundos e intenta de nuevo.";

    /// <summary>
    /// Organization A: two payees and four settlements, newest first, one of a payee outside the list. No period is a
    /// Monday-to-Sunday week or a half month, so none can read like a preset whatever day the suite runs.
    /// </summary>
    private static void SeedOrganizationA(SettlementApi api)
    {
        api.AddPayee(OrganizationA, Motorcycle);
        api.AddPayee(OrganizationA, Car);
        api.Add(OrganizationA, new SettlementModel(CalculatedId, Motorcycle.Id, "CALCULATED", "2026-10-20", "2026-10-26", "2026-10-28T06:30:00Z")
            .WithDelivery("7a000001-0000-4000-8000-000000000001", 4_550)
            .WithDelivery("7a000002-0000-4000-8000-000000000002", 3_000));
        api.Add(OrganizationA, new SettlementModel(PaidId, Car.Id, "PAID", "2026-10-02", "2026-10-14", "2026-10-16T07:00:00Z")
            .WithDelivery("7a000003-0000-4000-8000-000000000003", 12_000));
        api.Add(OrganizationA, new SettlementModel(UnlistedPayeeSettlementId, UnlistedPayeeId, "APPROVED", "2026-09-17", "2026-09-29", "2026-10-01T06:59:00Z")
            .WithDelivery("7a000006-0000-4000-8000-000000000006", 9_900));
        api.Add(OrganizationA, new SettlementModel(VoidableId, Car.Id, "CALCULATED", "2026-09-02", "2026-09-14", "2026-09-16T06:00:00Z")
            .WithDelivery("7a000004-0000-4000-8000-000000000004", 12_000));
    }

    private static ILocator CreateForm(IPage page) => page.Locator("main form:has(h2:text-is('Calcular liquidación'))");

    private static ILocator FiltersForm(IPage page) => page.Locator("main form:has(h2:text-is('Filtros'))");

    private static ILocator AdjustmentForm(IPage page) => page.Locator("main form:has(h3:text-is('Agregar ajuste'))");

    private static ILocator VoidForm(IPage page) => page.Locator("main form:has(h3:text-is('Anular liquidación'))");

    private static ILocator OpenDialog(IPage page) => page.Locator("dialog.confirmDialog[open]");

    private static ILocator DetailButton(IPage page, string name) =>
        page.Locator("main section.opsRouteDetail .opsFormActions").GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private static async Task<string[]> DetailButtonsAsync(IPage page) =>
        (await page.Locator("main section.opsRouteDetail .opsFormActions button").AllInnerTextsAsync())
            .Select(text => text.Trim()).ToArray();

    private static Task SelectAsync(IPage page, int index) =>
        page.Locator("main ul.opsRouteList li button").Nth(index).ClickAsync();

    private static async Task SubmitAdjustmentAsync(IPage page, string amount, string reason)
    {
        var form = AdjustmentForm(page);
        await form.Locator("input[name=amount]").FillAsync(amount);
        await form.Locator("textarea[name=reason]").FillAsync(reason);
        await form.GetByRole(AriaRole.Button, new() { Name = "Agregar ajuste", Exact = true }).ClickAsync();
    }

    private static async Task ExpectDialogAsync(IPage page, string title, string description)
    {
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal(title, (await dialog.Locator("h2").InnerTextAsync()).Trim());
        Assert.Equal(description, (await dialog.Locator("p").InnerTextAsync()).Trim());
    }

    private static async Task ConfirmOpenDialogAsync(IPage page, string confirmLabel)
    {
        var dialog = OpenDialog(page);
        await dialog.GetByRole(AriaRole.Button, new() { Name = confirmLabel, Exact = true }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    /// <summary>Each row of the list: the period, the status with the total, and the payee with its line count.</summary>
    private static async Task ExpectRowsAsync(IPage page, params string[][] rows)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string[][] actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("main ul.opsRouteList li button").EvaluateAllAsync<string[][]>(
                "buttons => buttons.map(button => [...button.children].map(part => part.textContent.replace(/\\s+/g, \" \").trim()))");
            if (actual.Length == rows.Length && actual.Zip(rows).All(pair => pair.First.SequenceEqual(pair.Second)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            $"The settlements did not match the API answer: {string.Join(" | ", actual.Select(row => string.Join(", ", row)))}");
    }

    /// <summary>The detail header (title, status line, total) and its lines (type, order, amount, created).</summary>
    private static async Task ExpectDetailAsync(IPage page, string title, string statusLine, string total, params string[][] lines)
    {
        var detail = page.Locator("main section.opsRouteDetail");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string actual = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await detail.Locator("header h2").CountAsync() == 1)
            {
                var header = new[]
                {
                    (await detail.Locator("header h2").InnerTextAsync()).Trim(),
                    Regex.Replace((await detail.Locator("header p").InnerTextAsync()).Trim(), @"\s+", " "),
                    (await detail.Locator("header strong").InnerTextAsync()).Trim(),
                };
                var rows = await detail.Locator("table tbody tr").EvaluateAllAsync<string[][]>(
                    "rows => rows.map(row => [...row.cells].map(cell => cell.innerText.trim()))");
                actual = $"{string.Join(" / ", header)} :: {string.Join(" | ", rows.Select(row => string.Join(", ", row)))}";
                if (header.SequenceEqual(new[] { title, statusLine, total }) && rows.Length == lines.Length &&
                    rows.Zip(lines).All(pair => pair.First.SequenceEqual(pair.Second)))
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The settlement detail did not match the API answer: {actual}");
    }

    private static Task ExpectStatusLineAsync(IPage page, string prefix) =>
        page.WaitForFunctionAsync(
            "prefix => document.querySelector('main section.opsRouteDetail header p')?.innerText.trim().startsWith(prefix) === true",
            prefix);

    private static async Task ExpectOptionsAsync(ILocator select, params string[] options)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<string> actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await select.Locator("option").AllTextContentsAsync();
            if (actual.Select(text => text.Trim()).SequenceEqual(options, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The options did not match: {string.Join(" | ", actual)}");
    }

    private static async Task ExpectErrorsAsync(IPage page, params string[] errors)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<string> actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("main ul.notice[role=alert] li").AllInnerTextsAsync();
            if (actual.Select(text => text.Trim()).SequenceEqual(errors, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The validation errors did not match: {string.Join(" | ", actual)}");
    }

    /// <summary>The screen's last server outcome (Feedback), not the shell's announcer.</summary>
    private static Task ExpectMessageAsync(IPage page, string message) =>
        page.Locator("main p.notice[role=status]").GetByText(message, new() { Exact = false }).WaitForAsync();

    /// <summary>
    /// UI-PHASE3-DRIVERS-2026-10-11: no payee id is ever shown (only the option values carry it, to be sent); no UUID
    /// is visible at all.
    /// </summary>
    private static async Task AssertNoPayeeIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(FullUuidPattern(), visible);
        foreach (var payeeId in new[] { Motorcycle.Id, Car.Id, OtherOrganizationVan.Id, UnlistedPayeeId })
        {
            Assert.DoesNotContain(payeeId[..8], visible, StringComparison.Ordinal);
        }
    }

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
        if (expected.Contains("/finance/settlements"))
        {
            Assert.Equal(
                "page",
                await page.Locator("nav.appNav a[href=\"/finance/settlements\"]").GetAttributeAsync("aria-current"));
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
    /// switch, and fulfils /me/organization-contexts and the settlement operations. Playwright runs the last matching
    /// route first, so the catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<SettlementApi> InstallAsync(
        IPage page,
        params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-settlement-desk-playwright",
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
        var api = new SettlementApi();
        await page.RouteAsync("**/api/v1/settlements**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/settlement-payees**", api.HandleAsync);
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

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex FullUuidPattern();

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    /// <summary>An AI-05 SettlementPayee.</summary>
    private sealed record SettlementPayee(string Id, string Reference, string Vehicle)
    {
        internal object ToJson() => new { driver_id = Id, driver_reference = Reference, vehicle_type = Vehicle };
    }

    private sealed record SettlementLineModel(string Id, string Type, string? OrderId, long AmountCents, string Source, string CreatedAt);

    /// <summary>A settlement of an organization; lines are only ever appended, as SET-001 keeps them.</summary>
    private sealed class SettlementModel(
        string id,
        string payeeId,
        string status,
        string periodFrom,
        string periodTo,
        string createdAt)
    {
        internal string Id { get; } = id;
        internal string PayeeId { get; } = payeeId;
        internal string Status { get; set; } = status;
        internal string PeriodFrom { get; } = periodFrom;
        internal string PeriodTo { get; } = periodTo;
        internal string CreatedAt { get; } = createdAt;
        internal List<SettlementLineModel> Lines { get; } = [];
        internal long TotalCents => Lines.Sum(line => line.AmountCents);

        internal SettlementModel WithDelivery(string orderId, long amountCents)
        {
            Lines.Add(new SettlementLineModel(
                $"11e00000-0000-4000-8000-{orderId[..8]}{Lines.Count:D4}",
                "DELIVERY",
                orderId,
                amountCents,
                $"dispatch.assignments/a5500000-0000-4000-8000-{orderId[..8]}{Lines.Count:D4}",
                CreatedAt));
            return this;
        }

        internal object ToJson() => new
        {
            id = Id,
            payee_type = "DRIVER",
            payee_id = PayeeId,
            status = Status,
            total_cents = TotalCents,
            period_from = PeriodFrom,
            period_to = PeriodTo,
            created_at = CreatedAt,
            lines = Lines.Select(line => new
            {
                id = line.Id,
                line_type = line.Type,
                order_id = line.OrderId,
                amount_cents = line.AmountCents,
                source_reference = line.Source,
                created_at = line.CreatedAt,
            }).ToArray(),
        };

        /// <summary>The AI-05 export: the exact header, one CRLF record per line, money in cents, UTC microseconds.</summary>
        internal string ToCsv()
        {
            var csv = new StringBuilder(
                "settlement_id,payee_type,payee_id,period_from,period_to,settlement_status,settlement_total_cents," +
                "line_id,line_type,order_id,amount_cents,source_reference,line_created_at\r\n");
            foreach (var line in Lines)
            {
                csv.Append(
                    $"{Id},DRIVER,{PayeeId},{PeriodFrom},{PeriodTo},{Status},{TotalCents},{line.Id},{line.Type}," +
                    $"{line.OrderId},{line.AmountCents},{line.Source},{line.CreatedAt[..19]}.000000Z\r\n");
            }

            return csv.ToString();
        }
    }

    private sealed record SettlementCall(
        string Organization,
        string Method,
        string Path,
        string Query,
        string? IdempotencyKey,
        string? Accept,
        string? Body);

    /// <summary>The settlement operations of AI-05 over per-organization data; every call is recorded in order.</summary>
    private sealed class SettlementApi
    {
        private readonly object _gate = new();
        private readonly List<SettlementCall> _calls = [];
        private readonly Dictionary<string, List<SettlementModel>> _settlements = [];
        private readonly Dictionary<string, List<SettlementPayee>> _payees = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();
        private int _adjustments;

        /// <summary>Statuses the next addSettlementAdjustment calls answer before one succeeds.</summary>
        internal ConcurrentQueue<int> AdjustmentFailures { get; } = new();

        /// <summary>approveSettlement and markSettlementPaid answer 403 MFA_REQUIRED (D7-SETTLEMENT-MFA).</summary>
        internal bool WritesNeedMfa { get; set; }

        /// <summary>The next exports answered 429 with Retry-After: 2.</summary>
        internal int ExportTooManyRequests { get; set; }

        /// <summary>The next payee list reads answered 503.</summary>
        internal int PayeeFailures { get; set; }

        /// <summary>
        /// Every read answers 403 MFA_REQUIRED, as for a PLATFORM_ADMIN whose session has no satisfied MFA challenge
        /// (AI-05 listSettlements, getSettlement, listSettlementPayees).
        /// </summary>
        internal bool ReadsNeedMfa { get; set; }

        internal IReadOnlyList<SettlementCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal void AddPayee(string organization, SettlementPayee payee)
        {
            lock (_gate)
            {
                if (!_payees.TryGetValue(organization, out var payees)) _payees[organization] = payees = [];
                payees.Add(payee);
            }
        }

        /// <summary>Appends a settlement after the newer ones (the list is newest first).</summary>
        internal void Add(string organization, SettlementModel settlement)
        {
            lock (_gate)
            {
                if (!_settlements.TryGetValue(organization, out var settlements)) _settlements[organization] = settlements = [];
                settlements.Add(settlement);
            }
        }

        internal string Csv(string organization, string settlementId) => Find(organization, settlementId)!.ToCsv();

        internal SettlementCall[] Calls(string organization, string method, string pathPart) =>
            AllCalls.Where(call => call.Organization == organization && call.Method == method &&
                (pathPart.StartsWith("/api/", StringComparison.Ordinal)
                    ? call.Path == pathPart
                    : call.Path.EndsWith(pathPart, StringComparison.Ordinal)))
                .ToArray();

        /// <summary>Holds the organization's answers until the returned source is completed.</summary>
        internal TaskCompletionSource Hold(string organization) =>
            _holds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var organization = request.Headers.TryGetValue("x-organization-id", out var value) ? value : string.Empty;
            var call = new SettlementCall(
                organization,
                request.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                request.Headers.TryGetValue("idempotency-key", out var key) ? key : null,
                request.Headers.TryGetValue("accept", out var accept) ? accept : null,
                request.PostData);
            lock (_gate) _calls.Add(call);
            if (_holds.TryGetValue(organization, out var hold)) await hold.Task;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (ReadsNeedMfa && call.Method == "GET")
            {
                await ProblemAsync(route, 403, "MFA_REQUIRED");
                return;
            }

            switch (call.Method, segments)
            {
                case ("GET", ["api", "v1", "settlement-payees"]):
                    await PayeesAsync(route, organization);
                    return;
                case ("GET", ["api", "v1", "settlements"]):
                    await ListAsync(route, organization, call.Query);
                    return;
                case ("POST", ["api", "v1", "settlements"]):
                    await CreateAsync(route, organization, call.Body);
                    return;
                case ("GET", ["api", "v1", "settlements", var id]):
                    await RespondAsync(route, Find(organization, id));
                    return;
                case ("POST", ["api", "v1", "settlements", var id, "adjustments"]):
                    await AdjustAsync(route, Find(organization, id), call.Body);
                    return;
                case ("POST", ["api", "v1", "settlements", var id, "approve"]):
                    await TransitionAsync(route, Find(organization, id), "CALCULATED", "APPROVED", WritesNeedMfa);
                    return;
                case ("POST", ["api", "v1", "settlements", var id, "pay"]):
                    await TransitionAsync(route, Find(organization, id), "APPROVED", "PAID", WritesNeedMfa);
                    return;
                case ("POST", ["api", "v1", "settlements", var id, "void"]):
                    await TransitionAsync(route, Find(organization, id), null, "VOID", false);
                    return;
                case ("GET", ["api", "v1", "settlements", var id, "export.csv"]):
                    await ExportAsync(route, Find(organization, id));
                    return;
                default:
                    await ProblemAsync(route, 404);
                    return;
            }
        }

        private SettlementModel? Find(string organization, string settlementId)
        {
            lock (_gate)
            {
                return _settlements.TryGetValue(organization, out var settlements)
                    ? settlements.FirstOrDefault(settlement => settlement.Id == settlementId)
                    : null;
            }
        }

        private async Task PayeesAsync(IRoute route, string organization)
        {
            if (PayeeFailures > 0)
            {
                PayeeFailures -= 1;
                await ProblemAsync(route, 503);
                return;
            }

            SettlementPayee[] payees;
            lock (_gate) payees = _payees.TryGetValue(organization, out var list) ? [.. list] : [];
            await FulfillAsync(route, new { items = payees.Select(payee => payee.ToJson()).ToArray(), next_cursor = (string?)null });
        }

        private async Task ListAsync(IRoute route, string organization, string query)
        {
            var parameters = System.Web.HttpUtility.ParseQueryString(query);
            SettlementModel[] settlements;
            lock (_gate) settlements = _settlements.TryGetValue(organization, out var list) ? [.. list] : [];
            var items = settlements
                .Where(settlement => parameters["payee_id"] is not { } payee || settlement.PayeeId == payee)
                .Where(settlement => parameters["status"] is not { } status || settlement.Status == status)
                .Select(settlement => settlement.ToJson())
                .ToArray();
            await FulfillAsync(route, new { items, next_cursor = (string?)null });
        }

        private async Task CreateAsync(IRoute route, string organization, string? body)
        {
            using var document = JsonDocument.Parse(body ?? "{}");
            var root = document.RootElement;
            var created = new SettlementModel(
                CreatedId,
                root.GetProperty("driver_id").GetString()!,
                "CALCULATED",
                root.GetProperty("period_from").GetString()!,
                root.GetProperty("period_to").GetString()!,
                "2026-11-16T05:31:00Z")
                .WithDelivery("7a000005-0000-4000-8000-000000000005", 4_200);
            lock (_gate) _settlements[organization].Insert(0, created);
            await FulfillAsync(route, created.ToJson(), 201);
        }

        private async Task AdjustAsync(IRoute route, SettlementModel? settlement, string? body)
        {
            if (AdjustmentFailures.TryDequeue(out var failure))
            {
                await ProblemAsync(route, failure);
                return;
            }

            if (settlement is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            if (settlement.Status != "CALCULATED")
            {
                await ProblemAsync(route, 409, "SETTLEMENT_STATE_CONFLICT");
                return;
            }

            using var document = JsonDocument.Parse(body ?? "{}");
            var amount = document.RootElement.GetProperty("amount_cents").GetInt64();
            // D7-SETTLEMENT-NEGATIVE-TOTAL.
            if (settlement.TotalCents + amount < 0)
            {
                await ProblemAsync(route, 409, "SETTLEMENT_TOTAL_NEGATIVE");
                return;
            }

            var count = Interlocked.Increment(ref _adjustments);
            settlement.Lines.Add(new SettlementLineModel(
                $"11e000ad-0000-4000-8000-{count:D12}",
                "ADJUSTMENT",
                null,
                amount,
                $"platform.audit_logs/a0d17000-0000-4000-8000-{count:D12}",
                "2026-11-16T05:40:00Z"));
            await FulfillAsync(route, settlement.ToJson(), 201);
        }

        private static async Task TransitionAsync(IRoute route, SettlementModel? settlement, string? from, string to, bool needsMfa)
        {
            if (needsMfa)
            {
                await ProblemAsync(route, 403, "MFA_REQUIRED");
                return;
            }

            if (settlement is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            if (from is not null ? settlement.Status != from : settlement.Status is "PAID" or "VOID")
            {
                await ProblemAsync(route, 409, "SETTLEMENT_STATE_CONFLICT");
                return;
            }

            settlement.Status = to;
            await FulfillAsync(route, settlement.ToJson());
        }

        private async Task ExportAsync(IRoute route, SettlementModel? settlement)
        {
            if (ExportTooManyRequests > 0)
            {
                ExportTooManyRequests -= 1;
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 429,
                    ContentType = "application/problem+json",
                    // The test API is another origin, so it exposes Retry-After as a cross-origin API would have to.
                    Headers = new Dictionary<string, string>
                    {
                        ["Retry-After"] = "2",
                        ["Cache-Control"] = "no-store",
                        ["Access-Control-Expose-Headers"] = "Retry-After",
                    },
                    Body = JsonSerializer.Serialize(new
                    {
                        type = "about:blank",
                        title = "Too Many Requests",
                        status = 429,
                        traceId = new string('0', 32),
                    }),
                });
                return;
            }

            if (settlement is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "text/csv; charset=utf-8",
                Headers = new Dictionary<string, string>
                {
                    ["Cache-Control"] = "no-store",
                    ["Content-Disposition"] = $"attachment; filename=settlement-{settlement.Id}.csv",
                },
                Body = settlement.ToCsv(),
            });
        }

        private static Task RespondAsync(IRoute route, SettlementModel? settlement) =>
            settlement is null ? ProblemAsync(route, 404) : FulfillAsync(route, settlement.ToJson());
    }
}
