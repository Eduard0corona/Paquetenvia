using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for AI-07 <c>cod_control</c>
/// (<c>/finance/cod</c>, UI-001-SCREENS-CSV-INC-COD, FINANCE-COD-RECONCILIATION, FINANCE-COD-MFA-2026-09-27,
/// API-FIN-COD-VISIBILITY-2026-09-29, FIN-PENDING-COD-LIST-FINANCE-2026-10-02) in a real browser against the
/// production Next build. The API is fulfilled in the browser with AI-05 shapes (getOrderFinancials,
/// recordCodCollection, reconcileCod, listOrders with cod_pending_reconciliation, /me/organization-contexts):
/// DISPATCHER records exactly expected_cents (typed pesos become integer cents with no floating point; anything else
/// is refused before a request) and reconciles; FINANCE with MFA reconciles from the pending list after reading the
/// order and is never offered "Registrar cobro"; FINANCE without MFA gets the step-up and returns to the order;
/// VIEWER and DRIVER have no access; the reference is never echoed; a write reuses its Idempotency-Key on a 5xx retry
/// and takes a new one for a new payload; a 429 on the pending list counts down, holds its reload after a write and
/// reads once at 0 while the collection actions stay available; a tenant switch clears the order, the COD record and
/// the keys. The screen calls only AI-05 paths, keeps nothing in browser storage, logs nothing, shows every amount
/// from integer cents and every time in America/Mazatlan whatever the device zone, and /finance is never stored.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class CodControlPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    /// <summary>An order out for delivery that expects $1,001.13 in cash and has nothing recorded yet.</summary>
    private const string LookupOrderId = "c0d00000-0000-4000-8000-000000000001";
    private const string LookupRecordId = "c0de0000-0000-4000-8000-0000000000a1";
    private const string LookupGuide = "ORD_CobroPlaywright0000001";

    /// <summary>A delivered order whose $250.00 collection is recorded and waits for reconciliation.</summary>
    private const string PendingOrderId = "c0d00000-0000-4000-8000-000000000002";
    private const string PendingRecordId = "c0de0000-0000-4000-8000-0000000000b2";
    private const string PendingGuide = "ORD_CobroPlaywright0000002";

    private const string SecondPendingOrderId = "c0d00000-0000-4000-8000-000000000003";
    private const string SecondPendingRecordId = "c0de0000-0000-4000-8000-0000000000c3";
    private const string SecondPendingGuide = "ORD_CobroPlaywright0000003";

    private const string OtherOrganizationOrderId = "c0d00000-0000-4000-8000-0000000000b1";
    private const string OtherOrganizationRecordId = "c0de0000-0000-4000-8000-0000000000d4";
    private const string OtherOrganizationGuide = "ORD_CobroPlaywrightOrgB001";

    /// <summary>$1,001.13: 1001.13 × 100 is 100112.99999999999 in floating point, so only integer parsing sends 100113.</summary>
    private const long ExpectedCents = 100_113;

    private const string ReferenceA = "Recibo sintético 0001";
    private const string ReferenceB = "Recibo sintético 0002";

    /// <summary>Recorded at 02:30 UTC on 1 November: 19:30 on 31 October in Mazatlán, 11:30 on 1 November in Tokyo.</summary>
    private const string RecordedAt = "2026-11-01T02:30:00Z";
    private const string RecordedAtMazatlan = "31 oct 2026, 19:30";
    private const string ReconciledAt = "2026-11-01T03:05:00Z";
    private const string ReconciledAtMazatlan = "31 oct 2026, 20:05";

    private const string RetryMessage =
        "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.";

    private const string MfaHint = "Consultar y conciliar cobros con tu rol requiere verificar tu identidad (MFA).";

    /// <summary>The AI-05 operations of cod_control and the shell's role read; any other call is a failure.</summary>
    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/orders$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/financials$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/cod-records$"),
        new("^POST /api/v1/cod-records/[0-9a-f-]{36}/reconcile$"),
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

    private static readonly string[] LookupEconomics =
    [
        "Ingreso: $1,000.00 MXN",
        "Costo Flota propia (2): $450.50 MXN",
        "Costo Repartidor externo (0): $0.00 MXN",
        "Costo Capacidad aliada (1): $300.00 MXN",
        "Costo total: $750.50 MXN",
        "Margen: $249.50 MXN (24.95 %)",
    ];

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Dispatcher_records_exactly_the_expected_cents_reusing_the_key_on_a_5xx_and_reconciles()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale: times still read in Mazatlán and amounts in es-MX pesos.
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Add(OrganizationA, LookupOrder());

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/finance/cod").AbsoluteUri);
        Assert.NotNull(response);
        // /finance is never stored (next.config.ts privateNoStore).
        Assert.Contains("no-store", (await response.AllHeadersAsync())["cache-control"], StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Cobro contra entrega", Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, "DISPATCHER");
        await ExpectTextAsync(page, "No hay cobros registrados pendientes de conciliar.");
        await ExpectTextAsync(page, "Consulta una orden para ver su cobro contra entrega.");
        Assert.Equal(0, await page.Locator("main p.noticeInfo").CountAsync());

        // An id that is not a UUID is refused before any request.
        await page.GetByLabel("ID de la orden", new() { Exact = true }).FillAsync("ORD_no-es-un-id");
        await page.GetByRole(AriaRole.Button, new() { Name = "Consultar", Exact = true }).ClickAsync();
        await ExpectErrorsAsync(page, "El ID de la orden no es válido.");
        Assert.Empty(api.Calls(OrganizationA, "GET", "/financials"));

        await LookUpAsync(page, LookupOrderId);
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Orden {LookupOrderId}", Exact = true }).WaitForAsync();
        Assert.Equal(LookupEconomics, await DescriptionAsync(page, 0));
        Assert.Equal(
            new[]
            {
                "Esperado: $1,001.13 MXN", "Estado: Sin cobro registrado", "Permite entregar: No", "Permite cerrar: No",
            },
            await DescriptionAsync(page, 1));
        var recordForm = RecordForm(page);
        await recordForm.GetByText("El monto debe ser exactamente $1,001.13 MXN.", new() { Exact = true }).WaitForAsync();

        // Anything but exactly the expected cents, or a reference with surrounding spaces, is refused on screen.
        await SubmitRecordAsync(page, "1001.12", ReferenceA);
        await ExpectErrorsAsync(page, "El monto cobrado debe ser exactamente el esperado por la orden.");
        await SubmitRecordAsync(page, "1001.135", ReferenceA);
        await ExpectErrorsAsync(page, "Captura el monto cobrado en MXN, positivo y con hasta 2 decimales.");
        await SubmitRecordAsync(page, "1,001.13", ReferenceA);
        await ExpectErrorsAsync(page, "Captura el monto cobrado en MXN, positivo y con hasta 2 decimales.");
        await SubmitRecordAsync(page, "1001.13", $" {ReferenceA}");
        await ExpectErrorsAsync(
            page,
            "La referencia es obligatoria (máximo 200 caracteres) y no puede iniciar ni terminar con espacios.");
        Assert.Equal(0, await OpenDialog(page).CountAsync());
        Assert.Empty(api.Calls(OrganizationA, "POST", "/cod-records"));

        // The confirmation states the exact amount and never the reference; the first two answers are 503.
        api.RecordFailures.Enqueue(503);
        api.RecordFailures.Enqueue(503);
        await SubmitRecordAsync(page, "1001.13", ReferenceA);
        await ConfirmAsync(
            page,
            "¿Registrar cobro?",
            "Se registrará el cobro contra entrega de $1,001.13 MXN de la orden c0d00000. Después quedará pendiente de conciliar.",
            "Registrar cobro");
        await ExpectMessageAsync(page, RetryMessage);
        await SubmitRecordAsync(page, "1001.13", ReferenceA);
        await ConfirmAsync(page, "¿Registrar cobro?", null, "Registrar cobro");
        await WaitForAsync(() => api.Calls(OrganizationA, "POST", "/cod-records").Length == 2, "the retry was not sent");
        await ExpectMessageAsync(page, RetryMessage);
        // A new payload is a new submission with a new key.
        await SubmitRecordAsync(page, "1001.13", ReferenceB);
        await ConfirmAsync(page, "¿Registrar cobro?", null, "Registrar cobro");
        await ExpectMessageAsync(page, "Cobro registrado.");

        var records = api.Calls(OrganizationA, "POST", "/cod-records");
        Assert.Equal(3, records.Length);
        Assert.All(records, record => Assert.Equal($"/api/v1/orders/{LookupOrderId}/cod-records", record.Path));
        Assert.All(records, record => Assert.Matches(UuidPattern(), record.IdempotencyKey ?? string.Empty));
        Assert.Equal(records[0].IdempotencyKey, records[1].IdempotencyKey);
        Assert.Equal(records[0].Body, records[1].Body);
        Assert.NotEqual(records[0].IdempotencyKey, records[2].IdempotencyKey);
        AssertRecordBody(records[0].Body, ReferenceA);
        AssertRecordBody(records[2].Body, ReferenceB);

        // REST is read again after the write: the position, the record and the pending list.
        await ExpectDescriptionAsync(
            page,
            1,
            "Esperado: $1,001.13 MXN", "Estado: Cobrado, sin conciliar", "Cobrado: $1,001.13 MXN", "Permite entregar: Sí",
            "Permite cerrar: No");
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Registro de cobro {LookupRecordId}", Exact = true }).WaitForAsync();
        Assert.Equal(
            new[] { "Monto: $1,001.13 MXN", "Estado: Cobrado, sin conciliar", $"Registrado: {RecordedAtMazatlan}", "Conciliado: —" },
            await DescriptionAsync(page, 2));
        Assert.Equal(0, await recordForm.CountAsync());
        await WaitForAsync(
            () => api.Calls(OrganizationA, "GET", "/api/v1/orders").Length == 2,
            "the pending list was not read again after the write");
        await AssertReferenceNotEchoedAsync(page);

        // Reconcile the record the API returned: the dialog states the amount and, now that the pending list names the
        // order, its guía; the request carries no body.
        await PendingRows(page).Filter(new() { HasTextString = LookupGuide }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Conciliar este cobro", Exact = true }).ClickAsync();
        await ConfirmAsync(
            page,
            "¿Conciliar este cobro?",
            $"Se conciliará el cobro de $1,001.13 MXN de la orden {LookupGuide}. Confirma solo si el efectivo ya se recibió completo.",
            "Conciliar");
        await ExpectMessageAsync(page, "Cobro conciliado.");
        var reconcile = Assert.Single(api.Calls(OrganizationA, "POST", "/reconcile"));
        Assert.Equal($"/api/v1/cod-records/{LookupRecordId}/reconcile", reconcile.Path);
        Assert.Matches(UuidPattern(), reconcile.IdempotencyKey ?? string.Empty);
        Assert.True(string.IsNullOrEmpty(reconcile.Body));
        await ExpectDescriptionAsync(
            page,
            1,
            "Esperado: $1,001.13 MXN", "Estado: Conciliado", "Cobrado: $1,001.13 MXN", "Permite entregar: Sí",
            "Permite cerrar: Sí");
        await ExpectDescriptionAsync(
            page,
            2,
            "Monto: $1,001.13 MXN", "Estado: Conciliado", $"Registrado: {RecordedAtMazatlan}",
            $"Conciliado: {ReconciledAtMazatlan}");
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Conciliar este cobro", Exact = true }).CountAsync());
        await AssertReferenceNotEchoedAsync(page);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        api.AssertPendingQueriesOnly();
        Assert.All(api.AllCalls, call => Assert.Equal(OrganizationA, call.Organization));
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Finance_with_MFA_reconciles_from_the_pending_list_after_reading_the_order_and_never_records()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            TimezoneId = "America/New_York",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "FINANCE"));
        api.Add(OrganizationA, LookupOrder());
        api.Add(OrganizationA, PendingOrder());

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/cod").AbsoluteUri);
        await ExpectNavigationAsync(page, "FINANCE");
        await page.Locator("main p.noticeInfo").GetByText(MfaHint, new() { Exact = true }).WaitForAsync();
        var row = PendingRows(page).Filter(new() { HasTextString = PendingGuide });
        await row.WaitForAsync();
        Assert.Contains("Entregada", await row.InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await PendingRows(page).CountAsync());

        // "Conciliar" reads the order's financials first; the dialog then states the amount it read.
        await row.GetByRole(AriaRole.Button, new() { Name = "Conciliar", Exact = true }).ClickAsync();
        await ConfirmAsync(
            page,
            "¿Conciliar cobro?",
            $"Se conciliará el cobro de $250.00 MXN de la orden {PendingGuide}. Confirma solo si el efectivo ya se recibió completo.",
            "Conciliar");
        await ExpectMessageAsync(page, "Cobro conciliado.");
        var sequence = api.AllCalls.Select(call => $"{call.Method} {call.Path}").ToArray();
        var read = Array.IndexOf(sequence, $"GET /api/v1/orders/{PendingOrderId}/financials");
        var write = Array.IndexOf(sequence, $"POST /api/v1/cod-records/{PendingRecordId}/reconcile");
        Assert.True(read >= 0 && write > read, string.Join(" | ", sequence));
        await ExpectTextAsync(page, "No hay cobros registrados pendientes de conciliar.");
        await ExpectDescriptionAsync(
            page,
            1,
            "Esperado: $250.00 MXN", "Estado: Conciliado", "Cobrado: $250.00 MXN", "Permite entregar: Sí",
            "Permite cerrar: Sí");

        // FINANCE is never offered "Registrar cobro", even for an order that expects cash and has none recorded.
        await LookUpAsync(page, LookupOrderId);
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Orden {LookupOrderId}", Exact = true }).WaitForAsync();
        await ExpectDescriptionAsync(
            page, 1, "Esperado: $1,001.13 MXN", "Estado: Sin cobro registrado", "Permite entregar: No", "Permite cerrar: No");
        Assert.Equal(0, await RecordForm(page).CountAsync());
        Assert.Equal(0, await page.GetByLabel("Monto cobrado (MXN)").CountAsync());
        Assert.Empty(api.Calls(OrganizationA, "POST", "/cod-records"));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        // FIN-PENDING-COD-LIST-FINANCE-2026-10-02: the pending filter is the only listOrders call FINANCE makes.
        api.AssertPendingQueriesOnly();
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Finance_without_MFA_gets_the_step_up_on_the_list_and_on_the_order_and_returns_to_that_order()
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
        api.Add(OrganizationA, LookupOrder());
        api.Add(OrganizationA, PendingOrder());
        api.MfaRequired = true;

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/cod").AbsoluteUri);
        await ExpectMessageAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        Assert.Equal("/login?mfa=required&return_url=%2Ffinance%2Fcod", await StepUpHrefAsync(page));
        Assert.Equal(0, await PendingRows(page).CountAsync());
        Assert.Equal(0, await page.GetByText("No hay cobros registrados pendientes de conciliar.").CountAsync());

        // The order's own read offers the step-up back to that order.
        await LookUpAsync(page, LookupOrderId);
        await WaitForAsync(
            () => api.Calls(OrganizationA, "GET", "/financials").Length == 1,
            "the order's financials were not requested");
        await page.WaitForFunctionAsync(
            "expected => [...document.querySelectorAll('main a')].some(link => link.getAttribute('href') === expected)",
            $"/login?mfa=required&return_url=%2Ffinance%2Fcod%3Forder%3D{LookupOrderId}");
        Assert.Equal(0, await page.Locator("main dl.descList").CountAsync());

        // With the second factor verified, the return URL reopens the order by itself.
        api.MfaRequired = false;
        await page.GotoAsync(new Uri(server.BaseAddress, $"/finance/cod?order={LookupOrderId}").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Orden {LookupOrderId}", Exact = true }).WaitForAsync();
        Assert.Equal(LookupEconomics, await DescriptionAsync(page, 0));
        await PendingRows(page).Filter(new() { HasTextString = PendingGuide }).WaitForAsync();
        Assert.Equal(0, await RecordForm(page).CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        api.AssertPendingQueriesOnly();
        trace.Passed();
    }

    /// <summary>AI-07 /finance/cod roles: VIEWER and DRIVER never reach the screen and never read an order.</summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("VIEWER")]
    [InlineData("DRIVER")]
    public async Task Roles_without_COD_access_are_not_offered_it_and_never_read_orders(string role)
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
        api.Add(OrganizationA, PendingOrder());

        await page.GotoAsync(new Uri(server.BaseAddress, $"/finance/cod?order={PendingOrderId}").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await ExpectTextAsync(page, "Tu rol en la organización activa no consulta cobros contra entrega.");
        await ExpectNavigationAsync(page, role);
        await Task.Delay(500);
        Assert.Empty(api.AllCalls);
        Assert.Equal(0, await page.GetByLabel("ID de la orden", new() { Exact = true }).CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_clears_the_order_the_collection_and_the_pending_keys()
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
            (OrganizationA, OrganizationAName, "DISPATCHER"),
            (OrganizationB, OrganizationBName, "PLATFORM_ADMIN"));
        api.Add(OrganizationA, LookupOrder());
        api.Add(OrganizationA, PendingOrder());
        api.Add(OrganizationB, new CodOrder(OtherOrganizationOrderId, OtherOrganizationGuide, OtherOrganizationRecordId, 4_200)
        {
            CodStatus = "RECORDED",
            OrderStatus = "DELIVERED",
        });

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/cod").AbsoluteUri);
        await PendingRows(page).Filter(new() { HasTextString = PendingGuide }).WaitForAsync();
        await LookUpAsync(page, LookupOrderId);
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Orden {LookupOrderId}", Exact = true }).WaitForAsync();
        // A collection that met a 503 keeps its Idempotency-Key for a retry...
        api.RecordFailures.Enqueue(503);
        await SubmitRecordAsync(page, "1001.13", ReferenceA);
        await ConfirmAsync(page, "¿Registrar cobro?", null, "Registrar cobro");
        await ExpectMessageAsync(page, RetryMessage);
        var firstKey = Assert.Single(api.Calls(OrganizationA, "POST", "/cod-records")).IdempotencyKey;

        // ...until the organization changes: the order, its collection and the pending list go before B answers.
        var releaseOrganizationB = api.Hold(OrganizationB);
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationBName });
        await WaitForAsync(() => api.AllCalls.Any(call => call.Organization == OrganizationB), "organization B was never read");
        await ExpectTextAsync(page, "Consulta una orden para ver su cobro contra entrega.");
        var during = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(LookupOrderId, during, StringComparison.Ordinal);
        Assert.DoesNotContain(PendingGuide, during, StringComparison.Ordinal);
        Assert.DoesNotContain(RetryMessage, during, StringComparison.Ordinal);
        Assert.Equal(0, await RecordForm(page).CountAsync());
        Assert.Equal(string.Empty, await page.GetByLabel("ID de la orden", new() { Exact = true }).InputValueAsync());
        releaseOrganizationB.TrySetResult();
        // PLATFORM_ADMIN in B: the MFA hint, its own pending list, and A's order is the uniform not found.
        await page.Locator("main p.noticeInfo").GetByText(MfaHint, new() { Exact = true }).WaitForAsync();
        await PendingRows(page).Filter(new() { HasTextString = OtherOrganizationGuide }).WaitForAsync();
        Assert.Equal(1, await PendingRows(page).CountAsync());
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        await LookUpAsync(page, LookupOrderId);
        await ExpectMessageAsync(page, "El recurso no existe o no está disponible.");
        Assert.Equal(0, await page.Locator("main dl.descList").CountAsync());

        // Back in A, the same collection is a new submission with a new key.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationAName });
        await PendingRows(page).Filter(new() { HasTextString = PendingGuide }).WaitForAsync();
        await LookUpAsync(page, LookupOrderId);
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Orden {LookupOrderId}", Exact = true }).WaitForAsync();
        await SubmitRecordAsync(page, "1001.13", ReferenceA);
        await ConfirmAsync(page, "¿Registrar cobro?", null, "Registrar cobro");
        await ExpectMessageAsync(page, "Cobro registrado.");
        var records = api.Calls(OrganizationA, "POST", "/cod-records");
        Assert.Equal(2, records.Length);
        Assert.Equal(records[0].Body, records[1].Body);
        Assert.NotEqual(firstKey, records[1].IdempotencyKey);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationB && call.Method == "POST");

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        api.AssertPendingQueriesOnly();
        trace.Passed();
    }

    /// <summary>
    /// SEC-004-PANEL-SCREENS-2026-10-13, AI-07 cod_control.rate_limit: the pending list (listOrders) answers 429 with
    /// Retry-After: 6. The common message counts down, announced once by the shell; "Actualizar lista" is disabled; the
    /// order's collection actions are not limited, and the pending list's reload after that write waits; at 0 the list is
    /// read once, by itself, and never again by itself.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Pending_list_waits_out_a_429_holding_its_reload_after_a_write_and_reads_once_at_zero()
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Add(OrganizationA, LookupOrder());
        api.Add(OrganizationA, PendingOrder());
        api.Add(OrganizationA, new CodOrder(SecondPendingOrderId, SecondPendingGuide, SecondPendingRecordId, 7_500)
        {
            CodStatus = "RECORDED",
            OrderStatus = "DELIVERED",
        });
        api.PendingPageSize = 1;
        api.PendingTooManyRequests = 1;

        await page.GotoAsync(new Uri(server.BaseAddress, "/finance/cod").AbsoluteUri);
        var countdown = page.Locator("main").GetByRole(AriaRole.Timer);
        await countdown.GetByText(TooManyRequestsText(6), new() { Exact = true }).WaitForAsync();
        Assert.Single(api.Calls(OrganizationA, "GET", "/api/v1/orders"));
        var reloadList = page.GetByRole(AriaRole.Button, new() { Name = "Actualizar lista", Exact = true });
        Assert.True(await reloadList.IsDisabledAsync());
        Assert.Equal(TooManyRequestsText(6), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.Null(await countdown.GetAttributeAsync("aria-live"));

        // The order's collection actions are not limited; the write's reload of the list waits for the end.
        await LookUpAsync(page, LookupOrderId);
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Orden {LookupOrderId}", Exact = true }).WaitForAsync();
        await SubmitRecordAsync(page, "1001.13", ReferenceA);
        await ConfirmAsync(page, "¿Registrar cobro?", null, "Registrar cobro");
        await ExpectMessageAsync(page, "Cobro registrado.");
        await ExpectDescriptionAsync(
            page,
            1,
            "Esperado: $1,001.13 MXN", "Estado: Cobrado, sin conciliar", "Cobrado: $1,001.13 MXN", "Permite entregar: Sí",
            "Permite cerrar: No");
        Assert.True(await countdown.IsVisibleAsync(), "the write and its reload had to finish during the wait");
        Assert.Single(api.Calls(OrganizationA, "GET", "/api/v1/orders"));
        Assert.Equal(TooManyRequestsText(6), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());

        // At 0 the list is read once, by itself; the message goes and the button comes back.
        await PendingRows(page).First.WaitForAsync(new() { Timeout = 15_000 });
        Assert.Equal(2, api.Calls(OrganizationA, "GET", "/api/v1/orders").Length);
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.False(await reloadList.IsDisabledAsync());
        await Task.Delay(2_500);
        Assert.Equal(2, api.Calls(OrganizationA, "GET", "/api/v1/orders").Length);

        // "Cargar más" sends the cursor with the same filter and appends the next page.
        Assert.Equal(1, await PendingRows(page).CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true }).ClickAsync();
        await WaitForAsync(async () => await PendingRows(page).CountAsync() == 3, "the next page was not appended");
        Assert.Equal(
            new[]
            {
                "cod_pending_reconciliation=true",
                "cod_pending_reconciliation=true",
                "cod_pending_reconciliation=true&cursor=p2",
            },
            api.Calls(OrganizationA, "GET", "/api/v1/orders").Select(call => call.Query));
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true }).CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    private static string TooManyRequestsText(int seconds) => seconds == 1
        ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
        : $"Demasiadas solicitudes. Espera {seconds} segundos e intenta de nuevo.";

    private static CodOrder LookupOrder() => new(LookupOrderId, LookupGuide, LookupRecordId, ExpectedCents)
    {
        OrderStatus = "DELIVERING",
        RevenueCents = 100_000,
        OwnCostCents = 45_050,
        OwnAssignments = 2,
        AllyCostCents = 30_000,
        AllyAssignments = 1,
        MarginBasisPoints = 2_495,
    };

    private static CodOrder PendingOrder() => new(PendingOrderId, PendingGuide, PendingRecordId, 25_000)
    {
        CodStatus = "RECORDED",
        OrderStatus = "DELIVERED",
    };

    private static ILocator RecordForm(IPage page) => page.Locator("main form:has(legend:text-is('Registrar cobro'))");

    private static ILocator PendingRows(IPage page) => page.Locator("main ul.opsPlainList > li");

    private static ILocator OpenDialog(IPage page) => page.Locator("dialog.confirmDialog[open]");

    private static async Task LookUpAsync(IPage page, string orderId)
    {
        await page.GetByLabel("ID de la orden", new() { Exact = true }).FillAsync(orderId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Consultar", Exact = true }).ClickAsync();
    }

    private static async Task SubmitRecordAsync(IPage page, string amount, string reference)
    {
        var form = RecordForm(page);
        await form.GetByLabel("Monto cobrado (MXN)").FillAsync(amount);
        await form.GetByLabel("Referencia", new() { Exact = false }).FillAsync(reference);
        await form.Locator("button[type=submit]").ClickAsync();
    }

    /// <summary>Checks the open confirmation (the description when given) and confirms it.</summary>
    private static async Task ConfirmAsync(IPage page, string title, string? description, string confirmLabel)
    {
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal(title, (await dialog.Locator("h2").InnerTextAsync()).Trim());
        var text = (await dialog.Locator("p").InnerTextAsync()).Trim();
        if (description is not null) Assert.Equal(description, text);
        Assert.DoesNotContain("Recibo", text, StringComparison.Ordinal);
        await dialog.GetByRole(AriaRole.Button, new() { Name = confirmLabel, Exact = true }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    /// <summary>The body is exactly the integer cents and the reference; the amount is a JSON integer literal.</summary>
    private static void AssertRecordBody(string? body, string reference)
    {
        Assert.NotNull(body);
        Assert.Contains($"\"amount_cents\":{ExpectedCents}", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(new[] { "amount_cents", "reference" }, root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(ExpectedCents.ToString(System.Globalization.CultureInfo.InvariantCulture), root.GetProperty("amount_cents").GetRawText());
        Assert.Equal(reference, root.GetProperty("reference").GetString());
    }

    /// <summary>AI-07 cod_control privacy: the COD reference is never echoed back after submission.</summary>
    private static async Task AssertReferenceNotEchoedAsync(IPage page)
    {
        Assert.DoesNotContain("Recibo sintético", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        var values = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('input, textarea')].map(field => field.value)");
        Assert.DoesNotContain(values, value => value.Contains("Recibo", StringComparison.Ordinal));
    }

    private static async Task<string[]> DescriptionAsync(IPage page, int index) =>
        await page.Locator("main dl.descList").Nth(index).EvaluateAsync<string[]>(
            """
            list => [...list.querySelectorAll(":scope > div")].map(pair =>
              `${pair.querySelector("dt").innerText.trim()}: ${pair.querySelector("dd").innerText.trim()}`)
            """);

    private static async Task ExpectDescriptionAsync(IPage page, int index, params string[] expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string[] actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.Locator("main dl.descList").CountAsync() > index)
            {
                actual = await DescriptionAsync(page, index);
                if (actual.SequenceEqual(expected, StringComparer.Ordinal)) return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The description did not match the API answer: {string.Join(" | ", actual)}");
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

    private static Task ExpectTextAsync(IPage page, string text) =>
        page.Locator("main").GetByText(text, new() { Exact = true }).WaitForAsync();

    private static async Task<string?> StepUpHrefAsync(IPage page) =>
        await page.Locator("main p.notice[role=status]")
            .GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true })
            .GetAttributeAsync("href");

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
        if (expected.Contains("/finance/cod"))
        {
            Assert.Equal("page", await page.Locator("nav.appNav a[href=\"/finance/cod\"]").GetAttributeAsync("aria-current"));
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
    /// switch, and fulfils /me/organization-contexts and the COD operations. Playwright runs the last matching route
    /// first, so the catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<CodApi> InstallAsync(
        IPage page,
        params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-cod-control-playwright",
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
        var api = new CodApi();
        await page.RouteAsync("**/api/v1/orders**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/cod-records/**", api.HandleAsync);
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

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    /// <summary>One order of an organization and its COD collection, as getOrderFinancials derives it.</summary>
    private sealed class CodOrder(string id, string publicId, string recordId, long expectedCents)
    {
        internal string Id { get; } = id;
        internal string PublicId { get; } = publicId;
        internal string RecordId { get; } = recordId;
        internal long ExpectedCents { get; } = expectedCents;
        internal string OrderStatus { get; init; } = "DELIVERED";
        internal long RevenueCents { get; init; } = 30_000;
        internal long OwnCostCents { get; init; } = 12_000;
        internal int OwnAssignments { get; init; } = 1;
        internal long AllyCostCents { get; init; }
        internal int AllyAssignments { get; init; }
        internal long MarginBasisPoints { get; init; } = 6_000;

        /// <summary>null until a collection is recorded, then RECORDED or RECONCILED.</summary>
        internal string? CodStatus { get; set; }

        private bool Recorded => CodStatus is "RECORDED" or "RECONCILED";

        internal object Record() => new
        {
            id = RecordId,
            order_id = Id,
            amount_cents = ExpectedCents,
            status = CodStatus,
            recorded_at = RecordedAt,
            reconciled_at = CodStatus == "RECONCILED" ? ReconciledAt : null,
        };

        internal object Financials()
        {
            var cost = OwnCostCents + AllyCostCents;
            var cod = new Dictionary<string, object?>
            {
                ["expected_cents"] = ExpectedCents,
                ["status"] = CodStatus,
                ["recorded"] = Recorded,
                ["reconciled"] = CodStatus == "RECONCILED",
                ["satisfies_delivery_requirement"] = ExpectedCents == 0 || Recorded,
                ["satisfies_close_requirement"] = ExpectedCents == 0 || CodStatus == "RECONCILED",
            };
            if (CodStatus is not null) cod["amount_cents"] = ExpectedCents;
            return new
            {
                order_id = Id,
                order_status = OrderStatus,
                currency = "MXN",
                revenue_cents = RevenueCents,
                cost_cents = cost,
                margin_cents = RevenueCents - cost,
                margin_basis_points = MarginBasisPoints,
                cost_by_modality = new object[]
                {
                    new { modality = "OWN", cost_cents = OwnCostCents, assignment_count = OwnAssignments },
                    new { modality = "EXTERNAL", cost_cents = 0L, assignment_count = 0 },
                    new { modality = "ALLY_CAPACITY", cost_cents = AllyCostCents, assignment_count = AllyAssignments },
                },
                cod,
                cod_record = CodStatus is null ? null : Record(),
            };
        }

        /// <summary>The AI-05 Order listOrders returns for the pending filter.</summary>
        internal object Order(string organization) => new
        {
            id = Id,
            public_id = PublicId,
            owner_org_id = organization,
            operator_org_id = (string?)null,
            status = OrderStatus,
            price_net = new { currency = "MXN", amount_cents = RevenueCents },
            version = 4,
            origin_location_id = "61000000-0000-0000-0000-000000000001",
            destination_location_id = "62000000-0000-0000-0000-000000000001",
            service_type = "SAME_DAY",
            quote_id = "63000000-0000-0000-0000-000000000001",
            city_id = "33333333-3333-3333-3333-333333333333",
            service_area_id = (string?)null,
            pricing_tier = "OCCASIONAL",
            total = new { currency = "MXN", amount_cents = RevenueCents },
            claim_window_ends_at = (string?)null,
            finalized_at = (string?)null,
            service_window = (object?)null,
        };
    }

    private sealed record CodCall(
        string Organization,
        string Method,
        string Path,
        string Query,
        string? IdempotencyKey,
        string? Body);

    /// <summary>The COD operations of AI-05 over per-organization orders; every call is recorded in order.</summary>
    private sealed class CodApi
    {
        private readonly object _gate = new();
        private readonly List<CodCall> _calls = [];
        private readonly Dictionary<(string Organization, string OrderId), CodOrder> _orders = [];
        private readonly List<(string Organization, CodOrder Order)> _ordered = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();

        /// <summary>Statuses the next recordCodCollection calls answer before the write succeeds.</summary>
        internal ConcurrentQueue<int> RecordFailures { get; } = new();

        /// <summary>getOrderFinancials and the pending list answer 403 MFA_REQUIRED.</summary>
        internal bool MfaRequired { get; set; }

        /// <summary>The next pending-list reads answered 429 with Retry-After: 6.</summary>
        internal int PendingTooManyRequests { get; set; }

        internal int PendingPageSize { get; set; } = 50;

        internal IReadOnlyList<CodCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal void Add(string organization, CodOrder order)
        {
            lock (_gate)
            {
                _orders[(organization, order.Id)] = order;
                _ordered.Add((organization, order));
            }
        }

        internal CodCall[] Calls(string organization, string method, string pathPart) =>
            AllCalls.Where(call => call.Organization == organization && call.Method == method &&
                (pathPart == "/api/v1/orders" ? call.Path == pathPart : call.Path.EndsWith(pathPart, StringComparison.Ordinal)))
                .ToArray();

        /// <summary>FIN-PENDING-COD-LIST-FINANCE-2026-10-02 and AI-07: listOrders is only ever the pending filter.</summary>
        internal void AssertPendingQueriesOnly() =>
            Assert.All(
                AllCalls.Where(call => call.Path == "/api/v1/orders"),
                call => Assert.Matches("^cod_pending_reconciliation=true(&cursor=p2)?$", call.Query));

        /// <summary>Holds the organization's answers until the returned source is completed.</summary>
        internal TaskCompletionSource Hold(string organization) =>
            _holds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var organization = request.Headers.TryGetValue("x-organization-id", out var value) ? value : string.Empty;
            var call = new CodCall(
                organization,
                request.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                request.Headers.TryGetValue("idempotency-key", out var key) ? key : null,
                request.PostData);
            lock (_gate) _calls.Add(call);
            if (_holds.TryGetValue(organization, out var hold)) await hold.Task;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (call.Method == "GET" && call.Path == "/api/v1/orders")
            {
                await PendingAsync(route, organization, call.Query);
                return;
            }

            if (segments is ["api", "v1", "orders", var orderId, "financials"] && call.Method == "GET")
            {
                if (MfaRequired)
                {
                    await ProblemAsync(route, 403, "MFA_REQUIRED");
                    return;
                }

                var order = Find(organization, orderId);
                if (order is null) await ProblemAsync(route, 404);
                else await FulfillAsync(route, order.Financials());
                return;
            }

            if (segments is ["api", "v1", "orders", var recordOrderId, "cod-records"] && call.Method == "POST")
            {
                if (RecordFailures.TryDequeue(out var failure))
                {
                    await ProblemAsync(route, failure);
                    return;
                }

                var order = Find(organization, recordOrderId);
                if (order is null)
                {
                    await ProblemAsync(route, 404);
                    return;
                }

                using var body = JsonDocument.Parse(call.Body ?? "{}");
                if (order.CodStatus is not null)
                {
                    await ProblemAsync(route, 409, "COD_ALREADY_RECORDED");
                    return;
                }

                if (body.RootElement.GetProperty("amount_cents").GetInt64() != order.ExpectedCents)
                {
                    await ProblemAsync(route, 409, "COD_AMOUNT_MISMATCH");
                    return;
                }

                order.CodStatus = "RECORDED";
                await FulfillAsync(route, order.Record(), 201);
                return;
            }

            if (segments is ["api", "v1", "cod-records", var recordId, "reconcile"] && call.Method == "POST")
            {
                CodOrder? order;
                lock (_gate)
                {
                    order = _ordered.Where(entry => entry.Organization == organization && entry.Order.RecordId == recordId)
                        .Select(entry => entry.Order)
                        .FirstOrDefault();
                }

                if (order is null || order.CodStatus is null)
                {
                    await ProblemAsync(route, 404);
                    return;
                }

                if (order.CodStatus != "RECORDED")
                {
                    await ProblemAsync(route, 409, "COD_STATE_CONFLICT");
                    return;
                }

                order.CodStatus = "RECONCILED";
                await FulfillAsync(route, order.Record());
                return;
            }

            await ProblemAsync(route, 404);
        }

        private CodOrder? Find(string organization, string orderId)
        {
            lock (_gate) return _orders.GetValueOrDefault((organization, orderId));
        }

        private async Task PendingAsync(IRoute route, string organization, string query)
        {
            if (MfaRequired)
            {
                await ProblemAsync(route, 403, "MFA_REQUIRED");
                return;
            }

            if (PendingTooManyRequests > 0)
            {
                PendingTooManyRequests -= 1;
                await route.FulfillAsync(new RouteFulfillOptions
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

            CodOrder[] pending;
            lock (_gate)
            {
                pending = _ordered.Where(entry => entry.Organization == organization && entry.Order.CodStatus == "RECORDED")
                    .Select(entry => entry.Order)
                    .ToArray();
            }

            var cursor = System.Web.HttpUtility.ParseQueryString(query)["cursor"];
            var page = cursor == "p2" ? pending.Skip(PendingPageSize) : pending.Take(PendingPageSize);
            await FulfillAsync(route, new
            {
                items = page.Select(order => order.Order(organization)).ToArray(),
                next_cursor = cursor is null && pending.Length > PendingPageSize ? "p2" : null,
            });
        }
    }
}
