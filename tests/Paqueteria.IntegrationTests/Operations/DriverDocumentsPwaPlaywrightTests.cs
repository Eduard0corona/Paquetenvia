using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_roster documents; Y1-Y13; docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md)
/// in a real browser against the production Next build, with the API fulfilled in the browser in AI-05 shapes
/// (listOrganizationDrivers, listDriverDocuments, recordDriverDocument, revokeDriverDocument,
/// /me/organization-contexts). The "Documentos" column of "Repartidores" puts «Vencido» first, then "Incompletos",
/// then «Vence en N días» / «Revisión en N días», then "Completos", computed in Culiacán time whatever the device zone
/// (Y5, Y6, Y12). Its dialog records what dispatch reviewed in person - the type the vehicle requires, the printed
/// date or the permanent Sinaloa license, Aprobado or Rechazado with one fixed reason - with one Idempotency-Key per
/// submission, reused only to retry the same one, and the problem codes as messages that keep the form; "Revocar"
/// asks "¿Revocar documento?" with Cancelar focused and revokes only the current VALID document (Y10). Nothing asks for a
/// photo, a copy, a file or a document number (Y3); the document ids never reach the screen; nothing is stored in the
/// browser or logged; only AI-05 paths are called. With documents off the roster keeps its five columns
/// (DriverRosterPwaPlaywrightTests).
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class DriverDocumentsPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    /// <summary>15:00 on 2 November 2026 in Culiacán (UTC-7); already 3 November in Tokyo, the device's zone.</summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 2, 22, 0, 0, TimeSpan.Zero);

    private static readonly string[] AllowedCalls =
    [
        "GET /api/v1/me/organization-contexts",
        "GET /api/v1/drivers",
        "GET /api/v1/drivers/documents",
        "POST /api/v1/drivers/{driverId}/documents",
        "POST /api/v1/drivers/{driverId}/documents/{documentId}/revoke",
    ];

    private static readonly Driver Review = new("e1a00000-0000-4000-8000-0000000000a1", "DRV-7c2e9f10", "MOTORCYCLE");
    private static readonly Driver Missing = new("e2b00000-0000-4000-8000-0000000000b2", "DRV-0b4d8e21", "BICYCLE");
    private static readonly Driver Expired = new("e3c00000-0000-4000-8000-0000000000c3", "DRV-3f9a1c55", "CAR");
    private static readonly Driver Complete = new("e4d00000-0000-4000-8000-0000000000d4", "DRV-5e6f7a8b", "VAN");
    private static readonly Driver OtherOrganization = new("e5e00000-0000-4000-8000-0000000000e5", "DRV-9a8b7c6d", "VAN");

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Dispatcher_sees_each_drivers_documents_and_records_one_reviewed_in_person()
    {
        await using var session = await StartAsync();
        var (page, api) = (session.Page, session.Api);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Repartidores", Exact = true }).WaitForAsync();
        // By reference; the most urgent state wins, dot and text, never color alone.
        await ExpectColumnAsync(page, ("DRV-0b4d8e21", "Incompletos"), ("DRV-3f9a1c55", "Vencido"),
            ("DRV-5e6f7a8b", "Completos"), ("DRV-7c2e9f10", "Revisión en 18 días"));
        Assert.Equal(
            new[] { "Repartidor", "Vehículo", "Estado", "Documentos", "Paradas activas", "Última ubicación" },
            (await page.Locator("thead th").AllTextContentsAsync()).Select(text => text.Trim()));

        // The dialog of a driver without its identification.
        var dialog = await OpenDocumentsAsync(page, Missing);
        Assert.Equal(new[] { "Identificación oficial Falta" }, await LinesAsync(dialog));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar documento", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Heading, new() { Name = "Registrar documento", Exact = true }).WaitForAsync();
        await dialog.GetByText(
                "El repartidor te muestra el original. No tomes fotos ni copias; solo anota el tipo y la fecha.",
                new() { Exact = true })
            .WaitForAsync();
        // Only the types the driver's vehicle requires (Y11); no file, photo or number field (Y3).
        Assert.Equal(new[] { "Identificación oficial" }, await OptionsAsync(dialog.GetByLabel("Documento", new() { Exact = true })));
        Assert.Equal(0, await dialog.Locator("input[type=file], input[capture], video, canvas, img").CountAsync());
        Assert.Equal(0, await dialog.GetByLabel(new Regex("n[uú]mero|folio", RegexOptions.IgnoreCase)).CountAsync());

        // The decision first; then the printed date, valid that whole day in Culiacán.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText("Elige si lo apruebas o lo rechazas.", new() { Exact = true }).WaitForAsync();
        await dialog.GetByLabel("Aprobado", new() { Exact = true }).CheckAsync();
        Assert.Equal(0, await dialog.GetByLabel("Licencia permanente de Sinaloa").CountAsync());
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText("Escribe la fecha de vencimiento impresa completa (día, mes y año).", new() { Exact = true }).WaitForAsync();
        Assert.Empty(api.Records);
        await dialog.GetByLabel("Fecha de vencimiento impresa", new() { Exact = true }).FillAsync("2027-03-01");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();

        await dialog.GetByRole(AriaRole.Heading, new() { Name = $"Documentos de {Missing.Reference}", Exact = true }).WaitForAsync();
        await dialog.GetByText("Identificación oficial: documento registrado.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(new[] { "Identificación oficial Aprobado Vence el 1 mar 2027 Revocar" }, await LinesAsync(dialog));
        var record = Assert.Single(api.Records);
        Assert.Equal(OrganizationA, record.Organization);
        Assert.Equal(Missing.Id, record.DriverId);
        Assert.Matches(UuidPattern(), record.Key);
        Assert.Equal(
            """{"document_type":"IDENTITY","decision":"VALID","expiry_basis":"PRINTED_EXPIRY","expires_on":"2027-03-01"}""",
            record.Body);

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cerrar", Exact = true }).ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = $"Documentos de {Missing.Reference}", Exact = true })
            .EvaluateAsync<bool>("button => button === document.activeElement"));
        await ExpectColumnAsync(page, ("DRV-0b4d8e21", "Completos"), ("DRV-3f9a1c55", "Vencido"),
            ("DRV-5e6f7a8b", "Completos"), ("DRV-7c2e9f10", "Revisión en 18 días"));

        await session.FinishAsync();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task A_rejection_takes_one_fixed_reason_and_a_retry_reuses_its_key_while_a_new_submission_does_not()
    {
        await using var session = await StartAsync();
        var (page, api) = (session.Page, session.Api);
        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);

        var dialog = await OpenDocumentsAsync(page, Review);
        Assert.Equal(
            new[]
            {
                "Identificación oficial Aprobado Vence el 30 jun 2027 Revocar",
                "Licencia de conducir Aprobado Revisión el 20 nov 2026 Revisión en 18 días Revocar",
            },
            await LinesAsync(dialog));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar documento", Exact = true }).ClickAsync();
        var type = dialog.GetByLabel("Documento", new() { Exact = true });
        Assert.Equal(new[] { "Identificación oficial", "Licencia de conducir" }, await OptionsAsync(type));
        await type.SelectOptionAsync(new SelectOptionValue { Label = "Licencia de conducir" });
        await dialog.GetByLabel("Rechazado", new() { Exact = true }).CheckAsync();
        var reason = dialog.GetByLabel("Motivo", new() { Exact = true });
        // Y7: the fixed list, no free text.
        Assert.Equal(
            new[] { "Elige un motivo", "Ilegible", "Vencido", "No es el pedido", "No es del repartidor", "Incompleto", "No aceptado" },
            await OptionsAsync(reason));
        Assert.Equal(0, await dialog.Locator("textarea, input[type=text]").CountAsync());
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText("Elige el motivo del rechazo.", new() { Exact = true }).WaitForAsync();
        await reason.SelectOptionAsync(new SelectOptionValue { Label = "No es del repartidor" });

        // No valid answer: the form keeps everything and a retry sends the same key and body.
        api.RecordFailures.Enqueue((503, null));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText(
                "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.",
                new() { Exact = true })
            .WaitForAsync();
        Assert.Equal("No es del repartidor", await reason.EvaluateAsync<string>("select => select.selectedOptions[0].text"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText("Licencia de conducir: documento registrado.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(2, api.Records.Count);
        Assert.Equal(api.Records[0].Key, api.Records[1].Key);
        Assert.Equal(api.Records[0].Body, api.Records[1].Body);
        Assert.Equal(
            """{"document_type":"DRIVER_LICENSE","decision":"REJECTED","rejection_code":"HOLDER_MISMATCH"}""",
            api.Records[1].Body);
        Assert.Equal(
            new[] { "Identificación oficial Aprobado Vence el 30 jun 2027 Revocar", "Licencia de conducir Rechazado: No es del repartidor" },
            await LinesAsync(dialog));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cerrar", Exact = true }).ClickAsync();
        await ExpectColumnAsync(page, ("DRV-0b4d8e21", "Incompletos"), ("DRV-3f9a1c55", "Vencido"),
            ("DRV-5e6f7a8b", "Completos"), ("DRV-7c2e9f10", "Incompletos"));

        // A printed date already past: the API's code as its message, the form kept, and a new key for a new body.
        dialog = await OpenDocumentsAsync(page, Expired);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar documento", Exact = true }).ClickAsync();
        await dialog.GetByLabel("Aprobado", new() { Exact = true }).CheckAsync();
        var date = dialog.GetByLabel("Fecha de vencimiento impresa", new() { Exact = true });
        await date.FillAsync("2026-10-01");
        api.RecordFailures.Enqueue((409, "EXPIRY_DATE_OUT_OF_RANGE"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText(
                "La fecha ya pasó o está a más de 10 años; si ya venció, recházalo como Vencido",
                new() { Exact = true })
            .WaitForAsync();
        Assert.Equal("2026-10-01", await date.InputValueAsync());
        await date.FillAsync("2027-10-01");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText("Identificación oficial: documento registrado.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(4, api.Records.Count);
        Assert.NotEqual(api.Records[2].Key, api.Records[3].Key);
        Assert.NotEqual(api.Records[1].Key, api.Records[2].Key);
        Assert.Equal(new[] { "Identificación oficial Aprobado Vence el 1 oct 2027 Revocar" }, await LinesAsync(dialog));

        await session.FinishAsync();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task A_permanent_license_reads_Revision_and_dispatch_revokes_only_after_confirming()
    {
        await using var session = await StartAsync();
        var (page, api) = (session.Page, session.Api);
        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);

        // Y5: a permanent Sinaloa license asks no date; its review is today plus 12 months, in Culiacán.
        var dialog = await OpenDocumentsAsync(page, Review);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar documento", Exact = true }).ClickAsync();
        await dialog.GetByLabel("Documento", new() { Exact = true })
            .SelectOptionAsync(new SelectOptionValue { Label = "Licencia de conducir" });
        await dialog.GetByLabel("Aprobado", new() { Exact = true }).CheckAsync();
        await dialog.GetByLabel("Licencia permanente de Sinaloa", new() { Exact = true }).CheckAsync();
        await dialog.GetByText("Revisión el 2 nov 2027", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await dialog.GetByLabel("Fecha de vencimiento impresa").CountAsync());
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await dialog.GetByText("Licencia de conducir: documento registrado.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(
            """{"document_type":"DRIVER_LICENSE","decision":"VALID","expiry_basis":"PERMANENT_LICENSE_REVIEW"}""",
            Assert.Single(api.Records).Body);
        Assert.Equal(
            new[] { "Identificación oficial Aprobado Vence el 30 jun 2027 Revocar", "Licencia de conducir Aprobado Revisión el 2 nov 2027 Revocar" },
            await LinesAsync(dialog));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cerrar", Exact = true }).ClickAsync();
        await ExpectColumnAsync(page, ("DRV-0b4d8e21", "Incompletos"), ("DRV-3f9a1c55", "Vencido"),
            ("DRV-5e6f7a8b", "Completos"), ("DRV-7c2e9f10", "Completos"));

        // Y10: "¿Revocar documento?" with Cancelar focused; cancelling sends nothing.
        dialog = await OpenDocumentsAsync(page, Complete);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Revocar Seguro del vehículo", Exact = true }).ClickAsync();
        var confirm = page.Locator("dialog.confirmDialog:not(.documentsDialog)");
        await confirm.GetByRole(AriaRole.Heading, new() { Name = "¿Revocar documento?", Exact = true }).WaitForAsync();
        await confirm.GetByText(
                "El repartidor ya no recibirá pedidos nuevos; lo que lleva sigue y puedes reasignarlo desde la orden.",
                new() { Exact = true })
            .WaitForAsync();
        Assert.True(await confirm.GetByRole(AriaRole.Button, new() { Name = "Cancelar", Exact = true })
            .EvaluateAsync<bool>("button => button === document.activeElement"));
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Cancelar", Exact = true }).ClickAsync();
        Assert.Empty(api.Revokes);

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Revocar Seguro del vehículo", Exact = true }).ClickAsync();
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Revocar", Exact = true }).ClickAsync();
        await dialog.GetByText("Seguro del vehículo: documento revocado.", new() { Exact = true }).WaitForAsync();
        var revoke = Assert.Single(api.Revokes);
        Assert.Equal((OrganizationA, Complete.Id, api.DocumentId(Complete, "INSURANCE"), string.Empty, (string?)null), revoke);
        Assert.Equal(
            new[]
            {
                "Identificación oficial Aprobado Vence el 30 jun 2027 Revocar",
                "Tarjeta de circulación Aprobado Vence el 30 jun 2027 Revocar",
                "Seguro del vehículo Revocado Vence el 30 jun 2027",
            },
            await LinesAsync(dialog));

        // A document that is no longer the current one: its code as a message, nothing else changes.
        api.RevokeFailures.Enqueue((409, "DOCUMENT_NOT_REVOCABLE"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Revocar Tarjeta de circulación", Exact = true }).ClickAsync();
        await confirm.GetByRole(AriaRole.Button, new() { Name = "Revocar", Exact = true }).ClickAsync();
        await dialog.GetByText("Ese documento ya no es el vigente. Actualiza la lista.", new() { Exact = true }).WaitForAsync();
        Assert.Contains("Tarjeta de circulación Aprobado Vence el 30 jun 2027 Revocar", await LinesAsync(dialog));
        await page.Keyboard.PressAsync("Escape");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await ExpectColumnAsync(page, ("DRV-0b4d8e21", "Incompletos"), ("DRV-3f9a1c55", "Vencido"),
            ("DRV-5e6f7a8b", "Incompletos"), ("DRV-7c2e9f10", "Completos"));

        await session.FinishAsync();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task An_organization_switch_closes_the_dialog_and_drops_the_previous_documents()
    {
        await using var session = await StartAsync(
            (OrganizationA, OrganizationAName, "DISPATCHER"),
            (OrganizationB, OrganizationBName, "PLATFORM_ADMIN"));
        var (page, api) = (session.Page, session.Api);
        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/drivers").AbsoluteUri);
        var dialog = await OpenDocumentsAsync(page, Review);

        // The session moves to another organization while the dialog is open (another tab, the session's own event):
        // the dialog closes and nothing of the previous organization stays.
        await page.EvaluateAsync(
            $"() => window.__paquetenviaOperationsSession.requestOrganizationChange({JsonSerializer.Serialize(OrganizationB)})");
        await page.Locator("dialog.documentsDialog[open]").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await ExpectColumnAsync(page, ("DRV-9a8b7c6d", "Incompletos"));
        var main = await page.Locator("main").InnerTextAsync();
        foreach (var driver in new[] { Review, Missing, Expired, Complete })
        {
            Assert.DoesNotContain(driver.Reference, main, StringComparison.Ordinal);
        }

        dialog = await OpenDocumentsAsync(page, OtherOrganization);
        Assert.Equal(
            new[] { "Identificación oficial Rechazado: Ilegible", "Tarjeta de circulación Falta", "Seguro del vehículo Falta" },
            await LinesAsync(dialog));
        Assert.Contains(api.Reads, read => read.Organization == OrganizationB && read.Path == "/api/v1/drivers/documents");

        // The modal dialog keeps the rest of the page inert; Escape closes it, then the shell's switcher works, and the
        // first organization's dialog does not come back by itself.
        await page.Keyboard.PressAsync("Escape");
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationAName });
        await ExpectColumnAsync(page, ("DRV-0b4d8e21", "Incompletos"), ("DRV-3f9a1c55", "Vencido"),
            ("DRV-5e6f7a8b", "Completos"), ("DRV-7c2e9f10", "Revisión en 18 días"));
        Assert.Equal(0, await page.Locator("dialog[open]").CountAsync());
        await session.FinishAsync();
    }

    // ---------- helpers ----------

    private async Task<BrowserSession> StartAsync(params (string Id, string Name, string Role)[] organizations)
    {
        if (organizations.Length == 0) organizations = [(OrganizationA, OrganizationAName, "DISPATCHER")];
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale: dates must still read in Culiacán, in es-MX.
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.Clock.SetFixedTimeAsync(FixedNow.UtcDateTime);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page, server.BaseAddress);
        var api = await InstallAsync(page, organizations);
        return new BrowserSession(playwright, browser, context, trace, page, api, console, calls);
    }

    private static async Task<ILocator> OpenDocumentsAsync(IPage page, Driver driver)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = $"Documentos de {driver.Reference}", Exact = true }).ClickAsync();
        var dialog = page.Locator("dialog.documentsDialog[open]");
        await dialog.GetByRole(AriaRole.Heading, new() { Name = $"Documentos de {driver.Reference}", Exact = true }).WaitForAsync();
        return dialog;
    }

    /// <summary>Each line of the documents dialog as its visible words.</summary>
    private static async Task<string[]> LinesAsync(ILocator dialog) =>
        (await dialog.Locator("ul.documentsList > li").EvaluateAllAsync<string[]>(
            "items => items.map(item => item.innerText.replace(/\\s+/g, ' ').trim())"))
        .ToArray();

    private static Task<string[]> OptionsAsync(ILocator select) =>
        select.EvaluateAsync<string[]>("select => [...select.options].map(option => option.text)");

    /// <summary>Each row's reference and its "Documentos" state, in display order, dot and text.</summary>
    private static async Task ExpectColumnAsync(IPage page, params (string Reference, string State)[] rows)
    {
        var expected = rows.Select(row => $"{row.Reference}={row.State}").ToArray();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string[] actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await page.Locator("tbody tr").EvaluateAllAsync<string[]>(
                """
                rows => rows.map(row => {
                  const badge = row.cells[3].querySelector(".statusBadge");
                  const dot = badge?.querySelector(".statusDot")?.getAttribute("aria-hidden") === "true";
                  return `${row.cells[0].innerText.trim()}=${dot ? badge.querySelector(".statusBadgeLabel").textContent.trim() : "no-dot"}`;
                })
                """);
            if (actual.SequenceEqual(expected)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The Documentos column did not match: {string.Join(", ", actual)}; screen: {await page.Locator("main").InnerTextAsync()}");
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

    private static ConcurrentQueue<string> RecordCalls(IPage page, Uri web)
    {
        var webOrigin = web.GetLeftPart(UriPartial.Authority) + "/";
        var calls = new ConcurrentQueue<string>();
        page.Request += (_, request) =>
        {
            if (!request.Url.StartsWith("http", StringComparison.Ordinal)) return;
            if (request.Url.StartsWith(DriverStopsNextServerFixture.TestApiOrigin + "/", StringComparison.Ordinal))
            {
                var path = UuidPattern().Replace(new Uri(request.Url).AbsolutePath, "{id}");
                path = Regex.Replace(path, "^/api/v1/drivers/\\{id\\}/documents/\\{id\\}/revoke$", "/api/v1/drivers/{driverId}/documents/{documentId}/revoke");
                path = Regex.Replace(path, "^/api/v1/drivers/\\{id\\}/documents$", "/api/v1/drivers/{driverId}/documents");
                calls.Enqueue($"{request.Method} {path}");
                return;
            }

            if (!request.Url.StartsWith(webOrigin, StringComparison.Ordinal))
            {
                calls.Enqueue($"FOREIGN {request.Url}");
            }
        };
        return calls;
    }

    /// <summary>
    /// The in-memory operations session (Mock mode, as the other panel suites) with the shell's organization switch;
    /// /me/organization-contexts, and the four driver operations. Playwright runs the last matching route first, so the
    /// catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<DocumentsApi> InstallAsync(IPage page, (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "drv-doc-001-panel-playwright",
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
            200,
            organizations.Select((organization, index) => new
            {
                organization_id = organization.Id,
                display_name = organization.Name,
                role = organization.Role,
                is_default = index == 0,
            }).ToArray()));
        var api = new DocumentsApi();
        await page.RouteAsync("**/api/v1/drivers**", api.HandleAsync);
        return api;
    }

    private static Task FulfillAsync(IRoute route, int status, object body) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = status,
            ContentType = "application/json",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
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

    private sealed record Driver(string Id, string Reference, string Vehicle)
    {
        internal object ToJson() => new
        {
            driver_id = Id,
            driver_reference = Reference,
            vehicle_type = Vehicle,
            status = "ACTIVE",
            active_stop_count = 0,
            last_position_at = (string?)null,
        };
    }

    /// <summary>An AI-05 DriverDocument; the last valid day is local to Culiacán, expires_at 00:00 of the next one.</summary>
    private sealed record Document(
        string Id,
        string Type,
        string Status,
        string? RejectionCode,
        string? ValidThrough,
        string? Basis,
        string? RevokedAt)
    {
        internal JsonObject ToJson() => new()
        {
            ["id"] = Id,
            ["document_type"] = Type,
            ["status"] = Status,
            ["rejection_code"] = RejectionCode,
            ["expires_at"] = ValidThrough is null
                ? null
                : DateOnly.ParseExact(ValidThrough, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1)
                    .ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
            ["valid_through"] = ValidThrough,
            ["expiry_basis"] = Basis,
            ["recorded_at"] = "2026-10-01T18:00:00+00:00",
            ["revoked_at"] = RevokedAt,
        };
    }

    internal sealed record Read(string Organization, string Path);

    /// <summary>The roster and the documents of each organization; every write is recorded as the API received it.</summary>
    private sealed class DocumentsApi
    {
        private readonly object _gate = new();
        private readonly List<Read> _reads = [];
        private readonly List<(string Organization, string DriverId, string Key, string Body)> _records = [];
        private readonly List<(string Organization, string DriverId, string DocumentId, string Body, string? Key)> _revokes = [];
        private readonly Dictionary<string, Driver[]> _drivers = new()
        {
            [OrganizationA] = [Review, Missing, Expired, Complete],
            [OrganizationB] = [OtherOrganization],
        };
        private readonly Dictionary<string, List<(string Type, Document? Document)>> _slots;
        private int _counter;

        internal DocumentsApi()
        {
            _slots = new Dictionary<string, List<(string, Document?)>>
            {
                [Review.Id] =
                [
                    ("IDENTITY", Valid("IDENTITY", "2027-06-30")),
                    ("DRIVER_LICENSE", Valid("DRIVER_LICENSE", "2026-11-20", "PERMANENT_LICENSE_REVIEW")),
                ],
                [Missing.Id] = [("IDENTITY", null)],
                [Expired.Id] = [("IDENTITY", Valid("IDENTITY", "2026-11-01"))],
                [Complete.Id] =
                [
                    ("IDENTITY", Valid("IDENTITY", "2027-06-30")),
                    ("VEHICLE_CARD", Valid("VEHICLE_CARD", "2027-06-30")),
                    ("INSURANCE", Valid("INSURANCE", "2027-06-30")),
                ],
                [OtherOrganization.Id] =
                [
                    ("IDENTITY", new Document(NextId(), "IDENTITY", "REJECTED", "ILLEGIBLE", null, null, null)),
                    ("VEHICLE_CARD", null),
                    ("INSURANCE", null),
                ],
            };
        }

        internal ConcurrentQueue<(int Status, string? Code)> RecordFailures { get; } = new();

        internal ConcurrentQueue<(int Status, string? Code)> RevokeFailures { get; } = new();

        internal IReadOnlyList<Read> Reads
        {
            get
            {
                lock (_gate) return [.. _reads];
            }
        }

        internal IReadOnlyList<(string Organization, string DriverId, string Key, string Body)> Records
        {
            get
            {
                lock (_gate) return [.. _records];
            }
        }

        internal IReadOnlyList<(string Organization, string DriverId, string DocumentId, string Body, string? Key)> Revokes
        {
            get
            {
                lock (_gate) return [.. _revokes];
            }
        }

        internal string DocumentId(Driver driver, string type)
        {
            lock (_gate) return _slots[driver.Id].Single(slot => slot.Type == type).Document!.Id;
        }

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var path = new Uri(request.Url).AbsolutePath;
            var organization = request.Headers.TryGetValue("x-organization-id", out var value) ? value : string.Empty;
            lock (_gate) _reads.Add(new Read(organization, path));
            if (!_drivers.TryGetValue(organization, out var drivers))
            {
                await ProblemAsync(route, 403);
                return;
            }

            if (request.Method == "GET" && path == "/api/v1/drivers")
            {
                await FulfillAsync(route, 200, new { items = drivers.Select(driver => driver.ToJson()).ToArray(), next_cursor = (string?)null });
                return;
            }

            if (request.Method == "GET" && path == "/api/v1/drivers/documents")
            {
                JsonArray items;
                lock (_gate)
                {
                    items = new JsonArray(drivers.Select(driver => (JsonNode)new JsonObject
                    {
                        ["driver_id"] = driver.Id,
                        ["documents"] = new JsonArray(_slots[driver.Id].Select(slot => (JsonNode)new JsonObject
                        {
                            ["document_type"] = slot.Type,
                            ["document"] = slot.Document?.ToJson(),
                        }).ToArray()),
                    }).ToArray());
                }

                await FulfillAsync(route, 200, new JsonObject { ["documents_enabled"] = true, ["items"] = items, ["next_cursor"] = null });
                return;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var driverId = segments.Length >= 4 ? segments[3] : string.Empty;
            var known = drivers.Any(driver => driver.Id == driverId);
            if (request.Method == "POST" && segments.Length == 5 && segments[4] == "documents" && known)
            {
                await RecordAsync(route, organization, driverId);
                return;
            }

            if (request.Method == "POST" && segments.Length == 7 && segments[4] == "documents" && segments[6] == "revoke" && known)
            {
                await RevokeAsync(route, organization, driverId, segments[5]);
                return;
            }

            await ProblemAsync(route, 404);
        }

        private async Task RecordAsync(IRoute route, string organization, string driverId)
        {
            var request = route.Request;
            var key = request.Headers.TryGetValue("idempotency-key", out var header) ? header : string.Empty;
            var body = request.PostData ?? string.Empty;
            lock (_gate) _records.Add((organization, driverId, key, body));
            if (RecordFailures.TryDequeue(out var failure))
            {
                await ProblemAsync(route, failure.Status, failure.Code);
                return;
            }

            var json = JsonNode.Parse(body)!.AsObject();
            var type = (string)json["document_type"]!;
            var decision = (string)json["decision"]!;
            var basis = (string?)json["expiry_basis"];
            Document document = decision == "REJECTED"
                ? new Document(NextId(), type, "REJECTED", (string)json["rejection_code"]!, null, null, null)
                : new Document(
                    NextId(),
                    type,
                    "VALID",
                    null,
                    basis == "PERMANENT_LICENSE_REVIEW" ? "2027-11-02" : (string)json["expires_on"]!,
                    basis,
                    null);
            lock (_gate)
            {
                var slots = _slots[driverId];
                slots[slots.FindIndex(slot => slot.Type == type)] = (type, document);
            }

            await FulfillAsync(route, 201, document.ToJson());
        }

        private async Task RevokeAsync(IRoute route, string organization, string driverId, string documentId)
        {
            var request = route.Request;
            lock (_gate)
            {
                _revokes.Add((organization, driverId, documentId, request.PostData ?? string.Empty,
                    request.Headers.TryGetValue("idempotency-key", out var key) ? key : null));
            }

            if (RevokeFailures.TryDequeue(out var failure))
            {
                await ProblemAsync(route, failure.Status, failure.Code);
                return;
            }

            Document revoked;
            lock (_gate)
            {
                var slots = _slots[driverId];
                var index = slots.FindIndex(slot => slot.Document?.Id == documentId);
                if (index < 0)
                {
                    revoked = null!;
                }
                else
                {
                    revoked = slots[index].Document! with { Status = "REVOKED", RevokedAt = "2026-11-02T22:00:00+00:00" };
                    slots[index] = (slots[index].Type, revoked);
                }
            }

            if (revoked is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            await FulfillAsync(route, 200, revoked.ToJson());
        }

        private Document Valid(string type, string validThrough, string basis = "PRINTED_EXPIRY") =>
            new(NextId(), type, "VALID", null, validThrough, basis, null);

        private string NextId() =>
            $"d0c00000-0000-4000-8000-{Interlocked.Increment(ref _counter):x12}";
    }

    private sealed class BrowserSession(
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        PlaywrightFailureTrace trace,
        IPage page,
        DocumentsApi api,
        ConcurrentQueue<string> console,
        ConcurrentQueue<string> calls) : IAsyncDisposable
    {
        internal IPage Page => page;

        internal DocumentsApi Api => api;

        /// <summary>No id on screen or in the markup, nothing stored or logged, only AI-05 calls; then the trace is kept as passed.</summary>
        internal async Task FinishAsync()
        {
            Assert.DoesNotMatch(UuidPattern(), await page.Locator("body").InnerTextAsync());
            var markup = await page.Locator("main").EvaluateAsync<string>("main => main.outerHTML");
            Assert.DoesNotMatch(UuidPattern(), markup);
            var stored = await page.EvaluateAsync<string[]>(
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
            Assert.Empty(stored);
            Assert.Empty(console);
            Assert.All(calls, call => Assert.Contains(call, AllowedCalls));
            Assert.DoesNotContain(calls, call => call.StartsWith("FOREIGN", StringComparison.Ordinal));
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
