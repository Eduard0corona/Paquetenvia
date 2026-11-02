using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for AI-07 <c>incident_desk</c>
/// (<c>/ops/incidents</c>, UI-001-SCREENS-CSV-INC-COD, API-INC-LIST-PROOFS-2026-09-29,
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11, Z13) in a real browser against the production Next build. The API is
/// fulfilled in the browser with AI-05 shapes (listIncidents, listOrderProofs, openIncident, resolveIncident,
/// /me/organization-contexts): the list is newest first, filterable by status and order, and "Cargar más" keeps both
/// filters; a malformed order filter is refused before any request; a null next_action reads "Lo decide despacho";
/// an incident is opened with the AI-05 enums and with evidence picked from the order's proof metadata, with no image,
/// download or signed URL ever requested; occurred_at is typed in Mazatlán and sent as UTC, and later than now plus
/// 5 minutes or older than 72 hours is refused before any request; a resolution is RESOLVED or REJECTED with the reason
/// sent exactly as typed, and the returned incident replaces the listed one where it is; neither the description nor
/// the reason is echoed back; a write reuses its Idempotency-Key on a 5xx retry; PLATFORM_ADMIN without MFA gets the
/// step-up; DRIVER, FINANCE and VIEWER have no access; a tenant switch clears the incidents, the filters, the proofs,
/// the typed text and the pending keys. The screen calls only AI-05 paths, keeps nothing in browser storage, logs
/// nothing and shows every time in America/Mazatlan whatever the device zone. It shows no amount, so the integer-cents
/// rule has nothing to check here.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class IncidentDeskPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    private const string OrderOne = "1c000000-0000-4000-8000-000000000001";
    private const string OrderTwo = "1c000000-0000-4000-8000-000000000002";
    private const string OrderWithoutIncidents = "1c000000-0000-4000-8000-000000000003";
    private const string OrderOfB = "2c000000-0000-4000-8000-000000000001";

    /// <summary>Newest first, as listIncidents orders them.</summary>
    private const string IncidentOne = "1d000000-0000-4000-8000-000000000001";
    private const string IncidentTwo = "1d000000-0000-4000-8000-000000000002";
    private const string IncidentThree = "1d000000-0000-4000-8000-000000000003";
    private const string IncidentFour = "1d000000-0000-4000-8000-000000000004";
    private const string IncidentFive = "1d000000-0000-4000-8000-000000000005";
    private const string IncidentOfB = "2d000000-0000-4000-8000-000000000001";

    private const string ProofPhoto = "1e000000-0000-4000-8000-000000000001";
    private const string ProofSignature = "1e000000-0000-4000-8000-000000000002";
    private const string ProofCode = "1e000000-0000-4000-8000-000000000003";

    /// <summary>11:00 on 2 November in Mazatlán, already 03:00 on 3 November in Tokyo.</summary>
    private const string FixedNow = "2026-11-02T18:00:00Z";

    private const string Description = "Fachada sin número visible; el vecino indica que el destinatario se mudó.";
    private const string RetryMessage =
        "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.";
    private const string MfaHint = "Abrir y resolver incidencias con tu rol requiere verificar tu identidad (MFA).";
    private const string InvalidOrder = "El ID de la orden no es válido.";

    private static readonly IncidentRecord[] OrganizationAIncidents =
    [
        new(IncidentOne, OrderOne, "OPEN", "HIGH", "RECIPIENT_ABSENT", null, true, "2026-11-02T16:10:00Z", "2026-11-02T20:10:00Z", [ProofPhoto]),
        new(IncidentTwo, OrderTwo, "INVESTIGATING", "MEDIUM", "ADDRESS_NOT_FOUND", "RESCHEDULED", false, "2026-11-02T14:00:00Z", "2026-11-02T18:00:00Z", [ProofSignature]),
        new(IncidentThree, OrderOne, "RESOLVED", "LOW", "RECIPIENT_REFUSED", "RETURNING", true, "2026-11-01T22:30:00Z", "2026-11-02T02:30:00Z", [ProofPhoto]),
        new(IncidentFour, OrderTwo, "OPEN", "CRITICAL", "SECURITY_RISK", "RETURNING", true, "2026-11-01T15:00:00Z", "2026-11-01T19:00:00Z", [ProofSignature]),
        new(IncidentFive, OrderTwo, "OPEN", "LOW", "ACCESS_RESTRICTED", "RESCHEDULED", false, "2026-10-31T01:45:00Z", "2026-10-31T05:45:00Z", [ProofSignature]),
    ];

    /// <summary>The rows as the table shows them, times in Mazatlán (in Tokyo 1 November 22:30 UTC is already 2 November).</summary>
    private static readonly IReadOnlyDictionary<string, string> Rows = new Dictionary<string, string>
    {
        [IncidentOne] = $"{IncidentOne} | Abrir orden | Abierta | Alta | No había nadie | Lo decide despacho | Con custodia | 2 nov 2026, 09:10 | 2 nov 2026, 13:10",
        [IncidentTwo] = $"{IncidentTwo} | Abrir orden | En investigación | Media | Dirección incorrecta | Reprogramar | Sin custodia | 2 nov 2026, 07:00 | 2 nov 2026, 11:00",
        [IncidentThree] = $"{IncidentThree} | Abrir orden | Resuelta | Baja | Rechazó el paquete | Devolver al remitente | Con custodia | 1 nov 2026, 15:30 | 1 nov 2026, 19:30",
        [IncidentFour] = $"{IncidentFour} | Abrir orden | Abierta | Crítica | Riesgo de seguridad | Devolver al remitente | Con custodia | 1 nov 2026, 08:00 | 1 nov 2026, 12:00",
        [IncidentFive] = $"{IncidentFive} | Abrir orden | Abierta | Baja | Acceso restringido | Reprogramar | Sin custodia | 30 oct 2026, 18:45 | 30 oct 2026, 22:45",
    };

    private static readonly ProofRecord[] OrderOneProofs =
    [
        new(ProofPhoto, "DELIVERY_PHOTO", new string('a', 64), "2026-11-02T15:55:00Z"),
        new(ProofSignature, "SIGNATURE", new string('b', 64), "2026-11-02T15:57:00Z"),
        new(ProofCode, "DELIVERY_CODE", new string('c', 64), "2026-11-02T16:00:00Z"),
    ];

    /// <summary>The AI-05 operations of incident_desk and the shell's role read; any other call is a failure.</summary>
    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/incidents$"),
        new("^GET /api/v1/incidents/[0-9a-f-]{36}$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/proofs$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/incidents$"),
        new("^POST /api/v1/incidents/[0-9a-f-]{36}/resolution$"),
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
    public async Task Dispatcher_lists_newest_first_filtered_by_status_and_order_and_pages_keeping_both_filters()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale: every time still reads in Mazatlán.
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
        api.Seed(OrganizationA, OrganizationAIncidents);
        api.PageSize = 2;

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        Assert.NotNull(response);
        // /ops is never stored (next.config.ts privateNoStore).
        Assert.Contains("no-store", (await response.AllHeadersAsync())["cache-control"], StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Incidencias", Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, "DISPATCHER");
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo);
        Assert.Equal(0, await page.Locator("main p.noticeInfo").CountAsync());
        Assert.Equal(
            new[] { "Incidencia", "Orden", "Estado", "Severidad", "Motivo", "Siguiente acción", "Custodia", "Ocurrió", "SLA" },
            (await page.Locator("main table.opsTable thead th").AllInnerTextsAsync()).Select(text => text.Trim()));
        Assert.Equal(
            new[] { $"/ops/orders/{OrderOne}", $"/ops/orders/{OrderTwo}" },
            await page.Locator("main table.opsTable tbody a").EvaluateAllAsync<string[]>(
                "links => links.map(link => link.getAttribute('href'))"));

        // "Cargar más" follows next_cursor and appends the older pages.
        await LoadMoreButton(page).ClickAsync();
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour);
        await LoadMoreButton(page).ClickAsync();
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        Assert.Equal(0, await LoadMoreButton(page).CountAsync());

        // The order filter, by Enter in its field, and "Cargar más" keeps it.
        await OrderFilter(page).FillAsync(OrderTwo);
        await OrderFilter(page).PressAsync("Enter");
        await ExpectRowsAsync(page, IncidentTwo, IncidentFour);
        await LoadMoreButton(page).ClickAsync();
        await ExpectRowsAsync(page, IncidentTwo, IncidentFour, IncidentFive);

        // A status change keeps the order on screen; an order without incidents shows the empty state.
        await StatusFilter(page).SelectOptionAsync(new SelectOptionValue { Label = "Abierta" });
        await ExpectRowsAsync(page, IncidentFour, IncidentFive);
        await OrderFilter(page).FillAsync(OrderWithoutIncidents);
        await RefreshButton(page).ClickAsync();
        await ExpectTextAsync(page, "Sin incidencias para mostrar.");
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());

        // A malformed order is refused before any request; the list in effect stays.
        var listed = api.ListQueries(OrganizationA).Length;
        await OrderFilter(page).FillAsync("ORD_IncidenciaPlaywright01");
        await RefreshButton(page).ClickAsync();
        await ExpectErrorsAsync(page, InvalidOrder);
        await Task.Delay(300);
        Assert.Equal(listed, api.ListQueries(OrganizationA).Length);
        await ExpectTextAsync(page, "Sin incidencias para mostrar.");

        // An empty order field lists every order again, with the status in effect.
        await OrderFilter(page).FillAsync(string.Empty);
        await RefreshButton(page).ClickAsync();
        await ExpectRowsAsync(page, IncidentOne, IncidentFour);
        await ExpectErrorsAsync(page);
        Assert.Equal(
            new[]
            {
                string.Empty,
                "cursor=o2",
                "cursor=o4",
                $"order_id={OrderTwo}",
                $"order_id={OrderTwo}&cursor=o2",
                $"status=OPEN&order_id={OrderTwo}",
                $"status=OPEN&order_id={OrderWithoutIncidents}",
                "status=OPEN",
            },
            api.ListQueries(OrganizationA));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        Assert.All(api.AllCalls, call => Assert.Equal(OrganizationA, call.Organization));
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Dispatcher_opens_with_listed_evidence_in_Mazatlan_time_reusing_the_key_on_a_5xx_without_echoing_the_description()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, OrganizationAIncidents);
        api.SeedProofs(OrganizationA, OrderOne, OrderOneProofs);
        api.PageSize = 2;

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo);
        var form = OpenForm(page);
        Assert.Equal("FAILED_ATTEMPT", await form.Locator("input[name=type]").InputValueAsync());
        await ExpectTextAsync(page, "Captura la orden y consulta sus evidencias para elegirlas.");
        // The AI-05 enums, with their Spanish labels.
        Assert.Equal(new[] { "Elige", "Baja", "Media", "Alta", "Crítica" }, await OptionsAsync(form, "severity"));
        Assert.Equal(
            new[]
            {
                "Elige", "No había nadie", "Dirección incorrecta", "Rechazó el paquete", "Acceso restringido",
                "No pagó el cobro", "Paquete dañado", "Riesgo de seguridad",
            },
            await OptionsAsync(form, "reason_code"));
        Assert.Equal(new[] { "Elige", "Reprogramar", "Devolver al remitente" }, await OptionsAsync(form, "next_action"));

        // The evidence comes from the order's proof metadata only: a malformed order is refused before any request.
        await form.GetByLabel("ID de la orden", new() { Exact = true }).FillAsync("ORD_IncidenciaPlaywright01");
        await ProofsButton(page).ClickAsync();
        await ExpectErrorsAsync(page, InvalidOrder);
        Assert.Empty(api.ProofQueries(OrganizationA));
        await form.GetByLabel("ID de la orden", new() { Exact = true }).FillAsync(OrderOne);
        await ProofsButton(page).ClickAsync();
        await form.Locator("input[name=evidence]").Nth(1).WaitForAsync();
        await form.GetByRole(AriaRole.Button, new() { Name = "Cargar más evidencias", Exact = true }).ClickAsync();
        await form.Locator("input[name=evidence]").Nth(2).WaitForAsync();
        Assert.Equal(
            new[]
            {
                "Foto de entrega · capturada 2 nov 2026, 08:55",
                "Firma · capturada 2 nov 2026, 08:57",
                "Código de entrega · capturada 2 nov 2026, 09:00",
            },
            await form.Locator("label:has(input[name=evidence])").EvaluateAllAsync<string[]>(
                "labels => labels.map(label => label.textContent.replace(/\\s+/g, ' ').trim())"));
        Assert.Equal(new[] { string.Empty, "cursor=q2" }, api.ProofQueries(OrganizationA));
        var shown = await page.Locator("body").InnerTextAsync();
        Assert.All(OrderOneProofs, proof => Assert.DoesNotContain(proof.Sha256, shown, StringComparison.Ordinal));
        Assert.Equal(0, await page.Locator("main img, main a[download]").CountAsync());

        await form.Locator("select[name=severity]").SelectOptionAsync(new SelectOptionValue { Label = "Alta" });
        await form.Locator("select[name=reason_code]").SelectOptionAsync(new SelectOptionValue { Label = "Dirección incorrecta" });
        await form.Locator("select[name=next_action]").SelectOptionAsync(new SelectOptionValue { Label = "Devolver al remitente" });
        await form.Locator("textarea[name=description]").FillAsync(Description);
        await form.Locator($"input[name=evidence][value=\"{ProofPhoto}\"]").CheckAsync();
        await form.Locator($"input[name=evidence][value=\"{ProofCode}\"]").CheckAsync();

        // occurred_at in Mazatlán: later than now plus 5 minutes, older than 72 hours, or without evidence is refused.
        await OpenAsync(page, "2026-11-02T11:06");
        await ExpectErrorsAsync(page, "La hora del intento no puede estar en el futuro.");
        await OpenAsync(page, "2026-10-30T10:59");
        await ExpectErrorsAsync(page, "El intento tiene más de 72 horas; el servidor no lo admite.");
        await form.Locator($"input[name=evidence][value=\"{ProofCode}\"]").UncheckAsync();
        await form.Locator($"input[name=evidence][value=\"{ProofPhoto}\"]").UncheckAsync();
        await OpenAsync(page, "2026-11-02T10:30");
        await ExpectErrorsAsync(page, "Indica de 1 a 10 evidencias.");
        Assert.Empty(api.Calls(OrganizationA, "POST", "/incidents"));

        // 10:30 in Mazatlán is 17:30 UTC on a device whose own zone reads 02:30 on 3 November; a 503 keeps the key.
        await form.Locator($"input[name=evidence][value=\"{ProofPhoto}\"]").CheckAsync();
        await form.Locator($"input[name=evidence][value=\"{ProofCode}\"]").CheckAsync();
        api.OpenFailures.Enqueue(503);
        await OpenAsync(page, "2026-11-02T10:30");
        await ExpectMessageAsync(page, RetryMessage);
        await form.GetByRole(AriaRole.Button, new() { Name = "Abrir incidencia", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, "Incidencia abierta y registrada.");
        var opens = api.Calls(OrganizationA, "POST", "/incidents");
        Assert.Equal(2, opens.Length);
        Assert.All(opens, open => Assert.Equal($"/api/v1/orders/{OrderOne}/incidents", open.Path));
        Assert.Matches(UuidPattern(), opens[0].IdempotencyKey ?? string.Empty);
        Assert.Equal(opens[0].IdempotencyKey, opens[1].IdempotencyKey);
        Assert.Equal(opens[0].Body, opens[1].Body);
        Assert.Equal(
            BrowserJson(new
            {
                type = "FAILED_ATTEMPT",
                severity = "HIGH",
                description = Description,
                reason_code = "ADDRESS_NOT_FOUND",
                next_action = "RETURNING",
                occurred_at = "2026-11-02T17:30:00.000Z",
                evidence_proof_ids = new[] { ProofPhoto, ProofCode },
            }),
            opens[0].Body);

        // With no filter in effect the opened incident is the newest, so it goes first; the form, its evidence and the
        // description are gone.
        var opened = api.LastOpenedId;
        Assert.Equal(
            $"{opened} | Abrir orden | Abierta | Alta | Dirección incorrecta | Devolver al remitente | Con custodia | 2 nov 2026, 10:30 | 2 nov 2026, 14:30",
            (await RowsAsync(page))[0]);
        await ExpectRowsAsync(page, opened, IncidentOne, IncidentTwo);
        Assert.Equal(string.Empty, await form.GetByLabel("ID de la orden", new() { Exact = true }).InputValueAsync());
        Assert.Equal(string.Empty, await form.Locator("textarea[name=description]").InputValueAsync());
        Assert.Equal(string.Empty, await form.Locator("input[name=occurred_at]").InputValueAsync());
        await ExpectTextAsync(page, "Captura la orden y consulta sus evidencias para elegirlas.");
        await AssertNotEchoedAsync(page, Description);
        Assert.Contains(
            "Dirección incorrecta · Alta · Abierta · SLA 2 nov 2026, 14:30",
            await OptionsAsync(ResolveForm(page), "incident_id"));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Dispatcher_resolves_and_rejects_in_place_with_the_reason_as_typed_and_never_echoed()
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        api.Seed(OrganizationA, OrganizationAIncidents);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        var form = ResolveForm(page);
        // Only the pending incidents the list returned are offered, newest first.
        Assert.Equal(
            new[]
            {
                "Elige",
                "No había nadie · Alta · Abierta · SLA 2 nov 2026, 13:10",
                "Dirección incorrecta · Media · En investigación · SLA 2 nov 2026, 11:00",
                "Riesgo de seguridad · Crítica · Abierta · SLA 1 nov 2026, 12:00",
                "Acceso restringido · Baja · Abierta · SLA 30 oct 2026, 22:45",
            },
            await OptionsAsync(form, "incident_id"));
        Assert.Equal(new[] { "Elige", "Resolver", "Rechazar" }, await OptionsAsync(form, "outcome"));

        // A reason with surrounding spaces is refused before any request.
        await ResolveAsync(page, IncidentFour, "Resolver", " Entregado en segundo intento ");
        await ExpectErrorsAsync(
            page,
            "El motivo es obligatorio (máximo 500 caracteres) y no puede iniciar ni terminar con espacios.");
        Assert.Empty(api.Calls(OrganizationA, "POST", "/resolution"));

        // The fourth incident is resolved after a 503, with the same key, and stays fourth.
        api.ResolveFailures.Enqueue(503);
        await ResolveAsync(page, IncidentFour, "Resolver", "Entregado en segundo intento");
        await ExpectMessageAsync(page, RetryMessage);
        await form.GetByRole(AriaRole.Button, new() { Name = "Registrar resolución", Exact = true }).ClickAsync();
        await ExpectMessageAsync(page, "Resolución registrada.");
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        Assert.Equal(Rows[IncidentFour].Replace("| Abierta |", "| Resuelta |", StringComparison.Ordinal), (await RowsAsync(page))[3]);
        var resolutions = api.Calls(OrganizationA, "POST", "/resolution");
        Assert.Equal(2, resolutions.Length);
        Assert.All(resolutions, call => Assert.Equal($"/api/v1/incidents/{IncidentFour}/resolution", call.Path));
        Assert.Equal(resolutions[0].IdempotencyKey, resolutions[1].IdempotencyKey);
        Assert.Equal("""{"outcome":"RESOLVED","reason":"Entregado en segundo intento"}""", resolutions[0].Body);
        Assert.Equal(resolutions[0].Body, resolutions[1].Body);
        Assert.Equal(string.Empty, await form.Locator("textarea[name=reason]").InputValueAsync());
        await AssertNotEchoedAsync(page, "Entregado en segundo intento");

        // The second is rejected, its reason sent exactly as typed (inner double space kept), and stays second.
        const string RejectReason = "Duplicada:  ya la reportó el repartidor";
        await ResolveAsync(page, IncidentTwo, "Rechazar", RejectReason);
        await ExpectMessageAsync(page, "Resolución registrada.");
        Assert.Equal(Rows[IncidentTwo].Replace("| En investigación |", "| Rechazada |", StringComparison.Ordinal), (await RowsAsync(page))[1]);
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        var reject = api.Calls(OrganizationA, "POST", "/resolution").Last();
        Assert.Equal($"/api/v1/incidents/{IncidentTwo}/resolution", reject.Path);
        Assert.Equal(BrowserJson(new { outcome = "REJECTED", reason = RejectReason }), reject.Body);
        Assert.NotEqual(resolutions[0].IdempotencyKey, reject.IdempotencyKey);
        await AssertNotEchoedAsync(page, "ya la reportó el repartidor");
        Assert.Equal(
            new[]
            {
                "Elige", "No había nadie · Alta · Abierta · SLA 2 nov 2026, 13:10",
                "Acceso restringido · Baja · Abierta · SLA 30 oct 2026, 22:45",
            },
            await OptionsAsync(form, "incident_id"));

        // An incident another dispatcher closed meanwhile: the conflict is explained and the row is not guessed;
        // "Actualizar" reads it again from the API.
        api.CloseOnServer(OrganizationA, IncidentFive);
        await ResolveAsync(page, IncidentFive, "Resolver", "Entregado en tercer intento");
        await ExpectMessageAsync(page, "La incidencia ya está cerrada o su estado no permite esta acción.");
        Assert.Equal(Rows[IncidentFive], (await RowsAsync(page))[4]);
        await RefreshButton(page).ClickAsync();
        await WaitForAsync(
            async () => (await RowsAsync(page)).ElementAtOrDefault(4) ==
                Rows[IncidentFive].Replace("| Abierta |", "| Resuelta |", StringComparison.Ordinal),
            "the list was not read again");
        Assert.Equal(2, api.ListQueries(OrganizationA).Length);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 incident_desk roles: PLATFORM_ADMIN lists, reads, opens and resolves only with MFA. Without it every read
    /// answers 403 MFA_REQUIRED and the screen offers the step-up back to /ops/incidents before showing any incident.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Platform_admin_without_MFA_gets_the_step_up_before_any_incident_or_proof_is_shown()
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
        api.Seed(OrganizationA, OrganizationAIncidents);
        api.SeedProofs(OrganizationA, OrderOne, OrderOneProofs);
        api.MfaRequired = true;

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        await ExpectMessageAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        Assert.Equal("/login?mfa=required&return_url=%2Fops%2Fincidents", await StepUpHrefAsync(page));
        await page.Locator("main p.noticeInfo").GetByText(MfaHint, new() { Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        await ExpectTextAsync(page, "Sin incidencias para mostrar.");
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());

        // The order's proofs need MFA too: the step-up again, and no proof shown.
        await OpenForm(page).GetByLabel("ID de la orden", new() { Exact = true }).FillAsync(OrderOne);
        await ProofsButton(page).ClickAsync();
        await WaitForAsync(() => api.ProofQueries(OrganizationA).Length == 1, "the proofs were not requested");
        await ExpectMessageAsync(page, "Esta acción requiere verificar tu identidad (MFA).");
        Assert.Equal(0, await OpenForm(page).Locator("input[name=evidence]").CountAsync());

        // With the second factor verified the list is shown, and the hint stays.
        api.MfaRequired = false;
        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        await page.Locator("main p.noticeInfo").GetByText(MfaHint, new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await Message(page).CountAsync());
        Assert.DoesNotContain(api.AllCalls, call => call.Method == "POST");

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>AI-07 /ops/incidents roles: DRIVER, FINANCE and VIEWER never reach the desk and never read incidents.</summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("DRIVER")]
    [InlineData("FINANCE")]
    [InlineData("VIEWER")]
    public async Task Roles_without_the_incident_desk_are_not_offered_it_and_never_read_incidents(string role)
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
        api.Seed(OrganizationA, OrganizationAIncidents);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await ExpectTextAsync(page, "Tu rol en la organización activa no gestiona incidencias.");
        await ExpectNavigationAsync(page, role);
        await Task.Delay(500);
        Assert.Empty(api.AllCalls);
        Assert.Equal(0, await OpenForm(page).CountAsync());
        Assert.Equal(0, await ResolveForm(page).CountAsync());
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_clears_incidents_filters_proofs_typed_text_and_pending_keys()
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
        await page.Clock.SetFixedTimeAsync(FixedNow);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(
            page,
            (OrganizationA, OrganizationAName, "DISPATCHER"),
            (OrganizationB, OrganizationBName, "PLATFORM_ADMIN"));
        api.Seed(OrganizationA, OrganizationAIncidents);
        api.SeedProofs(OrganizationA, OrderOne, OrderOneProofs);
        api.Seed(
            OrganizationB,
            [
                new(IncidentOfB, OrderOfB, "OPEN", "MEDIUM", "RECIPIENT_REFUSED", "RESCHEDULED", true,
                    "2026-11-02T15:00:00Z", "2026-11-02T19:00:00Z", ["2e000000-0000-4000-8000-000000000001"]),
            ]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/incidents").AbsoluteUri);
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        await StatusFilter(page).SelectOptionAsync(new SelectOptionValue { Label = "Abierta" });
        await OrderFilter(page).FillAsync(OrderTwo);
        await RefreshButton(page).ClickAsync();
        await ExpectRowsAsync(page, IncidentFour, IncidentFive);

        // An opening that met a 503 keeps its key...
        await FillOpenFormAsync(page);
        api.OpenFailures.Enqueue(503);
        await OpenAsync(page, "2026-11-02T10:30");
        await ExpectMessageAsync(page, RetryMessage);
        var firstKey = Assert.Single(api.Calls(OrganizationA, "POST", "/incidents")).IdempotencyKey;

        // ...until the organization changes: incidents, filters, proofs, typed text and message go before B answers.
        var releaseOrganizationB = api.Hold(OrganizationB);
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationBName });
        await WaitForAsync(() => api.RoleReads(OrganizationB) > 0, "organization B was never read");
        var during = await page.Locator("main").InnerTextAsync();
        foreach (var hidden in new[] { IncidentFour, IncidentFive, "Foto de entrega", Description, RetryMessage })
        {
            Assert.DoesNotContain(hidden, during, StringComparison.Ordinal);
        }

        Assert.Equal(0, await OpenForm(page).CountAsync());
        releaseOrganizationB.TrySetResult();

        // PLATFORM_ADMIN in B (MFA satisfied): its own list, unfiltered, and every field empty.
        await ExpectRowsAsync(page, IncidentOfB);
        await page.Locator("main p.noticeInfo").GetByText(MfaHint, new() { Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        Assert.Equal(new[] { string.Empty }, api.ListQueries(OrganizationB));
        Assert.Equal(string.Empty, await OrderFilter(page).InputValueAsync());
        Assert.Equal(string.Empty, await StatusFilter(page).InputValueAsync());
        Assert.Equal(string.Empty, await OpenForm(page).GetByLabel("ID de la orden", new() { Exact = true }).InputValueAsync());
        Assert.Equal(string.Empty, await OpenForm(page).Locator("textarea[name=description]").InputValueAsync());
        await ExpectTextAsync(page, "Captura la orden y consulta sus evidencias para elegirlas.");

        // Back in A the list is unfiltered and the same opening is a new submission with a new key.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationAName });
        await ExpectRowsAsync(page, IncidentOne, IncidentTwo, IncidentThree, IncidentFour, IncidentFive);
        Assert.Equal(string.Empty, api.ListQueries(OrganizationA).Last());
        await FillOpenFormAsync(page);
        await OpenAsync(page, "2026-11-02T10:30");
        await ExpectMessageAsync(page, "Incidencia abierta y registrada.");
        var opens = api.Calls(OrganizationA, "POST", "/incidents");
        Assert.Equal(2, opens.Length);
        Assert.Equal(opens[0].Body, opens[1].Body);
        Assert.NotEqual(firstKey, opens[1].IdempotencyKey);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationB && call.Method == "POST");

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>JSON as the browser's JSON.stringify writes it: non-ASCII letters are not escaped.</summary>
    private static string BrowserJson(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static ILocator OpenForm(IPage page) =>
        page.Locator("main form:has(legend:text-is('Abrir incidencia de intento fallido'))");

    private static ILocator ResolveForm(IPage page) => page.Locator("main form:has(legend:text-is('Resolver incidencia'))");

    /// <summary>The list's status select; a label that wraps a select also names it by its option texts.</summary>
    private static ILocator StatusFilter(IPage page) =>
        page.Locator("main label.opsInlineField", new() { HasTextString = "Estado" }).Locator("select");

    private static ILocator OrderFilter(IPage page) => page.GetByLabel("Orden", new() { Exact = true });

    private static ILocator RefreshButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });

    private static ILocator LoadMoreButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Cargar más", Exact = true });

    private static ILocator ProofsButton(IPage page) =>
        OpenForm(page).GetByRole(AriaRole.Button, new() { Name = "Ver evidencias de la orden", Exact = true });

    /// <summary>The screen's last server outcome (Feedback), not the shell's announcer.</summary>
    private static ILocator Message(IPage page) => page.Locator("main div.page > p.notice[role=status]");

    private static async Task FillOpenFormAsync(IPage page)
    {
        var form = OpenForm(page);
        await form.GetByLabel("ID de la orden", new() { Exact = true }).FillAsync(OrderOne);
        await ProofsButton(page).ClickAsync();
        await form.Locator($"input[name=evidence][value=\"{ProofPhoto}\"]").CheckAsync();
        await form.Locator("select[name=severity]").SelectOptionAsync(new SelectOptionValue { Label = "Alta" });
        await form.Locator("select[name=reason_code]").SelectOptionAsync(new SelectOptionValue { Label = "Dirección incorrecta" });
        await form.Locator("select[name=next_action]").SelectOptionAsync(new SelectOptionValue { Label = "Devolver al remitente" });
        await form.Locator("textarea[name=description]").FillAsync(Description);
    }

    private static async Task OpenAsync(IPage page, string occurredAtMazatlan)
    {
        var form = OpenForm(page);
        await form.Locator("input[name=occurred_at]").FillAsync(occurredAtMazatlan);
        await form.GetByRole(AriaRole.Button, new() { Name = "Abrir incidencia", Exact = true }).ClickAsync();
    }

    private static async Task ResolveAsync(IPage page, string incidentId, string outcome, string reason)
    {
        var form = ResolveForm(page);
        await form.Locator("select[name=incident_id]").SelectOptionAsync(incidentId);
        await form.Locator("select[name=outcome]").SelectOptionAsync(new SelectOptionValue { Label = outcome });
        await form.Locator("textarea[name=reason]").FillAsync(reason);
        await form.GetByRole(AriaRole.Button, new() { Name = "Registrar resolución", Exact = true }).ClickAsync();
    }

    private static async Task<string[]> OptionsAsync(ILocator form, string name) =>
        (await form.Locator($"select[name={name}] option").AllInnerTextsAsync()).Select(text => text.Trim()).ToArray();

    /// <summary>Each table row as "cell | cell | …".</summary>
    private static async Task<string[]> RowsAsync(IPage page) =>
        await page.Locator("main table.opsTable tbody tr").EvaluateAllAsync<string[]>(
            "rows => rows.map(row => [...row.cells].map(cell => cell.innerText.trim()).join(' | '))");

    /// <summary>The table lists exactly these incidents, in this order (the first cell is the incident id).</summary>
    private static async Task ExpectRowsAsync(IPage page, params string[] incidentIds)
    {
        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await RowsAsync(page);
            if (actual.Select(row => row.Split(" | ")[0]).SequenceEqual(incidentIds, StringComparer.Ordinal))
            {
                foreach (var cells in actual.Select(row => row.Split(" | ")).Where(cells => Rows.ContainsKey(cells[0])))
                {
                    // A seeded row reads exactly as seeded; a resolution changes only its status cell.
                    var seeded = Rows[cells[0]].Split(" | ");
                    Assert.Equal(seeded.Where((_, index) => index != 2), cells.Where((_, index) => index != 2));
                    Assert.Contains(cells[2], new[] { seeded[2], "Resuelta", "Rechazada" });
                }

                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The incident rows did not match: {string.Join(" || ", actual)}");
    }

    /// <summary>AI-07 incident_desk: the description and the resolution reason are never echoed back after submission.</summary>
    private static async Task AssertNotEchoedAsync(IPage page, string text)
    {
        Assert.DoesNotContain(text, await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        var values = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('input, textarea')].map(field => field.value)");
        Assert.DoesNotContain(values, value => value.Contains(text, StringComparison.Ordinal));
    }

    private static async Task ExpectErrorsAsync(IPage page, params string[] errors)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        IReadOnlyList<string> actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("main div.page > ul.notice[role=alert] li").AllInnerTextsAsync();
            if (actual.Select(text => text.Trim()).SequenceEqual(errors, StringComparer.Ordinal)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The validation errors did not match: {string.Join(" | ", actual)}");
    }

    private static Task ExpectMessageAsync(IPage page, string message) =>
        Message(page).GetByText(message, new() { Exact = false }).WaitForAsync();

    private static Task ExpectTextAsync(IPage page, string text) =>
        page.Locator("main").GetByText(text, new() { Exact = true }).WaitForAsync();

    private static async Task<string?> StepUpHrefAsync(IPage page) =>
        await Message(page)
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
        if (expected.Contains("/ops/incidents"))
        {
            Assert.Equal("page", await page.Locator("nav.appNav a[href=\"/ops/incidents\"]").GetAttributeAsync("aria-current"));
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

    /// <summary>
    /// Every API call is an AI-05 operation of this screen, and nothing goes to any third origin: no proof image,
    /// download or signed URL is ever requested.
    /// </summary>
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
    /// switch, and fulfils /me/organization-contexts and the incident operations. Playwright runs the last matching
    /// route first, so the catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<IncidentApi> InstallAsync(
        IPage page,
        params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-incident-desk-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        var api = new IncidentApi(organizations.Select((organization, index) => new
        {
            organization_id = organization.Id,
            display_name = organization.Name,
            role = organization.Role,
            is_default = index == 0,
        }).ToArray());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", api.HandleRoleReadAsync);
        await page.RouteAsync("**/api/v1/incidents**", api.HandleAsync);
        await page.RouteAsync("**/api/v1/orders/**", api.HandleAsync);
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

    /// <summary>An AI-05 Incident of one organization; the status changes when it is resolved.</summary>
    private sealed record IncidentRecord(
        string Id,
        string OrderId,
        string Status,
        string Severity,
        string ReasonCode,
        string? NextAction,
        bool CustodyAcquired,
        string OccurredAt,
        string SlaDueAt,
        string[] EvidenceProofIds)
    {
        internal string Status { get; set; } = Status;

        internal object Json() => new
        {
            id = Id,
            order_id = OrderId,
            status = Status,
            severity = Severity,
            reason_code = ReasonCode,
            next_action = NextAction,
            custody_acquired = CustodyAcquired,
            occurred_at = OccurredAt,
            sla_due_at = SlaDueAt,
            evidence_proof_ids = EvidenceProofIds,
        };
    }

    /// <summary>An AI-05 Proof: metadata only, never bytes, storage keys or URLs.</summary>
    private sealed record ProofRecord(string Id, string ProofType, string Sha256, string CapturedAt)
    {
        internal object Json() => new { id = Id, proof_type = ProofType, sha256 = Sha256, captured_at = CapturedAt };
    }

    private sealed record IncidentCall(
        string Organization,
        string Method,
        string Path,
        string Query,
        string? IdempotencyKey,
        string? Body);

    /// <summary>
    /// listIncidents, listOrderProofs, openIncident and resolveIncident of AI-05 over per-organization incidents and
    /// proofs: lists are newest first with strict filters and offset cursors, writes replay their Idempotency-Key, and
    /// every call is recorded in order.
    /// </summary>
    private sealed class IncidentApi(object[] contexts)
    {
        private static readonly string[] OpenFields =
            ["type", "severity", "description", "reason_code", "next_action", "occurred_at", "evidence_proof_ids"];

        private readonly object _gate = new();
        private readonly List<IncidentCall> _calls = [];
        private readonly Dictionary<string, List<IncidentRecord>> _incidents = [];
        private readonly Dictionary<(string Organization, string OrderId), ProofRecord[]> _proofs = [];
        private readonly Dictionary<string, (string Body, object Answer, int Status)> _replays = [];
        private readonly ConcurrentDictionary<string, int> _roleReads = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();
        private int _opened;

        internal ConcurrentQueue<int> OpenFailures { get; } = new();

        internal ConcurrentQueue<int> ResolveFailures { get; } = new();

        /// <summary>Every incident read and write answers 403 MFA_REQUIRED.</summary>
        internal bool MfaRequired { get; set; }

        internal int PageSize { get; set; } = 50;

        internal string LastOpenedId { get; private set; } = string.Empty;

        internal IReadOnlyList<IncidentCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal void Seed(string organization, IEnumerable<IncidentRecord> incidents)
        {
            lock (_gate) _incidents[organization] = incidents.Select(incident => incident with { }).ToList();
        }

        internal void SeedProofs(string organization, string orderId, ProofRecord[] proofs)
        {
            lock (_gate) _proofs[(organization, orderId)] = proofs;
        }

        /// <summary>Another dispatcher resolved the incident meanwhile.</summary>
        internal void CloseOnServer(string organization, string incidentId)
        {
            lock (_gate) _incidents[organization].Single(incident => incident.Id == incidentId).Status = "RESOLVED";
        }

        internal IncidentCall[] Calls(string organization, string method, string pathEnd) =>
            AllCalls.Where(call => call.Organization == organization && call.Method == method &&
                call.Path.EndsWith(pathEnd, StringComparison.Ordinal)).ToArray();

        internal string[] ListQueries(string organization) =>
            AllCalls.Where(call => call.Organization == organization && call.Method == "GET" && call.Path == "/api/v1/incidents")
                .Select(call => call.Query)
                .ToArray();

        internal string[] ProofQueries(string organization) =>
            AllCalls.Where(call => call.Organization == organization && call.Path.EndsWith("/proofs", StringComparison.Ordinal))
                .Select(call => call.Query)
                .ToArray();

        internal int RoleReads(string organization) => _roleReads.GetValueOrDefault(organization);

        /// <summary>Holds the organization's answers (its role read included) until the returned source is completed.</summary>
        internal TaskCompletionSource Hold(string organization) =>
            _holds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task HandleRoleReadAsync(IRoute route)
        {
            var organization = (await route.Request.AllHeadersAsync()).GetValueOrDefault("x-organization-id") ?? string.Empty;
            _roleReads.AddOrUpdate(organization, 1, (_, count) => count + 1);
            if (_holds.TryGetValue(organization, out var hold)) await hold.Task;
            await FulfillAsync(route, contexts);
        }

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var uri = new Uri(request.Url);
            var headers = await request.AllHeadersAsync();
            var call = new IncidentCall(
                headers.GetValueOrDefault("x-organization-id") ?? string.Empty,
                request.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                headers.GetValueOrDefault("idempotency-key"),
                request.PostData);
            lock (_gate) _calls.Add(call);
            if (_holds.TryGetValue(call.Organization, out var hold)) await hold.Task;
            if (MfaRequired)
            {
                await ProblemAsync(route, 403, "MFA_REQUIRED");
                return;
            }

            var segments = call.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (call.Method, segments)
            {
                case ("GET", ["api", "v1", "incidents"]):
                    await ListAsync(route, call);
                    return;
                case ("GET", ["api", "v1", "orders", var orderId, "proofs"]):
                    await ProofsAsync(route, call, orderId);
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "incidents"]):
                    await OpenAsync(route, call, orderId);
                    return;
                case ("POST", ["api", "v1", "incidents", var incidentId, "resolution"]):
                    await ResolveAsync(route, call, incidentId);
                    return;
                default:
                    await ProblemAsync(route, 404);
                    return;
            }
        }

        /// <summary>Strict query parsing: only status, order_id and cursor, each once, or 409 INVALID_REQUEST.</summary>
        private static Dictionary<string, string>? Query(string query, params string[] allowed)
        {
            var values = new Dictionary<string, string>();
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var name = Uri.UnescapeDataString(parts[0]);
                if (!allowed.Contains(name) || parts.Length != 2 || !values.TryAdd(name, Uri.UnescapeDataString(parts[1])))
                {
                    return null;
                }
            }

            return values;
        }

        private static (int Offset, bool Valid) Offset(Dictionary<string, string> query, string prefix)
        {
            if (!query.TryGetValue("cursor", out var cursor)) return (0, true);
            return cursor.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(cursor[1..], out var offset)
                ? (offset, true)
                : (0, false);
        }

        private async Task ListAsync(IRoute route, IncidentCall call)
        {
            var query = Query(call.Query, "status", "order_id", "cursor");
            var (offset, valid) = query is null ? (0, false) : Offset(query, "o");
            if (query is null || !valid)
            {
                await ProblemAsync(route, 409, "INVALID_REQUEST");
                return;
            }

            IncidentRecord[] matching;
            lock (_gate)
            {
                matching = _incidents.GetValueOrDefault(call.Organization, [])
                    .Where(incident => !query.TryGetValue("status", out var status) || incident.Status == status)
                    .Where(incident => !query.TryGetValue("order_id", out var orderId) || incident.OrderId == orderId)
                    .ToArray();
            }

            var page = matching.Skip(offset).Take(PageSize).Select(incident => incident.Json()).ToArray();
            await FulfillAsync(route, new
            {
                items = page,
                next_cursor = offset + PageSize < matching.Length ? $"o{offset + PageSize}" : null,
            });
        }

        private async Task ProofsAsync(IRoute route, IncidentCall call, string orderId)
        {
            var query = Query(call.Query, "cursor");
            var (offset, valid) = query is null ? (0, false) : Offset(query, "q");
            if (query is null || !valid)
            {
                await ProblemAsync(route, 409, "INVALID_REQUEST");
                return;
            }

            ProofRecord[] proofs;
            lock (_gate) proofs = _proofs.GetValueOrDefault((call.Organization, orderId), []);
            await FulfillAsync(route, new
            {
                items = proofs.Skip(offset).Take(PageSize).Select(proof => proof.Json()).ToArray(),
                next_cursor = offset + PageSize < proofs.Length ? $"q{offset + PageSize}" : null,
            });
        }

        private async Task OpenAsync(IRoute route, IncidentCall call, string orderId)
        {
            if (OpenFailures.TryDequeue(out var failure))
            {
                await ProblemAsync(route, failure);
                return;
            }

            if (await ReplayAsync(route, call)) return;
            using var body = JsonDocument.Parse(call.Body ?? "{}");
            var root = body.RootElement;
            if (!root.EnumerateObject().Select(property => property.Name).SequenceEqual(OpenFields))
            {
                await ProblemAsync(route, 409, "INVALID_REQUEST");
                return;
            }

            ProofRecord[] proofs;
            lock (_gate) proofs = _proofs.GetValueOrDefault((call.Organization, orderId), []);
            var evidence = root.GetProperty("evidence_proof_ids").EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (evidence.Any(id => proofs.All(proof => proof.Id != id)))
            {
                await ProblemAsync(route, 409, "EVIDENCE_NOT_AVAILABLE");
                return;
            }

            var occurredAt = DateTimeOffset.Parse(root.GetProperty("occurred_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var incident = new IncidentRecord(
                $"1d000000-0000-4000-8000-{0x100 + Interlocked.Increment(ref _opened):x12}",
                orderId,
                "OPEN",
                root.GetProperty("severity").GetString()!,
                root.GetProperty("reason_code").GetString()!,
                root.GetProperty("next_action").GetString(),
                true,
                occurredAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                occurredAt.AddHours(4).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                evidence);
            lock (_gate)
            {
                _incidents[call.Organization].Insert(0, incident);
                _replays[call.IdempotencyKey!] = (call.Body!, incident.Json(), 201);
                LastOpenedId = incident.Id;
            }

            await FulfillAsync(route, incident.Json(), 201);
        }

        private async Task ResolveAsync(IRoute route, IncidentCall call, string incidentId)
        {
            if (ResolveFailures.TryDequeue(out var failure))
            {
                await ProblemAsync(route, failure);
                return;
            }

            if (await ReplayAsync(route, call)) return;
            using var body = JsonDocument.Parse(call.Body ?? "{}");
            IncidentRecord? incident;
            lock (_gate) incident = _incidents.GetValueOrDefault(call.Organization, []).SingleOrDefault(item => item.Id == incidentId);
            if (incident is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            if (incident.Status is not ("OPEN" or "INVESTIGATING"))
            {
                await ProblemAsync(route, 409, "INCIDENT_STATE_CONFLICT");
                return;
            }

            lock (_gate)
            {
                incident.Status = body.RootElement.GetProperty("outcome").GetString()!;
                _replays[call.IdempotencyKey!] = (call.Body!, incident.Json(), 200);
            }

            await FulfillAsync(route, incident.Json());
        }

        /// <summary>A key already answered replays that answer for the same body and is IDEMPOTENCY_CONFLICT otherwise.</summary>
        private async Task<bool> ReplayAsync(IRoute route, IncidentCall call)
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
