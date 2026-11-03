using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (AI-07 driver_account documents; Y4, Y6, Y8; docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md)
/// in a real browser against the production Next build, on a phone, with the API fulfilled in the browser in AI-05
/// shapes: "Cuenta > Documentos" lists the driver's own documents read-only - one line per document its vehicle
/// requires with its label, status (Aprobado, Rechazado with the reason, Revocado, Falta), "Vence el {fecha}" or
/// "Revisión el {fecha}" and, from 30 days before, «Vence en N días» / «Revisión en N días» or «Vencido», in Culiacán
/// time whatever the device zone. It is read when "Cuenta" opens and when the driver taps "Actualizar documentos",
/// never by itself (Y8: no message); nothing is shown while documents are off or the account is not a driver there;
/// offline it asks for a connection and sends nothing. No field, file, number, key, hash or document id reaches the
/// screen, and the answer is never cached.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class DriverDocumentsPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationId = "11111111-1111-1111-1111-111111111111";
    private const string DocumentsRoute = "**/api/v1/driver/me/documents";

    /// <summary>15:00 on 2 November 2026 in Culiacán (UTC-7); already 3 November in Tokyo, the device's zone.</summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 2, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Cuenta_lists_the_drivers_own_documents_and_reads_them_only_on_open_and_refresh()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Documents.Enqueue(Answer(
            Slot("IDENTITY", Document("IDENTITY", "VALID", validThrough: "2026-11-01")),
            Slot("DRIVER_LICENSE", Document("DRIVER_LICENSE", "VALID", validThrough: "2026-11-20", basis: "PERMANENT_LICENSE_REVIEW")),
            Slot("VEHICLE_CARD", Document("VEHICLE_CARD", "REJECTED", rejection: "ILLEGIBLE")),
            Slot("INSURANCE", null)));

        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await page.GetByText("No tienes paradas activas.", new() { Exact = true }).WaitForAsync();
        // Nothing is read before "Cuenta" opens.
        await Task.Delay(500);
        Assert.Equal(0, phone.DocumentReads);

        var section = await OpenAccountAsync(page);
        await section.GetByText(
                "Despacho revisa tus documentos en persona. Lleva el original cuando te lo pidan.",
                new() { Exact = true })
            .WaitForAsync();
        Assert.Equal(
            new[]
            {
                "Identificación oficial Aprobado Vence el 1 nov 2026 Vencido",
                "Licencia de conducir Aprobado Revisión el 20 nov 2026 Revisión en 18 días",
                "Tarjeta de circulación Rechazado: Ilegible",
                "Seguro del vehículo Falta",
            },
            await LinesAsync(section));
        Assert.Equal(1, phone.DocumentReads);
        // Read-only: one button, no field, no file, no photo (Y3).
        Assert.Equal(new[] { "Actualizar documentos" }, await section.GetByRole(AriaRole.Button).AllInnerTextsAsync());
        Assert.Equal(0, await section.Locator("input, select, textarea, form, img, video, canvas").CountAsync());
        var refresh = await section.GetByRole(AriaRole.Button, new() { Name = "Actualizar documentos", Exact = true }).BoundingBoxAsync();
        Assert.NotNull(refresh);
        Assert.True(refresh.Height >= 44, $"Actualizar documentos is {refresh.Height}px high");
        Assert.False(await DriverStopsPwaPlaywrightTests.HasHorizontalOverflowAsync(page));

        // Dispatch renews and revokes in the panel: the app learns it only when the driver refreshes (Y8: no message).
        phone.Documents.Enqueue(Answer(
            Slot("IDENTITY", Document("IDENTITY", "VALID", validThrough: "2027-06-30")),
            Slot("DRIVER_LICENSE", Document("DRIVER_LICENSE", "VALID", validThrough: "2026-11-20", basis: "PERMANENT_LICENSE_REVIEW")),
            Slot("VEHICLE_CARD", Document("VEHICLE_CARD", "VALID", validThrough: "2027-01-31")),
            Slot("INSURANCE", Document("INSURANCE", "REVOKED", validThrough: "2027-06-30", revoked: true))));
        await Task.Delay(1_000);
        Assert.Equal(1, phone.DocumentReads);
        await section.GetByRole(AriaRole.Button, new() { Name = "Actualizar documentos", Exact = true }).ClickAsync();
        await section.GetByText("Identificación oficial", new() { Exact = true }).WaitForAsync();
        await WaitForLinesAsync(section,
            "Identificación oficial Aprobado Vence el 30 jun 2027",
            "Licencia de conducir Aprobado Revisión el 20 nov 2026 Revisión en 18 días",
            "Tarjeta de circulación Aprobado Vence el 31 ene 2027",
            "Seguro del vehículo Revocado Vence el 30 jun 2027");
        Assert.Equal(2, phone.DocumentReads);

        // Closing and opening "Cuenta" reads again.
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        await section.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        phone.Documents.Enqueue(Answer(Slot("IDENTITY", null)));
        section = await OpenAccountAsync(page);
        await WaitForLinesAsync(section, "Identificación oficial Falta");
        Assert.Equal(3, phone.DocumentReads);

        await phone.FinishAsync();
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Nothing_is_shown_while_documents_are_off_or_the_account_is_not_a_driver_there()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.Documents.Enqueue(new JsonObject { ["documents_enabled"] = false, ["documents"] = new JsonArray() });
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        await page.GetByText("Las llamadas con destinatarios no están activas en tu organización.", new() { Exact = true }).WaitForAsync();
        await WaitForAsync(() => phone.DocumentReads == 1, "the documents were never read");
        await Task.Delay(500);
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Documentos", Exact = true }).CountAsync());
        Assert.Equal(0, await page.GetByText("Consultando tus documentos…").CountAsync());

        // A 403 (no driver profile there) shows nothing either.
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        phone.DocumentFailures.Enqueue(403);
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        await WaitForAsync(() => phone.DocumentReads == 2, "the documents were not read again");
        await Task.Delay(500);
        Assert.Equal(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Documentos", Exact = true }).CountAsync());
        await phone.FinishAsync();
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task A_failed_read_says_so_and_offline_nothing_is_sent()
    {
        await using var phone = await StartAsync();
        var page = phone.Page;
        phone.DocumentFailures.Enqueue(503);
        await page.GotoAsync(new Uri(server.BaseAddress, "/driver/stops").AbsoluteUri);
        var section = await OpenAccountAsync(page);
        await section.GetByText("No pudimos consultar tus documentos. Intenta más tarde.", new() { Exact = true }).WaitForAsync();
        Assert.Empty(await LinesAsync(section));

        phone.Documents.Enqueue(Answer(Slot("IDENTITY", Document("IDENTITY", "VALID", validThrough: "2026-12-02"))));
        await section.GetByRole(AriaRole.Button, new() { Name = "Actualizar documentos", Exact = true }).ClickAsync();
        await WaitForLinesAsync(section, "Identificación oficial Aprobado Vence el 2 dic 2026 Vence en 30 días");
        Assert.Equal(2, phone.DocumentReads);

        // Offline: the list already shown stays, the refresh waits for a connection and nothing is sent.
        await phone.Context.SetOfflineAsync(true);
        await section.GetByText("Necesitas conexión a internet para consultar tus documentos.", new() { Exact = true }).WaitForAsync();
        Assert.True(await section.GetByRole(AriaRole.Button, new() { Name = "Actualizar documentos", Exact = true }).IsDisabledAsync());
        await WaitForLinesAsync(section, "Identificación oficial Aprobado Vence el 2 dic 2026 Vence en 30 días");
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        section = page.Locator("section[aria-labelledby=driver-documents-heading]");
        await section.GetByText("Necesitas conexión a internet para consultar tus documentos.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(2, phone.DocumentReads);
        await phone.Context.SetOfflineAsync(false);
        await phone.FinishAsync();
    }

    // ---------- helpers ----------

    private async Task<PhoneSession> StartAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            TimezoneId = "Asia/Tokyo",
            Locale = "en-US",
        });
        var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.DriverStopsJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.Clock.SetFixedTimeAsync(FixedNow.UtcDateTime);
        var session = new PhoneSession(playwright, browser, context, trace, page);
        page.Console += (_, message) =>
        {
            // The browser's own resource errors and the realtime client's, whose hub is blocked here, are not the list's.
            if (message.Type == "error" &&
                !message.Text.StartsWith("Failed to load resource", StringComparison.Ordinal) &&
                !message.Text.Contains("Failed to complete negotiation", StringComparison.Ordinal) &&
                !message.Text.Contains("Failed to start the connection", StringComparison.Ordinal))
            {
                session.Console.Enqueue(message.Text);
            }
        };
        page.PageError += (_, error) => session.Console.Enqueue($"page:{error}");

        // Registered first so the routes below take precedence: any other call to the test origin is blocked.
        await page.RouteAsync($"{DriverStopsNextServerFixture.TestApiOrigin}/**", route => route.AbortAsync("blockedbyclient"));
        await page.RouteAsync("**/hubs/driver/negotiate**", route => route.AbortAsync());
        await DriverOrganizationRoles.RouteAsync(page);
        await page.RouteAsync(DriverStopsPwaPlaywrightTests.StopsRoute, route => route.FulfillAsync(Json(
            DriverStopsPwaPlaywrightTests.StopsBody(route.Request, []))));
        await page.RouteAsync("**/api/v1/driver/me/phone", route => route.FulfillAsync(Json(JsonSerializer.Serialize(new
        {
            voice_calls_enabled = false,
            registered = false,
            consent_version = (string?)null,
            consented_at = (string?)null,
        }))));
        await page.RouteAsync(DocumentsRoute, session.HandleDocumentsAsync);
        var configuration = JsonSerializer.Serialize(new
        {
            organizationId = OrganizationId,
            cacheNamespace = "drv-doc-001-driver-documents",
            token = "synthetic-driver-token",
        });
        await page.AddInitScriptAsync(
            $$"""
            (() => {
              const value = {{configuration}};
              window.__paquetenviaDriverSession = {
                organizationId: value.organizationId,
                cacheNamespace: value.cacheNamespace,
                getAccessToken: () => value.token,
              };
            })();
            """);
        return session;
    }

    private static async Task<ILocator> OpenAccountAsync(IPage page)
    {
        await page.Locator("summary", new() { HasText = "Cuenta" }).ClickAsync();
        var section = page.Locator("section[aria-labelledby=driver-documents-heading]");
        await section.GetByRole(AriaRole.Heading, new() { Name = "Documentos", Exact = true }).WaitForAsync();
        return section;
    }

    private static async Task<string[]> LinesAsync(ILocator section) =>
        await section.Locator("li").EvaluateAllAsync<string[]>(
            "items => items.map(item => item.innerText.replace(/\\s+/g, ' ').trim())");

    private static async Task WaitForLinesAsync(ILocator section, params string[] expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string[] actual = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await LinesAsync(section);
            if (actual.SequenceEqual(expected)) return;
            await Task.Delay(50);
        }

        Assert.Fail($"The documents did not match: {string.Join(" | ", actual)}");
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

    private static RouteFulfillOptions Json(string body, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json; charset=utf-8",
        Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
        Body = body,
    };

    private static JsonObject Answer(params JsonObject[] slots) => new()
    {
        ["documents_enabled"] = true,
        ["documents"] = new JsonArray(slots.Select(slot => (JsonNode)slot).ToArray()),
    };

    private static JsonObject Slot(string type, JsonObject? document) => new()
    {
        ["document_type"] = type,
        ["document"] = document,
    };

    private static int _documents;

    /// <summary>An AI-05 DriverDocument; the last valid day is local to Culiacán and expires_at is 00:00 of the next one.</summary>
    private static JsonObject Document(
        string type,
        string status,
        string? validThrough = null,
        string? basis = "PRINTED_EXPIRY",
        string? rejection = null,
        bool revoked = false) => new()
    {
        ["id"] = $"d0c00000-0000-4000-8000-{Interlocked.Increment(ref _documents):x12}",
        ["document_type"] = type,
        ["status"] = status,
        ["rejection_code"] = rejection,
        ["expires_at"] = validThrough is null
            ? null
            : DateOnly.ParseExact(validThrough, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1)
                .ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture),
        ["valid_through"] = validThrough,
        ["expiry_basis"] = validThrough is null ? null : basis,
        ["recorded_at"] = "2026-10-01T18:00:00+00:00",
        ["revoked_at"] = revoked ? "2026-11-02T18:00:00+00:00" : null,
    };

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();

    private sealed class PhoneSession(
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        PlaywrightFailureTrace trace,
        IPage page) : IAsyncDisposable
    {
        private int _documentReads;

        internal IPage Page => page;

        internal IBrowserContext Context => context;

        internal ConcurrentQueue<string> Console { get; } = new();

        /// <summary>The next answers of listMyDriverDocuments; the last one is repeated.</summary>
        internal ConcurrentQueue<JsonObject> Documents { get; } = new();

        internal ConcurrentQueue<int> DocumentFailures { get; } = new();

        internal int DocumentReads => Volatile.Read(ref _documentReads);

        private JsonObject? _last;

        internal async Task HandleDocumentsAsync(IRoute route)
        {
            var request = route.Request;
            if (request.Method != "GET" || new Uri(request.Url).Query.Length > 0 || request.PostData is not null)
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 405 });
                return;
            }

            Assert.Equal(OrganizationId, request.Headers["x-organization-id"]);
            Interlocked.Increment(ref _documentReads);
            if (DocumentFailures.TryDequeue(out var status))
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = status,
                    ContentType = "application/problem+json",
                    Body = JsonSerializer.Serialize(new { type = "about:blank", title = "Problem.", status }),
                });
                return;
            }

            if (Documents.TryDequeue(out var next)) _last = next;
            await route.FulfillAsync(Json((_last ?? new JsonObject { ["documents_enabled"] = false, ["documents"] = new JsonArray() }).ToJsonString()));
        }

        /// <summary>No document id on screen or in the markup, the answer never cached, no error logged.</summary>
        internal async Task FinishAsync()
        {
            Assert.DoesNotMatch(UuidPattern(), await page.Locator("body").InnerTextAsync());
            Assert.DoesNotMatch(
                new Regex("d0c00000-"),
                await page.Locator("body").EvaluateAsync<string>("body => body.outerHTML"));
            var cached = await page.EvaluateAsync<string[]>(
                """
                async () => {
                  const found = [];
                  for (const name of await caches.keys())
                    for (const request of await (await caches.open(name)).keys())
                      if (new URL(request.url).pathname.includes("/documents")) found.push(request.url);
                  for (const storage of [localStorage, sessionStorage])
                    for (let index = 0; index < storage.length; index++) {
                      const value = storage.getItem(storage.key(index)) ?? "";
                      if (/IDENTITY|DRIVER_LICENSE|VEHICLE_CARD|INSURANCE/.test(value)) found.push(storage.key(index));
                    }
                  return found;
                }
                """);
            Assert.Empty(cached);
            Assert.Empty(Console);
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
