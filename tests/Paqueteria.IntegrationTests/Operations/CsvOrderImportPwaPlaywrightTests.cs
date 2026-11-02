using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for AI-07 <c>csv_order_import</c>
/// (<c>/ops/orders/import</c>, UI-001-SCREENS-CSV-INC-COD, CSV-001, D6-COD-EXPECTED, COD-CAP-20000-2026-10-02,
/// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02, NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23, SEC-004-PANEL-SCREENS-2026-10-13)
/// in a real browser against the production Next build. The API is fulfilled in the browser with AI-05 shapes
/// (previewOrderCsv, commitOrderCsv, /me/organization-contexts): an empty file and one over 1 MiB are refused before any
/// request; the file goes only as the multipart part <c>file</c> under the fixed name <c>orders.csv</c>, and the
/// commit sends exactly the previewed bytes and the previewed content_digest; file and row errors are shown by line,
/// column and Spanish code, and each valid row's cod_expected_cents as MXN; the commit needs the prohibited-goods tick;
/// the WhatsApp box reads the AI-24 text and the fixed note, and sends its two fields only when ticked; a 422 replaces
/// the report; a commit answer for another digest fails closed; a 5xx retry reuses the batch Idempotency-Key, the other
/// answer to the box, a new file or a definitive answer takes a new one; 409 CONFLICT and IDEMPOTENCY_CONFLICT are
/// explained; preview and commit share one 429 wait that keeps the file, the report and the tick and sends nothing by
/// itself; a tenant switch clears the file, the report and the key; VIEWER, FINANCE and DRIVER have no access. The
/// screen calls only AI-05 paths, keeps nothing in browser storage, logs nothing, never lets the local file name leave
/// the browser and shows every amount from integer cents. It shows no time, so the Mazatlán rule has nothing to check.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class CsvOrderImportPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    private const string Header =
        "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel,cod_expected_cents";

    private const string AcceptedAt = "2026-11-01T18:00:00Z";
    private const string Acceptance = $"terms-synthetic-v1,privacy-synthetic-v1,{AcceptedAt},ASSISTED";

    private const string QuoteOne = "9a000000-0000-4000-8000-000000000001";
    private const string QuoteTwo = "9a000000-0000-4000-8000-000000000002";
    private const string QuoteThree = "9a000000-0000-4000-8000-000000000003";
    private const string QuoteFour = "9a000000-0000-4000-8000-000000000004";
    private const string QuoteFive = "9a000000-0000-4000-8000-000000000005";
    private const string QuoteSix = "9a000000-0000-4000-8000-000000000006";

    /// <summary>Local file names: none may leave the browser or be shown.</summary>
    private const string ErrorsFileName = "clientes-octubre-errores.csv";
    private const string ValidFileName = "clientes-octubre-lote.csv";
    private const string OtherFileName = "clientes-noviembre-lote.csv";

    private const string ConsentText = "El destinatario aceptó recibir avisos de su envío por WhatsApp";
    private const string ConsentNote = "Aplica a todas las filas de este archivo. Márcala solo si cada destinatario lo aceptó.";
    private const string ConsentVersion = "NTF-RECIPIENT-WHATSAPP-CONSENT-V1";
    private const string GoodsText = "Confirmo que ningún envío del archivo contiene artículos prohibidos";

    private const string RetryMessage =
        "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.";

    /// <summary>Four data rows: one valid, three refused for different reasons.</summary>
    private const string FileWithErrors =
        $"{Header}\n" +
        $"{QuoteOne},SENDER,{Acceptance},15050\n" +
        $"no-es-una-cotizacion,SENDER,{Acceptance},150.50\n" +
        $"{QuoteOne},RECIPIENT,{Acceptance},\n" +
        $"{QuoteTwo},SENDER,terms-synthetic-v1,privacy-synthetic-v1,2020-01-01T00:00:00Z,ASSISTED,\n";

    private const string FileWithOldHeader = "quote,payer\n9a000000-0000-4000-8000-000000000001,SENDER\n";

    /// <summary>Three valid rows: no COD, $1,001.13 and the $20,000.00 cap.</summary>
    private const string ValidFile =
        $"{Header}\n" +
        $"{QuoteThree},SENDER,{Acceptance},\n" +
        $"{QuoteFour},RECIPIENT,{Acceptance},100113\n" +
        $"{QuoteFive},SENDER,{Acceptance},2000000\n";

    private const string OtherValidFile = $"{Header}\n{QuoteSix},SENDER,{Acceptance},25000\n";

    private static readonly PreviewRow[] RowsWithErrors =
    [
        PreviewRow.Valid(2, QuoteOne, "SENDER", 15_050),
        PreviewRow.Invalid(3, null, ("quote_id", "QUOTE_ID_INVALID"), ("cod_expected_cents", "COD_EXPECTED_CENTS_INVALID")),
        PreviewRow.Invalid(4, QuoteOne, ("quote_id", "QUOTE_ID_DUPLICATED")),
        PreviewRow.Invalid(5, QuoteTwo),
    ];

    private static readonly PreviewRow[] ValidRows =
    [
        PreviewRow.Valid(2, QuoteThree, "SENDER", 0),
        PreviewRow.Valid(3, QuoteFour, "RECIPIENT", 100_113),
        PreviewRow.Valid(4, QuoteFive, "SENDER", 2_000_000),
    ];

    private static readonly string[] ValidReport =
    [
        $"2 | {QuoteThree} | Sin cobro | Válida",
        $"3 | {QuoteFour} | $1,001.13 MXN | Válida",
        $"4 | {QuoteFive} | $20,000.00 MXN | Válida",
    ];

    /// <summary>The AI-05 operations of csv_order_import and the shell's role read; any other call is a failure.</summary>
    private static readonly string[] AllowedCalls =
    [
        "GET /api/v1/me/organization-contexts",
        "POST /api/v1/orders/csv/preview",
        "POST /api/v1/orders/csv/commit",
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
    public async Task Dispatcher_refuses_bad_files_reports_each_row_and_commits_the_previewed_bytes_under_a_neutral_name()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        // A device in Tokyo with an English locale: amounts still read in es-MX pesos.
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
        api.Register(FileWithErrors, RowsWithErrors);
        api.Register(FileWithOldHeader, [], "HEADER_INVALID");
        api.Register(ValidFile, ValidRows);
        api.UnavailableQuotes.Add(QuoteFour);

        var response = await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders/import").AbsoluteUri);
        Assert.NotNull(response);
        // /ops is never stored (next.config.ts privateNoStore).
        Assert.Contains("no-store", (await response.AllHeadersAsync())["cache-control"], StringComparison.Ordinal);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Importar órdenes por CSV", Exact = true }).WaitForAsync();
        await ExpectNavigationAsync(page, "DISPATCHER");
        // The format help names the CSV-001 header, the optional COD column and the COD cap.
        Assert.Equal(
            new[]
            {
                "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel", "cod_expected_cents",
                "15050", "2000000",
            },
            await UploadForm(page).Locator("code").AllInnerTextsAsync());
        Assert.Contains(
            "El tope es $20,000.00 MXN por pedido (2000000 centavos)",
            await UploadForm(page).InnerTextAsync(),
            StringComparison.Ordinal);
        Assert.True(await PreviewButton(page).IsDisabledAsync());

        // An empty file and one over 1 MiB are refused before any request; exactly 1 MiB is accepted.
        await FileInput(page).SetInputFilesAsync(new FilePayload { Name = "pedidos-vacios.csv", MimeType = "text/csv", Buffer = [] });
        await ExpectErrorsAsync(page, "Selecciona un archivo CSV con contenido.");
        Assert.True(await PreviewButton(page).IsDisabledAsync());
        await FileInput(page).SetInputFilesAsync(
            new FilePayload { Name = "pedidos-grandes.csv", MimeType = "text/csv", Buffer = new byte[1_048_577] });
        await ExpectErrorsAsync(page, "El archivo supera 1 MiB; divídelo en lotes de hasta 500 filas.");
        Assert.True(await PreviewButton(page).IsDisabledAsync());
        await SelectFileAsync(page, "pedidos-limite.csv", new byte[1_048_576]);
        await ExpectErrorsAsync(page);
        Assert.False(await PreviewButton(page).IsDisabledAsync());
        Assert.Empty(api.AllCalls);

        // Rows are reported as the API returned them: line, column and Spanish code, and the COD from integer cents.
        await SelectFileAsync(page, ErrorsFileName, Utf8(FileWithErrors));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "El archivo tiene errores; corrígelo y vuelve a previsualizar. Ninguna orden se creó.");
        await ExpectTextAsync(page, "4 fila(s): 1 válida(s), 3 con error.");
        Assert.Equal(
            "Resultado por fila (la línea 1 es el encabezado)",
            (await page.Locator("main table.opsTable caption").InnerTextAsync()).Trim());
        Assert.Equal(
            new[]
            {
                $"2 | {QuoteOne} | $150.50 MXN | Válida",
                "3 | — | — | Cotización inválida (quote_id); Cobro contra entrega inválido: usa centavos enteros, sin punto, " +
                "comas ni signos, de 0 a 2000000 ($20,000.00 MXN) (cod_expected_cents)",
                $"4 | {QuoteOne} | — | Cotización repetida en el archivo (quote_id)",
                $"5 | {QuoteTwo} | — | Rechazada: la aceptación queda fuera de los límites permitidos",
            },
            await ReportRowsAsync(page));
        Assert.Equal(0, await CommitForm(page).CountAsync());
        var errorsPreview = Assert.Single(api.Calls("/preview"));
        AssertUpload(errorsPreview, FileWithErrors, OrganizationA);
        Assert.Equal(new[] { "file" }, errorsPreview.Parts.Select(part => part.Name));
        Assert.Null(errorsPreview.IdempotencyKey);

        // A file-level rejection has no rows; a new file first drops the previous report.
        await SelectFileAsync(page, "encabezado-anterior.csv", Utf8(FileWithOldHeader));
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());
        Assert.Equal(0, await Message(page).CountAsync());
        await PreviewButton(page).ClickAsync();
        await ExpectTextAsync(page, "0 fila(s): 0 válida(s), 0 con error.");
        Assert.Equal(
            new[] { "El encabezado no es el del formato CSV-001." },
            await page.Locator("main .opsFormLayout ul.notice[role=alert] li").AllInnerTextsAsync());
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());
        Assert.Equal(0, await CommitForm(page).CountAsync());

        // A clean file offers the commit with both boxes unticked; the WhatsApp box reads the AI-24 text and the note.
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        await ExpectTextAsync(page, "3 fila(s): 3 válida(s), 0 con error.");
        Assert.Equal(ValidReport, await ReportRowsAsync(page));
        var goods = GoodsBox(page);
        var consent = ConsentBox(page);
        Assert.False(await goods.IsCheckedAsync());
        Assert.False(await consent.IsCheckedAsync());
        Assert.Equal(
            ConsentNote,
            (await page.Locator($"#{await consent.GetAttributeAsync("aria-describedby")}").InnerTextAsync()).Trim());
        Assert.Equal("Confirmar lote de 3 orden(es)", (await ConfirmButton(page).InnerTextAsync()).Trim());

        // Without the prohibited-goods tick "Confirmar" sends nothing.
        await ConfirmButton(page).ClickAsync();
        Assert.True(await goods.EvaluateAsync<bool>("box => box.validity.valueMissing"));
        await Task.Delay(300);
        Assert.Empty(api.Calls("/commit"));

        await goods.CheckAsync();
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Lote confirmado: 2 creada(s), 1 con error.");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Lote confirmado", Exact = true }).WaitForAsync();
        await ExpectTextAsync(page, "2 orden(es) creada(s), 1 fila(s) con error.");
        Assert.Equal(
            new[]
            {
                $"2 | Creada | {PublicIdFor(QuoteThree)}",
                "3 | La cotización ya no está disponible (usada, expirada o de otra organización). | —",
                $"4 | Creada | {PublicIdFor(QuoteFive)}",
            },
            await ReportRowsAsync(page));
        Assert.Equal(
            new[] { $"/ops/orders/{OrderIdFor(QuoteThree)}", $"/ops/orders/{OrderIdFor(QuoteFive)}" },
            await page.Locator("main table.opsTable a").EvaluateAllAsync<string[]>(
                "links => links.map(link => link.getAttribute('href'))"));
        Assert.Equal(0, await CommitForm(page).CountAsync());

        // The commit sends exactly the previewed bytes, the previewed digest and the tick, and no consent field.
        var validPreview = api.Calls("/preview").Last();
        var commit = Assert.Single(api.Calls("/commit"));
        AssertUpload(commit, ValidFile, OrganizationA);
        Assert.Equal(validPreview.Parts[0].Data, commit.Parts[0].Data);
        Assert.Equal(new[] { "file", "content_digest", "restricted_goods_acknowledged" }, commit.Parts.Select(part => part.Name));
        Assert.Equal(Digest(ValidFile), commit.Field("content_digest"));
        Assert.Equal("true", commit.Field("restricted_goods_acknowledged"));
        Assert.Matches(UuidPattern(), commit.IdempotencyKey ?? string.Empty);

        // The local file names never left the browser and are never shown.
        await AssertLocalNamesNeverLeftAsync(page, api);

        // "Importar otro archivo" starts again from an empty form.
        await page.GetByRole(AriaRole.Button, new() { Name = "Importar otro archivo", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Lote confirmado", Exact = true })
            .WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Equal(0, await FileInput(page).EvaluateAsync<int>("input => input.files.length"));
        Assert.True(await PreviewButton(page).IsDisabledAsync());
        Assert.Equal(0, await page.GetByText("Archivo listo en memoria", new() { Exact = false }).CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        Assert.Equal(3, api.Calls("/preview").Length);
        Assert.All(api.AllCalls, call => Assert.Equal(OrganizationA, call.Organization));
        trace.Passed();
    }

    /// <summary>
    /// AI-07 csv_order_import.idempotency and recipient_notices: the batch Idempotency-Key is reused for a retry of the
    /// same digest after a 5xx; the other answer to the WhatsApp box is another batch for the server; a new file or a
    /// definitive server answer drops the key; 409 IDEMPOTENCY_CONFLICT and CONFLICT are explained.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Commit_reuses_the_batch_key_on_a_5xx_and_takes_a_new_one_for_the_other_box_a_new_file_or_a_definitive_answer()
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
        api.Register(ValidFile, ValidRows);
        api.Register(OtherValidFile, [PreviewRow.Valid(2, QuoteSix, "SENDER", 25_000)]);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders/import").AbsoluteUri);
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        Assert.Equal(GoodsText, (await page.Locator("label:has(input[name=restricted_goods_acknowledged])").InnerTextAsync()).Trim());
        Assert.Equal(ConsentText, (await page.Locator("label[for=csv-recipient-whatsapp-consent]").InnerTextAsync()).Trim());
        await GoodsBox(page).CheckAsync();
        await ConsentBox(page).CheckAsync();

        // Ticked, the box sends both consent fields; two 503 answers keep the same key and the same bytes.
        api.CommitAnswers.Enqueue(CommitAnswer.Unavailable);
        api.CommitAnswers.Enqueue(CommitAnswer.Unavailable);
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, RetryMessage);
        await ConfirmButton(page).ClickAsync();
        await WaitForAsync(() => api.Calls("/commit").Length == 2, "the retry was not sent");
        await ExpectMessageAsync(page, RetryMessage);

        // The other answer to the box is another batch: a new key, and unticked sends neither consent field.
        api.CommitAnswers.Enqueue(CommitAnswer.Unavailable);
        await ConsentBox(page).UncheckAsync();
        await ConfirmButton(page).ClickAsync();
        await WaitForAsync(() => api.Calls("/commit").Length == 3, "the unticked commit was not sent");
        await ExpectMessageAsync(page, RetryMessage);
        var commits = api.Calls("/commit");
        Assert.All(commits, call => AssertUpload(call, ValidFile, OrganizationA));
        Assert.All(commits, call => Assert.Matches(UuidPattern(), call.IdempotencyKey ?? string.Empty));
        Assert.Equal(commits[0].IdempotencyKey, commits[1].IdempotencyKey);
        Assert.Equal(commits[0].RawBodyWithoutBoundary, commits[1].RawBodyWithoutBoundary);
        Assert.Equal(
            new[]
            {
                "file", "content_digest", "restricted_goods_acknowledged", "recipient_whatsapp_consent_attested",
                "recipient_whatsapp_consent_version",
            },
            commits[0].Parts.Select(part => part.Name));
        Assert.Equal("true", commits[0].Field("recipient_whatsapp_consent_attested"));
        Assert.Equal(ConsentVersion, commits[0].Field("recipient_whatsapp_consent_version"));
        Assert.NotEqual(commits[0].IdempotencyKey, commits[2].IdempotencyKey);
        Assert.Equal(new[] { "file", "content_digest", "restricted_goods_acknowledged" }, commits[2].Parts.Select(part => part.Name));
        Assert.Equal(Digest(ValidFile), commits[2].Field("content_digest"));

        // A new file drops the report, the boxes and the key.
        await SelectFileAsync(page, OtherFileName, Utf8(OtherValidFile));
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        Assert.Equal(new[] { $"2 | {QuoteSix} | $250.00 MXN | Válida" }, await ReportRowsAsync(page));
        Assert.False(await GoodsBox(page).IsCheckedAsync());
        Assert.False(await ConsentBox(page).IsCheckedAsync());
        await GoodsBox(page).CheckAsync();
        api.CommitAnswers.Enqueue(CommitAnswer.IdempotencyConflict);
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(
            page,
            "Este lote ya se confirmó con otro contenido. Vuelve a previsualizar el archivo para iniciar un lote nuevo.");
        var otherCommit = api.Calls("/commit")[3];
        AssertUpload(otherCommit, OtherValidFile, OrganizationA);
        Assert.Equal(Digest(OtherValidFile), otherCommit.Field("content_digest"));
        Assert.DoesNotContain(otherCommit.IdempotencyKey, commits.Select(call => call.IdempotencyKey));

        // A definitive answer drops the key: each press after one is a new batch key.
        api.CommitAnswers.Enqueue(CommitAnswer.Conflict);
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(
            page,
            "El servidor rechazó el archivo (formato de carga, tamaño o contenido distinto del previsualizado). Vuelve a previsualizar.");
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Lote confirmado: 1 creada(s), 0 con error.");
        commits = api.Calls("/commit");
        Assert.Equal(6, commits.Length);
        Assert.Equal(5, commits.Select(call => call.IdempotencyKey).Distinct().Count());
        Assert.NotEqual(commits[3].IdempotencyKey, commits[4].IdempotencyKey);
        Assert.NotEqual(commits[4].IdempotencyKey, commits[5].IdempotencyKey);
        Assert.Equal(commits[3].RawBodyWithoutBoundary, commits[5].RawBodyWithoutBoundary);

        await AssertLocalNamesNeverLeftAsync(page, api);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 csv_order_import.validations: a commit 422 replaces the preview with the returned per-row report and
    /// creates nothing; a commit response whose content_digest differs from the previewed one fails closed.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Commit_422_replaces_the_report_and_an_answer_for_another_digest_fails_closed()
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
        api.Register(ValidFile, ValidRows);
        // By the commit the second row's acceptance has left the allowed window.
        api.UnprocessableRows =
        [
            PreviewRow.Valid(2, QuoteThree, "SENDER", 0),
            PreviewRow.Invalid(3, QuoteFour),
            PreviewRow.Valid(4, QuoteFive, "SENDER", 2_000_000),
        ];

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders/import").AbsoluteUri);
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        await GoodsBox(page).CheckAsync();
        api.CommitAnswers.Enqueue(CommitAnswer.Unprocessable);
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(
            page,
            "El servidor rechazó el lote porque alguna fila no pasó la prevalidación. Ninguna orden se creó.");
        await ExpectTextAsync(page, "3 fila(s): 2 válida(s), 1 con error.");
        Assert.Equal(
            new[]
            {
                $"2 | {QuoteThree} | Sin cobro | Válida",
                $"3 | {QuoteFour} | — | Rechazada: la aceptación queda fuera de los límites permitidos",
                $"4 | {QuoteFive} | $20,000.00 MXN | Válida",
            },
            await ReportRowsAsync(page));
        Assert.Equal(0, await CommitForm(page).CountAsync());
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Lote confirmado" }).CountAsync());

        // Previewed again, the batch answers for another digest: nothing of that answer is shown and the key goes.
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        Assert.Equal(ValidReport, await ReportRowsAsync(page));
        await GoodsBox(page).CheckAsync();
        api.CommitAnswers.Enqueue(CommitAnswer.DigestMismatch);
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, "El servidor rechazó los datos enviados; revísalos.");
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Lote confirmado" }).CountAsync());
        Assert.Equal(ValidReport, await ReportRowsAsync(page));
        Assert.DoesNotContain(PublicIdFor(QuoteThree), await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);

        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Lote confirmado: 3 creada(s), 0 con error.");
        var commits = api.Calls("/commit");
        Assert.Equal(3, commits.Length);
        Assert.All(commits, call => AssertUpload(call, ValidFile, OrganizationA));
        Assert.Equal(3, commits.Select(call => call.IdempotencyKey).Distinct().Count());
        Assert.Equal(2, api.Calls("/preview").Length);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// SEC-004-PANEL-SCREENS-2026-10-13, AI-07 csv_order_import.rate_limit: previewOrderCsv and commitOrderCsv share one
    /// budget and one wait. The common message counts down, announced once by the shell; "Previsualizar" and
    /// "Confirmar" are both disabled and re-enable themselves; the file, the report and the prohibited-goods tick stay
    /// and nothing is sent by itself; a commit pressed again sends the same batch Idempotency-Key and bytes.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Preview_and_commit_share_one_429_wait_that_keeps_the_file_the_report_and_the_tick()
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
        api.Register(ValidFile, ValidRows);
        var countdown = page.Locator("main").GetByRole(AriaRole.Timer);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders/import").AbsoluteUri);
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        api.PreviewTooManyRequests = 1;
        await PreviewButton(page).ClickAsync();
        await countdown.GetByText(TooManyRequestsText(3), new() { Exact = true }).WaitForAsync();
        Assert.True(await PreviewButton(page).IsDisabledAsync());
        Assert.Equal(TooManyRequestsText(3), (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.Null(await countdown.GetAttributeAsync("aria-live"));
        Assert.Equal(0, await Message(page).CountAsync());
        await ExpectTextAsync(page, $"Archivo listo en memoria ({Utf8(ValidFile).Length} bytes).");

        // At 0 the button comes back and the message goes; nothing was sent by itself.
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        await WaitForAsync(async () => !await PreviewButton(page).IsDisabledAsync(), "Previsualizar did not re-enable itself");
        await Task.Delay(1_000);
        Assert.Single(api.Calls("/preview"));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");

        // The commit spends the same budget: both buttons wait, the report and the tick stay.
        await GoodsBox(page).CheckAsync();
        api.CommitTooManyRequests = 1;
        await ConfirmButton(page).ClickAsync();
        await countdown.GetByText(TooManyRequestsText(3), new() { Exact = true }).WaitForAsync();
        Assert.True(await ConfirmButton(page).IsDisabledAsync());
        Assert.True(await PreviewButton(page).IsDisabledAsync());
        Assert.True(await GoodsBox(page).IsCheckedAsync());
        Assert.Equal(ValidReport, await ReportRowsAsync(page));
        Assert.Equal(0, await Message(page).CountAsync());
        await countdown.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        await WaitForAsync(async () => !await ConfirmButton(page).IsDisabledAsync(), "Confirmar did not re-enable itself");
        Assert.False(await PreviewButton(page).IsDisabledAsync());
        await Task.Delay(1_000);
        Assert.Single(api.Calls("/commit"));
        Assert.Equal(2, api.Calls("/preview").Length);

        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Lote confirmado: 3 creada(s), 0 con error.");
        var commits = api.Calls("/commit");
        Assert.Equal(2, commits.Length);
        Assert.Matches(UuidPattern(), commits[0].IdempotencyKey ?? string.Empty);
        Assert.Equal(commits[0].IdempotencyKey, commits[1].IdempotencyKey);
        Assert.Equal(commits[0].RawBodyWithoutBoundary, commits[1].RawBodyWithoutBoundary);

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Organization_switch_clears_the_file_the_report_and_the_batch_key()
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
        api.Register(ValidFile, ValidRows);

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders/import").AbsoluteUri);
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        await GoodsBox(page).CheckAsync();
        // A commit that met a 503 keeps its batch key for a retry...
        api.CommitAnswers.Enqueue(CommitAnswer.Unavailable);
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, RetryMessage);
        var firstKey = Assert.Single(api.Calls("/commit")).IdempotencyKey;

        // ...until the organization changes: the file, the report and the message go before B answers.
        var releaseOrganizationB = api.Hold(OrganizationB);
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationBName });
        await WaitForAsync(() => api.RoleReads(OrganizationB) > 0, "organization B was never read");
        var during = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain(QuoteThree, during, StringComparison.Ordinal);
        Assert.DoesNotContain("Prevalidación", during, StringComparison.Ordinal);
        Assert.DoesNotContain("Archivo listo en memoria", during, StringComparison.Ordinal);
        Assert.DoesNotContain(RetryMessage, during, StringComparison.Ordinal);
        Assert.Equal(0, await CommitForm(page).CountAsync());
        releaseOrganizationB.TrySetResult();

        // PLATFORM_ADMIN in B starts from an empty form and previews in B only.
        await ExpectNavigationAsync(page, "PLATFORM_ADMIN");
        await UploadForm(page).WaitForAsync();
        Assert.Equal(0, await FileInput(page).EvaluateAsync<int>("input => input.files.length"));
        Assert.True(await PreviewButton(page).IsDisabledAsync());
        Assert.Equal(0, await page.Locator("main table.opsTable").CountAsync());
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        Assert.Equal("Confirmar lote de 3 orden(es)", (await ConfirmButton(page).InnerTextAsync()).Trim());
        Assert.False(await GoodsBox(page).IsCheckedAsync());
        AssertUpload(api.Calls("/preview").Last(), ValidFile, OrganizationB);

        // Back in A, the same file is a new batch with a new key.
        await page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = OrganizationAName });
        await ExpectNavigationAsync(page, "DISPATCHER");
        await page.Locator("main table.opsTable").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.Equal(0, await FileInput(page).EvaluateAsync<int>("input => input.files.length"));
        await SelectFileAsync(page, ValidFileName, Utf8(ValidFile));
        await PreviewButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Prevalidación completa: ninguna orden se ha creado todavía.");
        await GoodsBox(page).CheckAsync();
        await ConfirmButton(page).ClickAsync();
        await ExpectMessageAsync(page, "Lote confirmado: 3 creada(s), 0 con error.");
        var commits = api.Calls("/commit");
        Assert.Equal(2, commits.Length);
        Assert.All(commits, call => AssertUpload(call, ValidFile, OrganizationA));
        Assert.Equal(commits[0].RawBodyWithoutBoundary, commits[1].RawBodyWithoutBoundary);
        Assert.NotEqual(firstKey, commits[1].IdempotencyKey);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationB && call.Path.EndsWith("/commit", StringComparison.Ordinal));

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>AI-07 /ops/orders/import roles: VIEWER, FINANCE and DRIVER never reach the form and never send a file.</summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("VIEWER")]
    [InlineData("FINANCE")]
    [InlineData("DRIVER")]
    public async Task Roles_without_CSV_import_are_not_offered_it_and_never_send_a_file(string role)
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

        await page.GotoAsync(new Uri(server.BaseAddress, "/ops/orders/import").AbsoluteUri);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
        await ExpectTextAsync(page, "Tu rol en la organización activa no importa órdenes.");
        await ExpectNavigationAsync(page, role);
        await Task.Delay(500);
        Assert.Empty(api.AllCalls);
        Assert.Equal(0, await FileInput(page).CountAsync());
        Assert.Equal(0, await UploadForm(page).CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAi05Calls(calls, server.BaseAddress);
        trace.Passed();
    }

    private static string TooManyRequestsText(int seconds) => seconds == 1
        ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
        : $"Demasiadas solicitudes. Espera {seconds} segundos e intenta de nuevo.";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The AI-05 content_digest: Base64url SHA-256 of the file without padding.</summary>
    private static string Digest(string text) => Digest(Utf8(text));

    private static string Digest(byte[] bytes) =>
        Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string OrderIdFor(string quoteId) => $"0de{quoteId[3..]}";

    private static string PublicIdFor(string quoteId) => $"ORD_ImportacionPlaywright{quoteId[^2..]}";

    private static ILocator UploadForm(IPage page) => page.Locator("main form:has(legend:text-is('Archivo'))");

    private static ILocator FileInput(IPage page) => page.Locator("main input[type=file][name=file]");

    private static ILocator PreviewButton(IPage page) => UploadForm(page).Locator("button[type=submit]");

    private static ILocator CommitForm(IPage page) => page.Locator("main form:has(input[name=restricted_goods_acknowledged])");

    private static ILocator ConfirmButton(IPage page) => CommitForm(page).Locator("button[type=submit]");

    private static ILocator GoodsBox(IPage page) =>
        page.GetByRole(AriaRole.Checkbox, new() { Name = GoodsText, Exact = true });

    private static ILocator ConsentBox(IPage page) =>
        page.GetByRole(AriaRole.Checkbox, new() { Name = ConsentText, Exact = true });

    /// <summary>The screen's last server outcome (Feedback), not the shell's announcer or the report.</summary>
    private static ILocator Message(IPage page) => page.Locator("main div.page > p.notice[role=status]");

    private static async Task SelectFileAsync(IPage page, string name, byte[] bytes)
    {
        await FileInput(page).SetInputFilesAsync(new FilePayload { Name = name, MimeType = "text/csv", Buffer = bytes });
        await ExpectTextAsync(page, $"Archivo listo en memoria ({bytes.Length} bytes).");
    }

    /// <summary>Each report row as "cell | cell | …", as the table shows it.</summary>
    private static async Task<string[]> ReportRowsAsync(IPage page) =>
        await page.Locator("main table.opsTable tbody tr").EvaluateAllAsync<string[]>(
            "rows => rows.map(row => [...row.cells].map(cell => cell.innerText.trim()).join(' | '))");

    /// <summary>
    /// AI-07 csv_order_import.upload: one multipart part <c>file</c>, first, under the fixed neutral name, typed
    /// text/csv, carrying exactly the selected bytes, for the organization the screen is in.
    /// </summary>
    private static void AssertUpload(CsvCall call, string file, string organization)
    {
        Assert.Equal(organization, call.Organization);
        Assert.StartsWith("multipart/form-data; boundary=", call.ContentType ?? string.Empty, StringComparison.Ordinal);
        var part = call.Parts[0];
        Assert.Equal("file", part.Name);
        Assert.Equal("orders.csv", part.FileName);
        Assert.Equal("text/csv", part.ContentType);
        Assert.Equal(Utf8(file), part.Data);
        Assert.Single(call.Parts, candidate => candidate.Name == "file");
        Assert.All(call.Parts.Skip(1), field => Assert.Null(field.FileName));
    }

    private static async Task AssertLocalNamesNeverLeftAsync(IPage page, CsvApi api)
    {
        var shown = await page.Locator("body").InnerTextAsync();
        foreach (var name in new[] { ErrorsFileName, ValidFileName, OtherFileName, "pedidos-", "clientes-" })
        {
            Assert.DoesNotContain(name, shown, StringComparison.Ordinal);
            Assert.All(api.AllCalls, call => Assert.DoesNotContain(name, call.RawBodyWithoutBoundary, StringComparison.Ordinal));
        }
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
        Message(page).GetByText(message, new() { Exact = true }).WaitForAsync();

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
        if (expected.Contains("/ops/orders/import"))
        {
            Assert.Equal(
                "page",
                await page.Locator("nav.appNav a[href=\"/ops/orders/import\"]").GetAttributeAsync("aria-current"));
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
    /// switch, and fulfils /me/organization-contexts and the CSV-001 operations. Playwright runs the last matching route
    /// first, so the catch-all is registered before the specific ones.
    /// </summary>
    private static async Task<CsvApi> InstallAsync(
        IPage page,
        params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-csv-import-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        await page.RouteAsync("**/api/v1/**", route => ProblemAsync(route, 404));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        var api = new CsvApi(organizations.Select((organization, index) => new
        {
            organization_id = organization.Id,
            display_name = organization.Name,
            role = organization.Role,
            is_default = index == 0,
        }).ToArray());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", api.HandleRoleReadAsync);
        await page.RouteAsync("**/api/v1/orders/csv/**", api.HandleAsync);
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

    /// <summary>429 with Retry-After: 3; the test API is another origin, so it exposes the header as a cross-origin API must.</summary>
    private static Task TooManyRequestsAsync(IRoute route) =>
        route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 429,
            ContentType = "application/problem+json",
            Headers = new Dictionary<string, string>
            {
                ["Retry-After"] = "3",
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

    /// <summary>The multipart/form-data parts of a request body, in order (RFC 7578).</summary>
    private static FormPart[] ParseMultipart(string? contentType, byte[]? body)
    {
        if (contentType is null || body is null) return [];
        var boundary = MultipartBoundary().Match(contentType);
        if (!boundary.Success) return [];
        // Latin-1 maps every byte to one char, so the file's bytes survive the split unchanged.
        var text = Encoding.Latin1.GetString(body);
        var parts = new List<FormPart>();
        foreach (var section in text.Split($"--{boundary.Groups["boundary"].Value}").Skip(1))
        {
            if (section.StartsWith("--", StringComparison.Ordinal)) break;
            var headerEnd = section.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headers = section[2..headerEnd].Split("\r\n");
            var disposition = headers.Single(header => header.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase));
            var fileName = Regex.Match(disposition, "; filename=\"(?<value>[^\"]*)\"");
            var type = headers.SingleOrDefault(header => header.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
            parts.Add(new FormPart(
                Regex.Match(disposition, "; name=\"(?<value>[^\"]*)\"").Groups["value"].Value,
                fileName.Success ? fileName.Groups["value"].Value : null,
                type?["Content-Type:".Length..].Trim(),
                Encoding.Latin1.GetBytes(section[(headerEnd + 4)..^2])));
        }

        return [.. parts];
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    [GeneratedRegex("^multipart/form-data; boundary=(?<boundary>[^;]+)$")]
    private static partial Regex MultipartBoundary();

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    private sealed record FormPart(string Name, string? FileName, string? ContentType, byte[] Data);

    /// <summary>One CSV-001 request: the multipart parts and the body with its random boundary removed.</summary>
    private sealed record CsvCall(
        string Organization,
        string Path,
        string? IdempotencyKey,
        string? ContentType,
        FormPart[] Parts,
        string RawBodyWithoutBoundary)
    {
        /// <summary>The text of the one field with this name; null when it is absent or repeated.</summary>
        internal string? Field(string name)
        {
            var fields = Parts.Where(part => part.Name == name).ToArray();
            return fields.Length == 1 ? Encoding.UTF8.GetString(fields[0].Data) : null;
        }
    }

    /// <summary>An AI-05 CsvImportRowPreview; cod_expected_cents is present exactly on a valid row.</summary>
    private sealed record PreviewRow(
        int RowNumber,
        string? QuoteId,
        string? PayerType,
        bool IsValid,
        (string Column, string Code)[] Errors,
        long? CodExpectedCents)
    {
        internal static PreviewRow Valid(int rowNumber, string quoteId, string payerType, long codExpectedCents) =>
            new(rowNumber, quoteId, payerType, true, [], codExpectedCents);

        /// <summary>The server reports no payer for an invalid row; no error at all means the acceptance limits refused it.</summary>
        internal static PreviewRow Invalid(int rowNumber, string? quoteId, params (string Column, string Code)[] errors) =>
            new(rowNumber, quoteId, null, false, errors, null);

        internal Dictionary<string, object?> Json()
        {
            var row = new Dictionary<string, object?>
            {
                ["row_number"] = RowNumber,
                ["quote_id"] = QuoteId,
                ["payer_type"] = PayerType,
                ["valid"] = IsValid,
                ["errors"] = Errors.Select(error => new { column = error.Column, code = error.Code }).ToArray(),
            };
            if (IsValid) row["cod_expected_cents"] = CodExpectedCents;
            return row;
        }
    }

    private enum CommitAnswer
    {
        Unavailable,
        Unprocessable,
        DigestMismatch,
        IdempotencyConflict,
        Conflict,
    }

    /// <summary>
    /// previewOrderCsv and commitOrderCsv of AI-05 over the registered files: the preview reports a registered file by
    /// its digest, the commit validates the form fields as the API does and replays a batch key, and every call is
    /// recorded in order with its parsed multipart parts.
    /// </summary>
    private sealed class CsvApi(object[] contexts)
    {
        private readonly object _gate = new();
        private readonly List<CsvCall> _calls = [];
        private readonly Dictionary<string, (string[] FileErrors, PreviewRow[] Rows)> _reports = [];
        private readonly Dictionary<string, (string Fingerprint, object Body)> _batches = [];
        private readonly ConcurrentDictionary<string, int> _roleReads = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();

        /// <summary>Scripted answers of the next commits, before any validation.</summary>
        internal ConcurrentQueue<CommitAnswer> CommitAnswers { get; } = new();

        /// <summary>Quotes whose row a successful commit reports as FAILED QUOTE_UNAVAILABLE.</summary>
        internal HashSet<string> UnavailableQuotes { get; } = [];

        /// <summary>The per-row report a 422 commit answer returns.</summary>
        internal PreviewRow[] UnprocessableRows { get; set; } = [];

        internal int PreviewTooManyRequests { get; set; }

        internal int CommitTooManyRequests { get; set; }

        internal IReadOnlyList<CsvCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal void Register(string file, PreviewRow[] rows, params string[] fileErrors)
        {
            lock (_gate) _reports[Digest(file)] = (fileErrors, rows);
        }

        internal CsvCall[] Calls(string operation) =>
            AllCalls.Where(call => call.Path == $"/api/v1/orders/csv{operation}").ToArray();

        internal int RoleReads(string organization) => _roleReads.GetValueOrDefault(organization);

        /// <summary>Holds the organization's answers (its role read included) until the returned source is completed.</summary>
        internal TaskCompletionSource Hold(string organization) =>
            _holds.GetOrAdd(organization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal async Task HandleRoleReadAsync(IRoute route)
        {
            var organization = Organization(await route.Request.AllHeadersAsync());
            _roleReads.AddOrUpdate(organization, 1, (_, count) => count + 1);
            if (_holds.TryGetValue(organization, out var hold)) await hold.Task;
            await FulfillAsync(route, contexts);
        }

        internal async Task HandleAsync(IRoute route)
        {
            var request = route.Request;
            var headers = await request.AllHeadersAsync();
            var contentType = headers.GetValueOrDefault("content-type");
            var body = request.PostDataBuffer;
            var boundary = contentType is null ? null : MultipartBoundary().Match(contentType).Groups["boundary"].Value;
            var raw = body is null ? string.Empty : Encoding.Latin1.GetString(body);
            var call = new CsvCall(
                Organization(headers),
                new Uri(request.Url).AbsolutePath,
                headers.GetValueOrDefault("idempotency-key"),
                contentType,
                ParseMultipart(contentType, body),
                string.IsNullOrEmpty(boundary) ? raw : raw.Replace(boundary, "<boundary>", StringComparison.Ordinal));
            lock (_gate) _calls.Add(call);
            if (_holds.TryGetValue(call.Organization, out var hold)) await hold.Task;

            if (request.Method != "POST")
            {
                await ProblemAsync(route, 404);
                return;
            }

            if (call.Path == "/api/v1/orders/csv/preview") await PreviewAsync(route, call);
            else if (call.Path == "/api/v1/orders/csv/commit") await CommitAsync(route, call);
            else await ProblemAsync(route, 404);
        }

        private static string Organization(IReadOnlyDictionary<string, string> headers) =>
            headers.GetValueOrDefault("x-organization-id") ?? string.Empty;

        private async Task PreviewAsync(IRoute route, CsvCall call)
        {
            if (PreviewTooManyRequests > 0)
            {
                PreviewTooManyRequests -= 1;
                await TooManyRequestsAsync(route);
                return;
            }

            var report = call.Parts is [{ Name: "file" } file] ? Report(file.Data) : null;
            if (report is null)
            {
                await ProblemAsync(route, 409, "CONFLICT");
                return;
            }

            await FulfillAsync(route, PreviewBody(Digest(call.Parts[0].Data), report.Value.FileErrors, report.Value.Rows));
        }

        private async Task CommitAsync(IRoute route, CsvCall call)
        {
            if (CommitTooManyRequests > 0)
            {
                CommitTooManyRequests -= 1;
                await TooManyRequestsAsync(route);
                return;
            }

            if (CommitAnswers.TryDequeue(out var answer))
            {
                switch (answer)
                {
                    case CommitAnswer.Unavailable:
                        await ProblemAsync(route, 503);
                        return;
                    case CommitAnswer.IdempotencyConflict:
                        await ProblemAsync(route, 409, "IDEMPOTENCY_CONFLICT");
                        return;
                    case CommitAnswer.Conflict:
                        await ProblemAsync(route, 409, "CONFLICT");
                        return;
                    case CommitAnswer.Unprocessable:
                        await FulfillAsync(route, PreviewBody(call.Field("content_digest")!, [], UnprocessableRows), 422);
                        return;
                    case CommitAnswer.DigestMismatch:
                        await FulfillAsync(route, CommitBody(Digest("another batch"), Report(call.Parts[0].Data)!.Value.Rows));
                        return;
                }
            }

            // AI-05 CsvImportCommitRequest: the file, its previewed digest, exactly one tick and the consent pair or none.
            var names = call.Parts.Select(part => part.Name).ToArray();
            var consentValid = names.Count(name => name.StartsWith("recipient_whatsapp_consent_", StringComparison.Ordinal)) switch
            {
                0 => true,
                2 => call.Field("recipient_whatsapp_consent_attested") == "true" &&
                     call.Field("recipient_whatsapp_consent_version") == ConsentVersion,
                _ => false,
            };
            var report = call.Parts.FirstOrDefault()?.Name == "file" ? Report(call.Parts[0].Data) : null;
            if (call.IdempotencyKey is null || report is null || names.Count(name => name == "file") != 1 ||
                call.Field("content_digest") != Digest(call.Parts[0].Data) ||
                call.Field("restricted_goods_acknowledged") != "true" || !consentValid)
            {
                await ProblemAsync(route, 409, "CONFLICT");
                return;
            }

            var digest = call.Field("content_digest")!;
            var fingerprint = $"{digest}|{names.Length}";
            object? body;
            lock (_gate)
            {
                if (_batches.TryGetValue(call.IdempotencyKey, out var batch))
                {
                    body = batch.Fingerprint == fingerprint ? batch.Body : null;
                }
                else
                {
                    body = CommitBody(digest, report.Value.Rows);
                    _batches[call.IdempotencyKey] = (fingerprint, body);
                }
            }

            if (body is null) await ProblemAsync(route, 409, "IDEMPOTENCY_CONFLICT");
            else await FulfillAsync(route, body);
        }

        private (string[] FileErrors, PreviewRow[] Rows)? Report(byte[] file)
        {
            lock (_gate) return _reports.TryGetValue(Digest(file), out var report) ? report : null;
        }

        private static object PreviewBody(string digest, string[] fileErrors, PreviewRow[] rows) => new
        {
            content_digest = digest,
            total_rows = rows.Length,
            valid_rows = rows.Count(row => row.IsValid),
            invalid_rows = rows.Count(row => !row.IsValid),
            file_errors = fileErrors,
            rows = rows.Select(row => row.Json()).ToArray(),
        };

        private object CommitBody(string digest, PreviewRow[] rows)
        {
            var outcomes = rows.Select(row => UnavailableQuotes.Contains(row.QuoteId!)
                ? new
                {
                    row_number = row.RowNumber,
                    quote_id = row.QuoteId!,
                    status = "FAILED",
                    order_id = (string?)null,
                    public_id = (string?)null,
                    error_code = (string?)"QUOTE_UNAVAILABLE",
                }
                : new
                {
                    row_number = row.RowNumber,
                    quote_id = row.QuoteId!,
                    status = "CREATED",
                    order_id = (string?)OrderIdFor(row.QuoteId!),
                    public_id = (string?)PublicIdFor(row.QuoteId!),
                    error_code = (string?)null,
                }).ToArray();
            return new
            {
                content_digest = digest,
                total_rows = outcomes.Length,
                created_rows = outcomes.Count(outcome => outcome.status == "CREATED"),
                failed_rows = outcomes.Count(outcome => outcome.status == "FAILED"),
                rows = outcomes,
            };
        }
    }
}
