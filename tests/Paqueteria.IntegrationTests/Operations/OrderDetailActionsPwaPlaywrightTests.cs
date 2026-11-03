using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// UI-001 "Playwright per role" and "tenant switch clears prior state" for the actions of AI-07 <c>/ops/orders/:id</c>
/// in a real browser against the production Next build: "Siguiente paso" (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05,
/// UI-NEXT-STEP-RETURNS-CLAIMS-2026-10-09 and the rule codes of ORD-002-GUARD-CODES-2026-10-05, among them the close
/// AI-09 "Cierre financiero" blocks while the cash is not reconciled), "Intento fallido" with the dispatcher's decision
/// (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11), "Asignar repartidor" on the detail
/// (UI-PHASE2-DRIVER-PICKER-2026-10-05), "Enlace de seguimiento" (TRK-002-AUTO-LINK, TRK-002-NO-REVOCATION) and
/// "Avisos al destinatario" with "Detener avisos" (NTF-RECIPIENT-NOTICES-2026-10-20,
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30), and the screen's pause during the dashboard's 429 wait
/// (SEC-004-PANEL-SCREENS-2026-10-13). The release, the assignment and "Ver enlace" of the main journey run against
/// the real API in <c>MainJourneyEndToEndTests</c>; here the API is fulfilled in the browser with the AI-05 shapes of
/// getOrder (OrderDetail with allowed_transitions), transitionOrder, listIncidents, listAssignableDrivers, assignDriver,
/// issueTrackingLink, listOrderRecipientNotices, stopOrderRecipientNotices and /me/organization-contexts, and the order's
/// row of the operations dashboard read model (OBS-001, ADR-OBS-001); the OperationsHub is unreachable. RLS is the
/// uniform 404 for an order of another organization, an operator organization receives no allowed transition and no
/// link or notice, and every role outside an operation receives 403. Every write carries the exact AI-05 body and an
/// Idempotency-Key reused only for a retry of the same payload; the order is read again after each answer; nothing is
/// kept in browser storage or logged; no internal id or coordinate is shown; every amount typed becomes integer cents
/// and every time reads in America/Mazatlan whatever the device zone.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class OrderDetailActionsPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrganizationB = "22222222-2222-2222-2222-222222222222";
    private const string OrganizationAName = "Logística Sintética";
    private const string OrganizationBName = "Mensajería Sintética Norte";

    private const string ZoneCentro = "5a000000-0000-4000-8000-000000000001";
    private const string ClientAccount = "c7000000-0000-4000-8000-000000000001";
    private const string DriverMoto = "d1000000-0000-4000-8000-000000000001";
    private const string DriverCar = "d1000000-0000-4000-8000-000000000002";
    private const string DriverVan = "d1000000-0000-4000-8000-000000000003";
    private const string AssignmentId = "a6000000-0000-4000-8000-000000000001";
    private const string TokenId = "7e000000-0000-4000-8000-000000000001";

    /// <summary>
    /// A 43-character Base64URL token, as issueTrackingLink returns it: a repeated synthetic word, so it is never
    /// mistaken for a credential.
    /// </summary>
    private const string Token = "sintetico-sintetico-sintetico-sintetico-sin";

    private const string TrackingUrl = "https://rastreo.example.test/track/" + Token;

    /// <summary>Generated at 05:20 UTC on 3 November: 22:20 on 2 November in Mazatlán, 14:20 on 3 November in Tokyo.</summary>
    private const string GeneratedAt = "2026-11-03T05:20:00Z";

    private const string GeneratedAtInMazatlan = "2 nov 2026, 22:20";

    private static readonly string[] OrderIds =
    [
        "0d000000-0000-4000-8000-000000000001", "0d000000-0000-4000-8000-000000000002",
        "0d000000-0000-4000-8000-000000000003", "0d000000-0000-4000-8000-000000000004",
        "0d000000-0000-4000-8000-000000000005", "0d000000-0000-4000-8000-000000000006",
    ];

    private static readonly IReadOnlyDictionary<string, string> DriverReferences = new Dictionary<string, string>
    {
        [DriverMoto] = "DRV-1a2b3c4d",
        [DriverCar] = "DRV-5e6f7a8b",
        [DriverVan] = "DRV-9c0d1e2f",
    };

    /// <summary>Request bodies as the browser's JSON.stringify writes them: accents are not escaped.</summary>
    private static readonly JsonSerializerOptions BrowserJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The screen's sources: AI-05 paths, the dashboard read model of the order's row and the hub negotiation.</summary>
    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/operations/dashboard$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/transitions$"),
        new("^GET /api/v1/incidents$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/assignable-drivers$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/assignments$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/tracking-link$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}/recipient-notices$"),
        new("^POST /api/v1/orders/[0-9a-f-]{36}/recipient-notices/stop$"),
        new("^POST /hubs/operations/negotiate$"),
    ];

    /// <summary>
    /// AI-07 next_step and rejection_messages, AI-09 "Cierre financiero / Efectivo pendiente": a DELIVERED order offers
    /// exactly the transitions getOrder lists, in the screen's order; the reason is required and the confirmation names the
    /// order and the action. A 409 COD_NOT_RECONCILED says the cash on delivery is not reconciled, a 409 without a code
    /// says the generic sentence, and each reads the order again without changing the offered actions; a 503 keeps the
    /// key and reads nothing, so the next press resends the same key and body; the applied close reads the order again.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Closing_names_the_unmet_rule_and_retries_only_an_unanswered_press_with_the_same_key()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Locale = "en-US",
            TimezoneId = "America/New_York",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        var order = api.Add(new DetailOrder
        {
            Id = OrderIds[0],
            PublicId = "ORD_DetalleSintetico000001",
            Owner = OrganizationA,
            Status = "DELIVERED",
            Version = 5,
            Allowed = [new("CLOSED"), new("CLAIM_OPEN")],
            DriverId = DriverMoto,
        });
        order.AllowedAfter["CLOSED"] = [new("CLAIM_OPEN")];
        api.TransitionAnswers.Enqueue(new Answer(409, "COD_NOT_RECONCILED"));
        api.TransitionAnswers.Enqueue(new Answer(409));
        api.TransitionAnswers.Enqueue(new Answer(503));

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        var step = NextStep(page);
        await ExpectButtonsAsync(step, "Cerrar orden", "Abrir reclamación");
        await page.Locator("main header").GetByText($"Última actualización: {GeneratedAtInMazatlan}", new() { Exact = true }).WaitForAsync();
        await ExpectStatusAsync(page, "Entregada");
        await ExpectTimelineAsync(page, "Orden creada 2 nov 2026, 09:00");

        // The reason is required before any confirmation or request.
        await step.GetByRole(AriaRole.Button, new() { Name = "Cerrar orden", Exact = true }).ClickAsync();
        Assert.Equal("true", await step.GetByRole(AriaRole.Button, new() { Name = "Cerrar orden", Exact = true }).GetAttributeAsync("aria-pressed"));
        await step.GetByRole(AriaRole.Button, new() { Name = "Continuar", Exact = true }).ClickAsync();
        await step.Locator("p.fieldError").GetByText("Escribe el motivo del cambio.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await OpenDialog(page).CountAsync());
        await step.GetByLabel("Motivo: cerrar orden").FillAsync("  Entrega conforme; cierre del día  ");

        // 409 COD_NOT_RECONCILED: the unmet rule in es-MX; the order is read again and offers the same actions.
        var reads = api.OrderReads(OrganizationA).Length;
        await ConfirmTransitionAsync(page, step, "Cerrar orden",
            $"Orden {order.PublicId}. La orden se cerrará; todavía podrá abrirse una reclamación dentro del plazo.");
        await ExpectTextAsync(step.Locator("p[role=status]"), "El cobro contra entrega aún no está conciliado.");
        await WaitForAsync(() => api.OrderReads(OrganizationA).Length == reads + 1, "the order was not read again after the 409");
        await ExpectButtonsAsync(step, "Cerrar orden", "Abrir reclamación");
        await ExpectStatusAsync(page, "Entregada");

        // 409 without a code: the generic sentence, a new key (the previous press was answered) and the same body.
        await ConfirmTransitionAsync(page, step, "Cerrar orden",
            $"Orden {order.PublicId}. La orden se cerrará; todavía podrá abrirse una reclamación dentro del plazo.");
        await ExpectTextAsync(step.Locator("p[role=status]"),
            "No se pudo cambiar el estado; la orden cambió o falta un requisito. Actualiza e intenta de nuevo.");
        await WaitForAsync(() => api.OrderReads(OrganizationA).Length == reads + 2, "the order was not read again after the second 409");

        // 503: no valid answer, so the order is not read and the key is kept for the same payload.
        await ConfirmTransitionAsync(page, step, "Cerrar orden",
            $"Orden {order.PublicId}. La orden se cerrará; todavía podrá abrirse una reclamación dentro del plazo.");
        await ExpectTextAsync(step.Locator("p[role=status]"),
            "Sin respuesta válida del servidor. Puedes reintentar: se reenvía la misma solicitud sin duplicarla.");

        // The retry of the same press is applied; the order is read again once and offers what is allowed now.
        await ConfirmTransitionAsync(page, step, "Cerrar orden",
            $"Orden {order.PublicId}. La orden se cerrará; todavía podrá abrirse una reclamación dentro del plazo.");
        await ExpectTextAsync(step.Locator("p[role=status]"), "Listo: Cerrar orden. Se actualizó la información.");
        await ExpectStatusAsync(page, "Cerrada");
        await ExpectButtonsAsync(step, "Abrir reclamación");
        Assert.Equal(reads + 3, api.OrderReads(OrganizationA).Length);
        Assert.Equal(0, await step.Locator("textarea").CountAsync());
        await ExpectTimelineAsync(page, "Orden creada 2 nov 2026, 09:00", "Estado actualizado 2 nov 2026, 22:25");

        var transitions = api.Transitions(OrganizationA);
        Assert.Equal(4, transitions.Length);
        Assert.All(transitions, transition =>
        {
            Assert.Equal($"/api/v1/orders/{order.Id}/transitions", transition.Path);
            Assert.Equal(string.Empty, transition.Query);
            Assert.Matches(TransitionKeyPattern(), transition.IdempotencyKey ?? string.Empty);
            Assert.Equal(
                JsonSerializer.Serialize(new { target_status = "CLOSED", reason = "Entrega conforme; cierre del día", expected_version = 5 }, BrowserJson),
                transition.Body);
        });
        Assert.Equal(3, transitions.Select(transition => transition.IdempotencyKey).Distinct().Count());
        Assert.Equal(transitions[2].IdempotencyKey, transitions[3].IdempotencyKey);
        AssertOrderReadsAreExact(api, OrganizationA, order.Id);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 failed_attempt and next_step from FAILED_ATTEMPT, on a device in Tokyo: a failed read of the newest incident
    /// says the reason could not be read and still offers the three decisions; once the order changed, the reason, "Lo
    /// decide despacho" and the attention deadline in Mazatlán are shown, never the description, evidence or ids. The
    /// dispatcher returns the package ("Regresar") or reschedules it ("Reprogramar") with the exact body; the order is
    /// read again and the failed attempt goes with its status.
    /// </summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("RETURNING", "Regresar", "Motivo de la devolución", "El paquete regresará a quien lo envió.", "En devolución")]
    [InlineData("RESCHEDULED", "Reprogramar", "Motivo de la reprogramación",
        "La orden quedará reprogramada; después podrás liberarla para recolección o asignarle un repartidor.", "Reprogramada")]
    public async Task Failed_attempt_shows_why_and_dispatch_decides_how_the_order_continues(
        string target, string label, string reasonLabel, string effect, string statusLabel)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Locale = "en-US",
            TimezoneId = "Asia/Tokyo",
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        var order = api.Add(new DetailOrder
        {
            Id = OrderIds[1],
            PublicId = "ORD_DetalleSintetico000002",
            Owner = OrganizationA,
            Status = "FAILED_ATTEMPT",
            Version = 7,
            Allowed = [new("RESCHEDULED"), new("DELIVERING"), new("RETURNING")],
            DriverId = DriverMoto,
        });
        order.AllowedAfter["RETURNING"] = [new("RETURNED")];
        order.AllowedAfter["RESCHEDULED"] = [new("READY_FOR_PICKUP"), new("CANCELLED")];
        if (target == "RESCHEDULED") order.OnTransition = changed => changed.DriverId = null;
        api.Incidents =
        [
            Incident("e2000000-0000-4000-8000-000000000002", order.Id, "ADDRESS_NOT_FOUND", null, "2026-11-03T06:30:00Z"),
            Incident("e2000000-0000-4000-8000-000000000001", order.Id, "RECIPIENT_ABSENT", "RESCHEDULED", "2026-11-02T20:00:00Z"),
        ];
        api.Drivers = [Driver(DriverMoto, "MOTORCYCLE", 0)];
        api.IncidentAnswers.Enqueue(new Answer(503));

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        var failed = page.Locator("section[aria-labelledby='failed-attempt-title']");
        var step = NextStep(page);

        // A failed read says so and never blocks "Siguiente paso", whose buttons are dispatch's decision.
        await ExpectTextAsync(failed.Locator("p[role=status]"), "No fue posible leer el motivo del intento fallido.");
        await ExpectButtonsAsync(step, "Reprogramar", "Reintentar hoy", "Regresar");
        await ExpectStatusAsync(page, "Intento fallido");

        // The order changed on the server (a newer version): it is read with "Actualizar" and the reason with it.
        order.Version = 8;
        await page.Locator("main header").GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true }).ClickAsync();
        await ExpectListAsync(
            () => failed.Locator("dl[aria-label='Intento fallido']").EvaluateAsync<string[]>(DescriptionListScript),
            ["Motivo: Dirección incorrecta", "Siguiente paso: Lo decide despacho", "Atender antes de: 2 nov 2026, 23:30"],
            "the failed attempt");
        await ExpectTextAsync(failed.Locator("p.noticeInfo[role=status]"),
            "El repartidor reportó el intento. Despacho decide cómo sigue la orden en «Siguiente paso».");
        Assert.Equal(
            new[] { $"order_id={order.Id}", $"order_id={order.Id}" },
            api.Calls(OrganizationA, "GET", "/api/v1/incidents").Select(call => call.Query));
        await ExpectButtonsAsync(step, "Reprogramar", "Reintentar hoy", "Regresar");
        await AssertNoIdentifiersAsync(page);

        // Dispatch decides with a reason and a confirmation that names the order and the decision.
        await step.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).ClickAsync();
        Assert.Equal("true", await step.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).GetAttributeAsync("aria-pressed"));
        await step.GetByLabel(reasonLabel).FillAsync("Despacho decidió tras llamar al remitente");
        await ConfirmTransitionAsync(page, step, label, $"Orden {order.PublicId}. {effect}");
        await ExpectTextAsync(step.Locator("p[role=status]"), $"Listo: {label}. Se actualizó la información.");
        await ExpectStatusAsync(page, statusLabel);
        await WaitForAsync(async () => await failed.CountAsync() == 0, "the failed attempt stayed after the order left FAILED_ATTEMPT");
        if (target == "RETURNING")
        {
            await ExpectButtonsAsync(step, "Marcar como devuelta");
        }
        else
        {
            await ExpectButtonsAsync(step, "Liberar para recolección", "Cancelar orden");
            await page.Locator("main p.noticeCrit[role=status]").GetByText("Requiere asignación", new() { Exact = true }).WaitForAsync();
            await page.Locator("section.opsDriverAssignment ul.opsDriverChoices > li").First.WaitForAsync();
        }

        var transition = Assert.Single(api.Transitions(OrganizationA));
        Assert.Matches(TransitionKeyPattern(), transition.IdempotencyKey ?? string.Empty);
        Assert.Equal(
            JsonSerializer.Serialize(new { target_status = target, reason = "Despacho decidió tras llamar al remitente", expected_version = 8 }, BrowserJson),
            transition.Body);
        Assert.Equal(2, api.Calls(OrganizationA, "GET", "/api/v1/incidents").Length);
        AssertOrderReadsAreExact(api, OrganizationA, order.Id);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 assignment_picker on the detail of a READY_FOR_PICKUP order: "Siguiente paso" never offers ASSIGNED (the
    /// picker does) and shows "Cancelar orden" in the danger style; the picker lists every own driver by DRV- reference
    /// with the reason of the ineligible ones; a 409 DRIVER_INELIGIBLE reads the order and the list again; the confirmed
    /// assignment sends integer cents of an inexact typed amount, its outcome stays while the order, read again, drops the
    /// picker, "Requiere asignación" and the unassigned summary.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Assignment_on_the_detail_reads_the_order_again_and_keeps_its_outcome()
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        var order = api.Add(new DetailOrder
        {
            Id = OrderIds[2],
            PublicId = "ORD_DetalleSintetico000003",
            Owner = OrganizationA,
            Status = "READY_FOR_PICKUP",
            Version = 4,
            Allowed = [new("ASSIGNED"), new("CANCELLED")],
        });
        order.AllowedAfter["ASSIGNED"] = [new("CANCELLED")];
        api.Drivers = [Driver(DriverVan, "VAN", 1, "DOCUMENT_EXPIRED"), Driver(DriverCar, "CAR", 2), Driver(DriverMoto, "MOTORCYCLE", 0)];
        api.DriverListsAfterFirst.Enqueue(
            [Driver(DriverVan, "VAN", 1, "DOCUMENT_EXPIRED"), Driver(DriverCar, "CAR", 2, "DOCUMENT_EXPIRED"), Driver(DriverMoto, "MOTORCYCLE", 0)]);
        api.AssignmentAnswers.Enqueue(new Answer(409, "DRIVER_INELIGIBLE"));

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        var step = NextStep(page);
        var panel = page.Locator("section.opsDriverAssignment");
        await ExpectButtonsAsync(step, "Cancelar orden");
        Assert.Contains("btnDanger", await step.GetByRole(AriaRole.Button, new() { Name = "Cancelar orden", Exact = true }).GetAttributeAsync("class") ?? string.Empty);
        await page.Locator("main p.noticeCrit[role=status]").GetByText("Requiere asignación", new() { Exact = true }).WaitForAsync();
        await ExpectSummaryAsync(page,
            $"Dueño: {OrganizationAName}", "Opera: Sin operador", "Cliente: Comercio Sintético del Valle", "Servicio: Mismo día",
            "Zona: Centro", "Recolección: Por confirmar", "Entrega: Horario de la zona", "Asignación: Sin asignación",
            "Última posición: No disponible", "Precio: Sin advertencias");
        await ExpectChoicesAsync(panel,
            "DRV-1a2b3c4d · Motocicleta · sin entregas en curso Disponible para esta orden",
            "DRV-5e6f7a8b · Automóvil · 2 entregas en curso Disponible para esta orden",
            "DRV-9c0d1e2f · Van · 1 entrega en curso No disponible: Tiene un documento vencido");
        Assert.True(await panel.Locator($"input[type=radio][value=\"{DriverVan}\"]").IsDisabledAsync());

        // The server refuses the driver: the order and the list are read again; the typed cost stays.
        var reads = api.OrderReads(OrganizationA).Length;
        await panel.Locator($"input[type=radio][value=\"{DriverCar}\"]").CheckAsync();
        await panel.GetByLabel("Costo de la asignación (MXN)").FillAsync("19.99");
        await ConfirmAssignmentAsync(page, panel,
            $"La orden {order.PublicId} se asignará al repartidor DRV-5e6f7a8b (automóvil) con un costo de $19.99 MXN.");
        await ExpectTextAsync(panel.Locator("p.noticeWarn[role=status]").Last,
            "El repartidor ya no cumple los requisitos para esta orden. Revisa la lista actualizada.");
        await ExpectChoicesAsync(panel,
            "DRV-1a2b3c4d · Motocicleta · sin entregas en curso Disponible para esta orden",
            "DRV-9c0d1e2f · Van · 1 entrega en curso No disponible: Tiene un documento vencido",
            "DRV-5e6f7a8b · Automóvil · 2 entregas en curso No disponible: Tiene un documento vencido");
        await WaitForAsync(() => api.OrderReads(OrganizationA).Length == reads + 1, "the order was not read again after the 409");
        Assert.Equal("19.99", await panel.GetByLabel("Costo de la asignación (MXN)").InputValueAsync());
        Assert.Equal(2, api.Calls(OrganizationA, "GET", $"/api/v1/orders/{order.Id}/assignable-drivers").Length);

        // 45.5 is not two decimals: it is still read as 4550 cents, never through floating point.
        await panel.Locator($"input[type=radio][value=\"{DriverMoto}\"]").CheckAsync();
        await panel.GetByLabel("Costo de la asignación (MXN)").FillAsync("45.5");
        await ConfirmAssignmentAsync(page, panel,
            $"La orden {order.PublicId} se asignará al repartidor DRV-1a2b3c4d (motocicleta) con un costo de $45.50 MXN.");
        await page.Locator("main p.noticeInfo[role=status]").GetByText("Repartidor DRV-1a2b3c4d asignado.", new() { Exact = true }).WaitForAsync();
        await ExpectStatusAsync(page, "Asignada");
        await WaitForAsync(async () => await panel.CountAsync() == 0, "the picker stayed after the order was assigned");
        await page.Locator("main p.noticeInfo[role=status]").GetByText("Repartidor DRV-1a2b3c4d asignado.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await page.Locator("main p.noticeCrit[role=status]").GetByText("Requiere asignación", new() { Exact = true }).CountAsync());
        await ExpectSummaryAsync(page,
            $"Dueño: {OrganizationAName}", "Opera: Sin operador", "Cliente: Comercio Sintético del Valle", "Servicio: Mismo día",
            "Zona: Centro", "Recolección: Por confirmar", "Entrega: Horario de la zona", "Asignación: Flota propia · DRV-1a2b3c4d",
            "Última posición: No disponible", "Precio: Sin advertencias");
        await ExpectButtonsAsync(step, "Cancelar orden");
        Assert.Equal(reads + 2, api.OrderReads(OrganizationA).Length);

        var assignments = api.Calls(OrganizationA, "POST", $"/api/v1/orders/{order.Id}/assignments");
        Assert.Equal(2, assignments.Length);
        Assert.Equal(
            new[]
            {
                JsonSerializer.Serialize(new { driver_id = DriverCar, assignment_type = "OWN", cost_cents = 1_999, route_id = (string?)null }),
                JsonSerializer.Serialize(new { driver_id = DriverMoto, assignment_type = "OWN", cost_cents = 4_550, route_id = (string?)null }),
            },
            assignments.Select(assignment => assignment.Body));
        Assert.All(assignments, assignment => Assert.Matches(AssignmentKeyPattern(), assignment.IdempotencyKey ?? string.Empty));
        Assert.NotEqual(assignments[0].IdempotencyKey, assignments[1].IdempotencyKey);
        Assert.Empty(api.Transitions(OrganizationA));
        AssertOrderReadsAreExact(api, OrganizationA, order.Id);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 recipient_notices (T1 to T6) and the tracking link (TRK-002) for the owner's dispatcher, then the switch to
    /// the organization that operates the same order: the link, the notices, the outcome of "Detener avisos", the open
    /// form of "Siguiente paso" and the kept key of an unanswered stop all go; the operator sees the order with
    /// "Otra organización" as owner, no link, no notices and no action, and reads nothing of them; back on the owner the
    /// next stop is a new press with a new key. "Última posición" shows the time and accuracy, never the coordinates.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Owner_sees_the_link_and_notices_stops_them_and_the_operator_switch_clears_everything()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Locale = "en-US",
            TimezoneId = "Europe/Madrid",
        });
        await context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new() { Origin = server.BaseAddress.GetLeftPart(UriPartial.Authority) });
        await using var trace = await PlaywrightFailureTrace.StartAsync(context, job: PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"), (OrganizationB, OrganizationBName, "DISPATCHER"));
        var order = api.Add(new DetailOrder
        {
            Id = OrderIds[3],
            PublicId = "ORD_DetalleSintetico000004",
            Owner = OrganizationA,
            Operator = OrganizationB,
            Status = "DELIVERED",
            Version = 6,
            Allowed = [new("CLOSED"), new("CLAIM_OPEN")],
            DriverId = DriverMoto,
            HasLocation = true,
        });
        api.TrackingValidUntil = "2026-11-04T05:15:00Z";
        api.WhatsAppConsent = true;
        api.EmailOnFile = true;
        api.Notices =
        [
            Notice("b4000000-0000-4000-8000-000000000001", "TRACKING_LINK", "WHATSAPP", "DELIVERED", 1, "MESSAGING_ACCEPTED", "2026-11-02T18:10:00Z"),
            Notice("b4000000-0000-4000-8000-000000000002", "IN_TRANSIT", "WHATSAPP", "FAILED", 3, "MESSAGING_RECIPIENT_UNDELIVERABLE", "2026-11-02T19:40:00Z"),
            Notice("b4000000-0000-4000-8000-000000000003", "IN_TRANSIT", "EMAIL", "SENT", 1, "MESSAGING_ACCEPTED", "2026-11-02T19:41:00Z"),
            Notice("b4000000-0000-4000-8000-000000000004", "DELIVERED", "EMAIL", "PENDING", 2, "MESSAGING_PROVIDER_TIMEOUT", "2026-11-03T04:55:00Z"),
        ];
        api.NoticesAfterStop =
        [
            api.Notices[0], api.Notices[1], api.Notices[2],
            Notice("b4000000-0000-4000-8000-000000000004", "DELIVERED", "EMAIL", "CANCELLED", 2, "RECIPIENT_OPTED_OUT", "2026-11-03T05:22:00Z"),
        ];
        api.StopAnswers.Enqueue(new Answer(503));
        api.StopAnswers.Enqueue(new Answer(200));
        api.StopAnswers.Enqueue(new Answer(503));

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        await ExpectSummaryAsync(page,
            $"Dueño: {OrganizationAName}", "Opera: Otra organización", "Cliente: Comercio Sintético del Valle", "Servicio: Mismo día",
            "Zona: Centro", "Recolección: Por confirmar", "Entrega: Horario de la zona", "Asignación: Flota propia · DRV-1a2b3c4d",
            "Última posición: 2 nov 2026, 22:05; precisión aproximada 13 m", "Precio: Sin advertencias");
        var body = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("24.80", body, StringComparison.Ordinal);
        Assert.DoesNotContain("107.39", body, StringComparison.Ordinal);

        // The link: get-or-create, one key per press, shown with its validity in Mazatlán, copied, hidden.
        var link = page.Locator("section.opsTrackingLink");
        await link.GetByRole(AriaRole.Button, new() { Name = "Ver enlace", Exact = true }).ClickAsync();
        var field = link.GetByLabel("Enlace público de la orden");
        await field.WaitForAsync();
        Assert.Equal(TrackingUrl, await field.InputValueAsync());
        await link.GetByText("Vigente hasta: 3 nov 2026, 22:15", new() { Exact = true }).WaitForAsync();
        await link.GetByRole(AriaRole.Button, new() { Name = "Copiar enlace", Exact = true }).ClickAsync();
        await link.GetByRole(AriaRole.Button, new() { Name = "Copiado", Exact = true }).WaitForAsync();
        Assert.Equal(TrackingUrl, await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
        await link.GetByRole(AriaRole.Button, new() { Name = "Ocultar", Exact = true }).ClickAsync();
        await WaitForAsync(async () => await link.Locator("#tracking-link-url").CountAsync() == 0, "the link stayed after Ocultar");
        await link.GetByRole(AriaRole.Button, new() { Name = "Ver enlace", Exact = true }).ClickAsync();
        await field.WaitForAsync();
        Assert.Equal(TrackingUrl, await field.InputValueAsync());
        var links = api.Calls(OrganizationA, "POST", $"/api/v1/orders/{order.Id}/tracking-link");
        Assert.Equal(2, links.Length);
        Assert.All(links, call =>
        {
            Assert.Matches(TrackingKeyPattern(), call.IdempotencyKey ?? string.Empty);
            Assert.Null(call.Body);
            Assert.Equal(string.Empty, call.Query);
        });
        Assert.NotEqual(links[0].IdempotencyKey, links[1].IdempotencyKey);

        // The notices: the fixed lines, the order's two facts and one row per notice and channel, times in Mazatlán.
        var notices = page.Locator("section.opsRecipientNotices");
        await notices.GetByRole(AriaRole.Heading, new() { Name = "Avisos al destinatario", Exact = true }).WaitForAsync();
        Assert.Equal(
            new[] { "Los avisos al destinatario todavía no están activos.", "Los avisos solo salen de 8:00 a 21:00, hora de Mazatlán" },
            (await notices.Locator("p.opsSummaryNote").AllInnerTextsAsync()).Select(text => text.Trim()));
        await ExpectListAsync(() => notices.Locator("ul > li").AllInnerTextsAsync(),
            ["WhatsApp: permitido (se marcó la casilla)", "Correo del destinatario: registrado"], "the order's facts");
        await ExpectNoticesAsync(notices,
            "Enlace de seguimiento · WhatsApp — Recibido | 2 nov 2026, 11:10",
            "Va en camino · WhatsApp — No se pudo enviar | 2 nov 2026, 12:40 | Intentos: 3 | No llegó: el número no recibe WhatsApp.",
            "Va en camino · Correo — Enviado | 2 nov 2026, 12:41",
            "Entregado · Correo — Pendiente | 2 nov 2026, 21:55 | Intentos: 2 | Reintentando");

        // T1: the dialog starts on "Cancelar"; cancelling sends nothing.
        var stop = notices.GetByRole(AriaRole.Button, new() { Name = "Detener avisos", Exact = true });
        await stop.ClickAsync();
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal("¿Detener avisos?", (await dialog.Locator("h2").InnerTextAsync()).Trim());
        Assert.Equal(
            $"Orden {order.PublicId}. El destinatario dejará de recibir avisos de todas las empresas. Durante el piloto no se puede deshacer.",
            (await dialog.Locator("p").InnerTextAsync()).Trim());
        Assert.Equal(new[] { "Cancelar", "Detener avisos" }, (await dialog.Locator("button").AllInnerTextsAsync()).Select(text => text.Trim()));
        Assert.Equal("Cancelar", await page.EvaluateAsync<string>("() => document.activeElement?.textContent ?? ''"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancelar", Exact = true }).ClickAsync();
        await WaitForAsync(async () => await OpenDialog(page).CountAsync() == 0, "the dialog stayed open after Cancelar");
        Assert.Empty(api.Calls(OrganizationA, "POST", $"/api/v1/orders/{order.Id}/recipient-notices/stop"));

        // An unanswered stop keeps its key; the retry of that press is answered and the list is read after each answer.
        var noticeReads = api.NoticeReads(OrganizationA);
        await ConfirmStopAsync(page, stop);
        await ExpectTextAsync(notices.Locator("p[role=status]"), "No pudimos detener los avisos. Intenta de nuevo.");
        await WaitForAsync(() => api.NoticeReads(OrganizationA) == noticeReads + 1, "the notices were not read again after the failed stop");
        await ConfirmStopAsync(page, stop);
        await ExpectTextAsync(notices.Locator("p[role=status]"),
            "Listo. Se detuvieron los avisos por WhatsApp y correo. Los avisos pendientes ya no se enviarán.");
        await ExpectNoticesAsync(notices,
            "Enlace de seguimiento · WhatsApp — Recibido | 2 nov 2026, 11:10",
            "Va en camino · WhatsApp — No se pudo enviar | 2 nov 2026, 12:40 | Intentos: 3 | No llegó: el número no recibe WhatsApp.",
            "Va en camino · Correo — Enviado | 2 nov 2026, 12:41",
            "Entregado · Correo — No se envió | 2 nov 2026, 22:22 | Intentos: 2 | El destinatario pidió no recibir avisos.");
        Assert.Equal(noticeReads + 2, api.NoticeReads(OrganizationA));

        // T3: the button stays; a new press is a new key, and its unanswered key is kept for a retry.
        await ConfirmStopAsync(page, stop);
        await ExpectTextAsync(notices.Locator("p[role=status]"), "No pudimos detener los avisos. Intenta de nuevo.");
        var stops = api.Calls(OrganizationA, "POST", $"/api/v1/orders/{order.Id}/recipient-notices/stop");
        Assert.Equal(3, stops.Length);
        Assert.All(stops, call =>
        {
            Assert.Matches(StopKeyPattern(), call.IdempotencyKey ?? string.Empty);
            Assert.Null(call.Body);
        });
        Assert.Equal(stops[0].IdempotencyKey, stops[1].IdempotencyKey);
        Assert.NotEqual(stops[1].IdempotencyKey, stops[2].IdempotencyKey);

        // A draft of "Siguiente paso" is open with its reason when the organization changes.
        var step = NextStep(page);
        await step.GetByRole(AriaRole.Button, new() { Name = "Cerrar orden", Exact = true }).ClickAsync();
        await step.GetByLabel("Motivo: cerrar orden").FillAsync("Borrador de cierre");
        await AssertNoIdentifiersAsync(page);

        // The operator organization: the same order with its own names, nothing of the owner's panels and no reads of them.
        var switchedAt = DateTimeOffset.UtcNow;
        await SwitchOrganizationAsync(page, OrganizationBName);
        await ExpectSummaryAsync(page,
            "Dueño: Otra organización", $"Opera: {OrganizationBName}", "Cliente: Comercio Sintético del Valle", "Servicio: Mismo día",
            "Zona: Centro", "Recolección: Por confirmar", "Entrega: Horario de la zona", "Asignación: Flota propia · DRV-1a2b3c4d",
            "Última posición: 2 nov 2026, 22:05; precisión aproximada 13 m", "Precio: Sin advertencias");
        await Task.Delay(1_000);
        Assert.Equal(0, await page.Locator("section.opsTrackingLink").CountAsync());
        Assert.Equal(0, await page.Locator("section.opsRecipientNotices").CountAsync());
        Assert.Equal(0, await NextStep(page).CountAsync());
        Assert.Equal(0, await page.Locator("main textarea").CountAsync());
        var underOperator = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("Detener avisos", underOperator, StringComparison.Ordinal);
        Assert.DoesNotContain("No pudimos detener", underOperator, StringComparison.Ordinal);
        Assert.DoesNotContain(api.AllCalls, call => call.Organization == OrganizationA && call.At > switchedAt);
        Assert.Equal(
            new[] { "GET /api/v1/me/organization-contexts", "GET /api/v1/operations/dashboard", $"GET /api/v1/orders/{order.Id}" },
            api.AllCalls.Where(call => call.Organization == OrganizationB).Select(call => $"{call.Method} {call.Path}").Distinct().Order(StringComparer.Ordinal));
        AssertOrderReadsAreExact(api, OrganizationB, order.Id);
        await AssertNoIdentifiersAsync(page);

        // Back on the owner: the panels start empty, and the next stop is a new press with a new key.
        await SwitchOrganizationAsync(page, OrganizationAName);
        await ExpectSummaryAsync(page,
            $"Dueño: {OrganizationAName}", "Opera: Otra organización", "Cliente: Comercio Sintético del Valle", "Servicio: Mismo día",
            "Zona: Centro", "Recolección: Por confirmar", "Entrega: Horario de la zona", "Asignación: Flota propia · DRV-1a2b3c4d",
            "Última posición: 2 nov 2026, 22:05; precisión aproximada 13 m", "Precio: Sin advertencias");
        notices = page.Locator("section.opsRecipientNotices");
        stop = notices.GetByRole(AriaRole.Button, new() { Name = "Detener avisos", Exact = true });
        await stop.WaitForAsync();
        Assert.Equal(0, await page.Locator("#tracking-link-url").CountAsync());
        Assert.Equal(0, await notices.Locator("p[role=status]").CountAsync());
        Assert.Equal(0, await page.Locator("main textarea").CountAsync());
        await ConfirmStopAsync(page, stop);
        await ExpectTextAsync(notices.Locator("p[role=status]"),
            "Listo. Se detuvieron los avisos por WhatsApp y correo. Los avisos pendientes ya no se enviarán.");
        var last = api.Calls(OrganizationA, "POST", $"/api/v1/orders/{order.Id}/recipient-notices/stop").Last();
        Assert.Matches(StopKeyPattern(), last.IdempotencyKey ?? string.Empty);
        Assert.NotEqual(stops[2].IdempotencyKey, last.IdempotencyKey);
        Assert.Empty(api.Transitions(OrganizationA));
        Assert.Empty(api.Transitions(OrganizationB));

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// D5 and the AI-05 matrices on the detail. PLATFORM_ADMIN (MFA satisfied, so the API answers) gets every action: the
    /// allowed transitions, the picker, which lists no eligible driver and points to "Publicar oferta externa desde el
    /// tablero", the link and the notices with their empty list. VIEWER (getOrder admits it, the dashboard row does not) and
    /// FINANCE receive 403 and see "Acceso no disponible": no guía, no panel, no panel read and no hub.
    /// </summary>
    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("PLATFORM_ADMIN")]
    [InlineData("VIEWER")]
    [InlineData("FINANCE")]
    public async Task Each_role_gets_only_the_actions_its_operations_admit(string role)
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, role));
        var order = api.Add(new DetailOrder
        {
            Id = OrderIds[4],
            PublicId = "ORD_DetalleSintetico000005",
            Owner = OrganizationA,
            Status = "RESCHEDULED",
            Version = 3,
            Allowed = [new("READY_FOR_PICKUP"), new("CANCELLED")],
        });
        api.Drivers = [Driver(DriverMoto, "MOTORCYCLE", 0, "DRIVER_UNAVAILABLE"), Driver(DriverCar, "CAR", 1, "HOME_CITY_MISMATCH")];

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        if (role == "PLATFORM_ADMIN")
        {
            await ExpectButtonsAsync(NextStep(page), "Liberar para recolección", "Cancelar orden");
            var panel = page.Locator("section.opsDriverAssignment");
            await ExpectTextAsync(panel.Locator("fieldset p.noticeWarn[role=status]"),
                "Ningún repartidor propio cumple hoy los requisitos para esta orden. Puedes publicar una oferta externa desde el tablero.");
            await ExpectChoicesAsync(panel,
                "DRV-1a2b3c4d · Motocicleta · sin entregas en curso No disponible: No está disponible",
                "DRV-5e6f7a8b · Automóvil · 1 entrega en curso No disponible: Trabaja en otra ciudad");
            Assert.Equal(2, await panel.Locator("input[type=radio]:disabled").CountAsync());
            Assert.Equal("true", await panel.GetByRole(AriaRole.Button, new() { Name = "Asignar repartidor", Exact = true }).GetAttributeAsync("aria-disabled"));
            Assert.Equal("/ops/dashboard", await page.GetByRole(AriaRole.Link, new() { Name = "Publicar oferta externa desde el tablero", Exact = true }).GetAttributeAsync("href"));
            await page.Locator("section.opsTrackingLink").GetByRole(AriaRole.Button, new() { Name = "Ver enlace", Exact = true }).WaitForAsync();
            var notices = page.Locator("section.opsRecipientNotices");
            await notices.GetByText("Esta orden todavía no tiene avisos.", new() { Exact = true }).WaitForAsync();
            await ExpectListAsync(() => notices.Locator("ul > li").AllInnerTextsAsync(),
                ["WhatsApp: no permitido (no se marcó la casilla)", "Correo del destinatario: no registrado"], "the order's facts");
            Assert.True(await notices.GetByRole(AriaRole.Button, new() { Name = "Detener avisos", Exact = true }).IsEnabledAsync());
            await Task.Delay(500);
            Assert.Empty(api.Calls(OrganizationA, "GET", "/api/v1/incidents"));
            AssertOrderReadsAreExact(api, OrganizationA, order.Id);
        }
        else
        {
            await page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true }).WaitForAsync();
            await page.GetByText("No es posible mostrar información de operaciones.", new() { Exact = true }).WaitForAsync();
            await Task.Delay(1_000);
            var main = await page.Locator("main").InnerTextAsync();
            Assert.DoesNotContain(order.PublicId, main, StringComparison.Ordinal);
            Assert.DoesNotContain("Resumen de la orden", main, StringComparison.Ordinal);
            Assert.Equal(0, await page.Locator("main section").CountAsync());
            Assert.Equal(
                new[] { "GET /api/v1/me/organization-contexts", "GET /api/v1/operations/dashboard", $"GET /api/v1/orders/{order.Id}" },
                api.AllCalls.Select(call => $"{call.Method} {call.Path}").Distinct().Order(StringComparer.Ordinal));
            Assert.DoesNotContain(calls.Api, call => call.Path.StartsWith("/hubs/", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(api.AllCalls, call => call.Method == "POST");
        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    /// <summary>
    /// AI-07 next_step input and the screen's rate_limit (SEC-004-PANEL-SCREENS-2026-10-13): "Confirmar orden" needs the
    /// reason and the restricted goods acknowledgement, sent as the JSON literal true. A 429 on the order's dashboard row
    /// counts down on the screen, is announced once with the seconds asked for, disables "Actualizar" and keeps the order;
    /// the transition, which is not limited, is applied during the wait, but the read after it, like the notices panel,
    /// waits for 0 and happens once, by itself.
    /// </summary>
    [Fact]
    [Trait("Category", "OperationsDashboardPwa")]
    public async Task Confirming_needs_the_acknowledgement_and_the_read_after_it_waits_for_the_dashboard()
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
        var api = await InstallAsync(page, (OrganizationA, OrganizationAName, "DISPATCHER"));
        var order = api.Add(new DetailOrder
        {
            Id = OrderIds[5],
            PublicId = "ORD_DetalleSintetico000006",
            Owner = OrganizationA,
            Status = "DRAFT",
            Version = 1,
            Allowed = [new("CONFIRMED", "restricted_goods_acknowledged"), new("CANCELLED")],
        });
        order.AllowedAfter["CONFIRMED"] = [new("READY_FOR_PICKUP"), new("CANCELLED")];

        await page.GotoAsync(new Uri(server.BaseAddress, $"/ops/orders/{order.Id}").AbsoluteUri);
        var step = NextStep(page);
        await ExpectButtonsAsync(step, "Confirmar orden", "Cancelar orden");
        await page.Locator("section.opsRecipientNotices").GetByText("Esta orden todavía no tiene avisos.", new() { Exact = true }).WaitForAsync();

        // The reason alone is not enough: the acknowledgement is required before the confirmation.
        await step.GetByRole(AriaRole.Button, new() { Name = "Confirmar orden", Exact = true }).ClickAsync();
        await step.GetByLabel("Motivo: confirmar orden").FillAsync("El cliente confirmó el contenido del envío");
        var acknowledgement = step.GetByRole(AriaRole.Checkbox, new() { Name = "Confirmo que el envío no contiene artículos prohibidos.", Exact = true });
        Assert.False(await acknowledgement.IsCheckedAsync());
        await step.GetByRole(AriaRole.Button, new() { Name = "Continuar", Exact = true }).ClickAsync();
        await step.Locator("p.fieldError").GetByText("Confirma que el envío no contiene artículos prohibidos.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(0, await OpenDialog(page).CountAsync());
        await acknowledgement.CheckAsync();

        // "Actualizar" meets a 429 on the dashboard row: the countdown, one announcement, "Actualizar" disabled.
        var refresh = page.Locator("main header").GetByRole(AriaRole.Button, new() { Name = "Actualizar", Exact = true });
        api.DashboardTooManyRequests = 1;
        await refresh.ClickAsync();
        await page.Locator("main").GetByRole(AriaRole.Timer).GetByText(TooManyRequestsPattern()).WaitForAsync();
        Assert.Equal("Demasiadas solicitudes. Espera 6 segundos e intenta de nuevo.",
            (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        Assert.True(await refresh.IsDisabledAsync());
        var limitedAt = api.Calls(OrganizationA, "GET", "/api/v1/operations/dashboard").Last().At;

        // The transition is not limited: it is applied during the wait.
        await ConfirmTransitionAsync(page, step, "Confirmar orden", $"Orden {order.PublicId}. La orden quedará confirmada y lista para prepararse.");
        await ExpectTextAsync(step.Locator("p[role=status]"), "Listo: Confirmar orden. Se actualizó la información.");
        var transition = Assert.Single(api.Transitions(OrganizationA));
        Assert.True(transition.At < limitedAt.AddSeconds(5), "The transition was not sent during the wait, so the pause was not exercised.");
        Assert.Matches(TransitionKeyPattern(), transition.IdempotencyKey ?? string.Empty);
        Assert.Equal(
            JsonSerializer.Serialize(new
            {
                target_status = "CONFIRMED",
                reason = "El cliente confirmó el contenido del envío",
                expected_version = 1,
                metadata = new { restricted_goods_acknowledged = true },
            }, BrowserJson),
            transition.Body);

        // At 0 the order is read once, by itself, and the notices panel with it.
        await ExpectStatusAsync(page, "Confirmada");
        await ExpectButtonsAsync(step, "Liberar para recolección", "Cancelar orden");
        await WaitForAsync(async () => await page.Locator("main").GetByRole(AriaRole.Timer).CountAsync() == 0, "the countdown stayed after 0");
        Assert.True(await refresh.IsEnabledAsync());
        Assert.Equal(string.Empty, (await page.Locator("p.srOnly[role=status]").InnerTextAsync()).Trim());
        await WaitForAsync(() => api.NoticeReadTimes(OrganizationA).Any(at => at > limitedAt), "the notices were not read after the wait");
        await Task.Delay(500);
        // After the 429 (and, for getOrder, which answered with it, after the transition): one read each, at 0.
        var dashboardReads = api.Calls(OrganizationA, "GET", "/api/v1/operations/dashboard").Where(call => call.At > limitedAt).ToArray();
        var orderReads = api.Calls(OrganizationA, "GET", $"/api/v1/orders/{order.Id}").Where(call => call.At > transition.At).ToArray();
        var noticeReads = api.NoticeReadTimes(OrganizationA).Where(at => at > limitedAt).ToArray();
        Assert.Single(dashboardReads);
        Assert.Single(orderReads);
        Assert.Single(noticeReads);
        Assert.True(dashboardReads[0].At >= limitedAt.AddSeconds(5.5), "The dashboard row was read during the wait.");
        Assert.True(orderReads[0].At >= limitedAt.AddSeconds(5.5), "The order was read during the wait.");
        Assert.True(noticeReads[0] >= limitedAt.AddSeconds(5.5), "The notices were read during the wait.");
        AssertOrderReadsAreExact(api, OrganizationA, order.Id);

        await AssertNoIdentifiersAsync(page);
        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

    private const string DescriptionListScript =
        "dl => [...dl.querySelectorAll(':scope > div')].map(d => d.querySelector('dt').innerText.trim() + ': ' + " +
        "d.querySelector('dd').innerText.replace(/\\s+/g, ' ').trim())";

    private static ILocator NextStep(IPage page) => page.Locator("section[aria-labelledby='next-step-title']");

    private static ILocator OpenDialog(IPage page) => page.Locator("dialog.confirmDialog[open]");

    private static Task SwitchOrganizationAsync(IPage page, string name) =>
        page.Locator("label.appOrg select").SelectOptionAsync(new SelectOptionValue { Label = name });

    /// <summary>"Continuar", then the confirmation: its title, its text naming the order, "Cancelar" first and focused.</summary>
    private static async Task ConfirmTransitionAsync(IPage page, ILocator step, string label, string description)
    {
        await step.GetByRole(AriaRole.Button, new() { Name = "Continuar", Exact = true }).ClickAsync();
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal($"¿{label}?", (await dialog.Locator("h2").InnerTextAsync()).Trim());
        Assert.Equal(description, (await dialog.Locator("p").InnerTextAsync()).Trim());
        Assert.Equal(new[] { "Cancelar", label }, (await dialog.Locator("button").AllInnerTextsAsync()).Select(text => text.Trim()));
        Assert.Equal("Cancelar", await page.EvaluateAsync<string>("() => document.activeElement?.textContent ?? ''"));
        await dialog.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).ClickAsync();
    }

    private static async Task ConfirmAssignmentAsync(IPage page, ILocator panel, string description)
    {
        await panel.GetByRole(AriaRole.Button, new() { Name = "Asignar repartidor", Exact = true }).ClickAsync();
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        Assert.Equal("¿Asignar repartidor?", (await dialog.Locator("h2").InnerTextAsync()).Trim());
        Assert.Equal(description, (await dialog.Locator("p").InnerTextAsync()).Trim());
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Asignar", Exact = true }).ClickAsync();
    }

    private static async Task ConfirmStopAsync(IPage page, ILocator stop)
    {
        await WaitForAsync(async () => await stop.IsEnabledAsync(), "Detener avisos stayed disabled");
        await stop.ClickAsync();
        var dialog = OpenDialog(page);
        await dialog.WaitForAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Detener avisos", Exact = true }).ClickAsync();
    }

    private static async Task ExpectTextAsync(ILocator locator, string expected)
    {
        var actual = string.Empty;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await locator.CountAsync() == 1)
            {
                actual = (await locator.InnerTextAsync()).Trim();
                if (actual == expected) return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Expected «{expected}», found «{actual}».");
    }

    private static async Task ExpectListAsync(Func<Task<IReadOnlyList<string>>> read, string[] expected, string what)
    {
        string[] actual = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                actual = [.. (await read()).Select(text => Regex.Replace(text, "\\s+", " ").Trim())];
                if (actual.SequenceEqual(expected, StringComparer.Ordinal)) return;
            }
            catch (PlaywrightException)
            {
                // The element was replaced while it was read (a new read of the order); read it again.
            }

            await Task.Delay(50);
        }

        Assert.Fail($"{what} did not match: {string.Join(" || ", actual)}");
    }

    private static Task ExpectListAsync(Func<Task<string[]>> read, string[] expected, string what) =>
        ExpectListAsync(async () => (IReadOnlyList<string>)await read(), expected, what);

    /// <summary>The section's buttons, never a disabled placeholder.</summary>
    private static async Task ExpectButtonsAsync(ILocator step, params string[] labels)
    {
        await ExpectListAsync(() => step.Locator(".opsFormActions").First.Locator("button").AllInnerTextsAsync(), labels, "Siguiente paso");
        Assert.Equal(0, await step.Locator(".opsFormActions").First.Locator("button:disabled, button[aria-disabled=true]").CountAsync());
    }

    private static Task ExpectStatusAsync(IPage page, string label) =>
        page.Locator("main header .statusBadgeLabel").GetByText(label, new() { Exact = true }).WaitForAsync();

    private static Task ExpectSummaryAsync(IPage page, params string[] items) =>
        ExpectListAsync(
            () => page.Locator("main dl[aria-label='Resumen de la orden']").EvaluateAsync<string[]>(DescriptionListScript),
            items,
            "the order summary");

    private static Task ExpectTimelineAsync(IPage page, params string[] items) =>
        ExpectListAsync(
            () => page.Locator("section[aria-labelledby='timeline-title'] ol.opsTimeline > li").EvaluateAllAsync<string[]>(
                "items => items.map(item => item.querySelector('strong').textContent.trim() + ' ' + item.querySelector('time').textContent.trim())"),
            items,
            "the timeline");

    private static Task ExpectChoicesAsync(ILocator panel, params string[] items) =>
        ExpectListAsync(() => panel.Locator("ul.opsDriverChoices > li").AllInnerTextsAsync(), items, "the driver choices");

    /// <summary>Each notice row as its parts: «aviso · canal — estado», the time, the attempts and the reason.</summary>
    private static Task ExpectNoticesAsync(ILocator notices, params string[] rows) =>
        ExpectListAsync(
            () => notices.Locator("ol.opsTimeline > li").EvaluateAllAsync<string[]>(
                "items => items.map(item => [...item.children].map(child => child.textContent.trim()).join(' | '))"),
            rows,
            "the notices");

    /// <summary>Every read of the order and its row asks for exactly that order.</summary>
    private static void AssertOrderReadsAreExact(DetailApi api, string organization, string orderId)
    {
        var rows = api.Calls(organization, "GET", "/api/v1/operations/dashboard");
        Assert.NotEmpty(rows);
        Assert.All(rows, call => Assert.Equal($"order_id={orderId}", call.Query));
        var orders = api.OrderReads(organization);
        Assert.NotEmpty(orders);
        Assert.All(orders, call => Assert.Equal(string.Empty, call.Query));
    }

    /// <summary>
    /// No internal id is visible anywhere on the page: no UUID, nor the first 8 characters of any synthetic organization,
    /// order, zone, client, driver, assignment, incident, notice or link id. Only guías and DRV- references are shown.
    /// </summary>
    private static async Task AssertNoIdentifiersAsync(IPage page)
    {
        var visible = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotMatch(UuidPattern(), visible);
        foreach (var id in new[]
                 {
                     OrganizationA, OrganizationB, OrderIds[0], ZoneCentro, ClientAccount, DriverMoto, AssignmentId, TokenId,
                     "e2000000-0000-4000-8000-000000000001", "b4000000-0000-4000-8000-000000000001",
                     "f3000000-0000-4000-8000-000000000001", "61000000-0000-4000-8000-000000000001",
                     "62000000-0000-4000-8000-000000000001", "63000000-0000-4000-8000-000000000001",
                     "64000000-0000-4000-8000-000000000001",
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

    /// <summary>Every API call is a source of this screen, and nothing goes to any third origin.</summary>
    private static void AssertOnlyAllowedCalls(CallRecorder calls, Uri web)
    {
        Assert.NotEmpty(calls.Api);
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
    /// switch and fulfils every API call from <see cref="DetailApi"/>; the OperationsHub is unreachable.
    /// </summary>
    private static async Task<DetailApi> InstallAsync(IPage page, params (string Id, string Name, string Role)[] organizations)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(organizations[0].Id)}},
              sessionNamespace: "ui-001-order-detail-actions-playwright",
              getAccessToken: () => "synthetic-operations-token",
              requestOrganizationChange: organizationId => {
                window.__paquetenviaOperationsSession = { ...window.__paquetenviaOperationsSession, organizationId };
                window.dispatchEvent(new Event("paquetenvia:operations-session-changed"));
              },
            };
            """);
        var api = new DetailApi(organizations);
        await page.RouteAsync("**/api/v1/**", api.HandleAsync);
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        return api;
    }

    private static object Driver(string id, string vehicle, int active, params string[] reasons) => new
    {
        driver_id = id,
        driver_reference = DriverReferences[id],
        vehicle_type = vehicle,
        eligible = reasons.Length == 0,
        ineligibility_reasons = reasons,
        active_assignment_count = active,
    };

    private static object Incident(string id, string orderId, string reason, string? nextAction, string slaDueAt) => new
    {
        id,
        order_id = orderId,
        status = "OPEN",
        severity = "MEDIUM",
        reason_code = reason,
        next_action = nextAction,
        custody_acquired = true,
        occurred_at = "2026-11-03T04:30:00Z",
        sla_due_at = slaDueAt,
        evidence_proof_ids = new[] { "f3000000-0000-4000-8000-000000000001" },
    };

    private static object Notice(string id, string notice, string channel, string status, int attempts, string? lastCode, string updatedAt) => new
    {
        notification_id = id,
        notice,
        channel,
        status,
        attempts,
        last_code = lastCode,
        sent_at = status is "SENT" or "DELIVERED" ? updatedAt : null,
        delivered_at = status == "DELIVERED" ? updatedAt : null,
        updated_at = updatedAt,
    };

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

    /// <summary>One key per confirmed transition payload (order-transition-controller.ts).</summary>
    [GeneratedRegex("^order-transition-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex TransitionKeyPattern();

    /// <summary>One key per confirmed driver and cost (driver-assignment-controller.ts).</summary>
    [GeneratedRegex("^assign-driver-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex AssignmentKeyPattern();

    /// <summary>One key per press of "Ver enlace" (tracking-link-api.ts).</summary>
    [GeneratedRegex("^tracking-link-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex TrackingKeyPattern();

    /// <summary>One key per press of "Detener avisos" (recipient-notices-controller.ts).</summary>
    [GeneratedRegex("^recipient-notices-stop-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex StopKeyPattern();

    /// <summary>The visible countdown at any second of the 6-second wait.</summary>
    [GeneratedRegex("^Demasiadas solicitudes\\. Espera [1-6] segundos? e intenta de nuevo\\.$")]
    private static partial Regex TooManyRequestsPattern();

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }

    /// <summary>An AI-05 OrderAllowedTransition.</summary>
    private sealed record AllowedTransition(string Target, params string[] Metadata)
    {
        internal object Json() => new { target_status = Target, required_metadata = Metadata };
    }

    /// <summary>A scripted answer: the status and, for a problem, its code.</summary>
    private sealed record Answer(int Status, string? Code = null);

    private sealed record DetailCall(
        DateTimeOffset At, string Organization, string Method, string Path, string Query, string? IdempotencyKey, string? Body);

    /// <summary>
    /// One order as the server holds it: its owner and operator, status, version, the transitions getOrder lists to the
    /// owner (and after each applied transition) and its OWN assignment.
    /// </summary>
    private sealed class DetailOrder
    {
        internal required string Id { get; init; }
        internal required string PublicId { get; init; }
        internal required string Owner { get; init; }
        internal string? Operator { get; init; }
        internal required string Status { get; set; }
        internal required int Version { get; set; }
        internal AllowedTransition[] Allowed { get; set; } = [];
        internal Dictionary<string, AllowedTransition[]> AllowedAfter { get; } = [];
        internal List<(string Type, string At)> Timeline { get; } = [("ORDER_CREATED", "2026-11-02T16:00:00Z")];
        internal string? DriverId { get; set; }
        internal bool HasLocation { get; init; }
        internal Action<DetailOrder>? OnTransition { get; set; }

        internal bool UnassignedAlert => DriverId is null && Status is "READY_FOR_PICKUP" or "RESCHEDULED";

        /// <summary>AI-05 Order (transitionOrder answers it without timeline and allowed_transitions).</summary>
        internal Dictionary<string, object?> Order() => new()
        {
            ["id"] = Id,
            ["public_id"] = PublicId,
            ["owner_org_id"] = Owner,
            ["operator_org_id"] = Operator,
            ["status"] = Status,
            ["price_net"] = new { currency = "MXN", amount_cents = 21_552L },
            ["version"] = Version,
            ["origin_location_id"] = "61000000-0000-4000-8000-000000000001",
            ["destination_location_id"] = "62000000-0000-4000-8000-000000000001",
            ["service_type"] = "SAME_DAY",
            ["quote_id"] = "63000000-0000-4000-8000-000000000001",
            ["city_id"] = "64000000-0000-4000-8000-000000000001",
            ["service_area_id"] = null,
            ["pricing_tier"] = "OCCASIONAL",
            ["total"] = new { currency = "MXN", amount_cents = 25_000L },
            ["claim_window_ends_at"] = null,
            ["finalized_at"] = null,
            ["service_window"] = null,
            ["created_at"] = "2026-11-02T16:00:00Z",
        };

        /// <summary>AI-05 OrderDetail for the reader: an operator organization receives no allowed transition.</summary>
        internal Dictionary<string, object?> Detail(string reader)
        {
            var detail = Order();
            detail["timeline"] = Timeline.Select(item => new { event_type = item.Type, occurred_at = item.At }).ToArray();
            detail["allowed_transitions"] = reader == Owner ? Allowed.Select(item => item.Json()).ToArray() : Array.Empty<object>();
            return detail;
        }

        /// <summary>The order's row of the dashboard read model: the reader's own name only (OBS-001-OPERATED-OWNER-NAME).</summary>
        internal object Row(string reader, string readerName) => new
        {
            order_id = Id,
            aggregate_version = Version,
            public_id = PublicId,
            owner = new { organization_id = Owner, display_name = Owner == reader ? readerName : null },
            @operator = Operator is null ? null : new { organization_id = Operator, display_name = Operator == reader ? readerName : null },
            client = new { client_account_id = ClientAccount, display_name = "Comercio Sintético del Valle" },
            status = Status,
            created_at = "2026-11-02T16:00:00Z",
            updated_at = "2026-11-03T05:00:00Z",
            service_type = "SAME_DAY",
            pickup_window = (object?)null,
            delivery_window = (object?)null,
            delivery_zone = new { operating_zone_id = ZoneCentro, name = "Centro", zone_type = "CORE" },
            assignment = DriverId is null
                ? null
                : new { assignment_id = AssignmentId, assignment_type = "OWN", status = "ACCEPTED", driver_id = DriverId, driver_reference = DriverReferences[DriverId] },
            latest_driver_location = HasLocation && DriverId is not null
                ? new { lat = 24.806912, lng = -107.393851, accuracy_m = 12.6, captured_at = "2026-11-03T05:05:00Z" }
                : null,
            total = new { currency = "MXN", amount_cents = 25_000L },
            cost_warning = (string?)null,
            unassigned_alert = UnassignedAlert,
        };
    }

    /// <summary>
    /// The server: RLS shows an order only to its owner and operator (the uniform 404 otherwise), each operation admits
    /// the roles of its AI-05 matrix (403 otherwise), the link and the notices only to the owner; writes follow the
    /// scripted answers, then apply. Every call is recorded with the instant it arrived.
    /// </summary>
    private sealed class DetailApi((string Id, string Name, string Role)[] organizations)
    {
        private readonly object _gate = new();
        private readonly List<DetailCall> _calls = [];
        private readonly List<DetailOrder> _orders = [];
        private int _dashboardTooManyRequests;
        private bool _stopped;

        internal ConcurrentQueue<Answer> TransitionAnswers { get; } = new();
        internal ConcurrentQueue<Answer> AssignmentAnswers { get; } = new();
        internal ConcurrentQueue<Answer> IncidentAnswers { get; } = new();
        internal ConcurrentQueue<Answer> StopAnswers { get; } = new();

        /// <summary>The driver list from its second read on, one per read; the last one stays.</summary>
        internal ConcurrentQueue<object[]> DriverListsAfterFirst { get; } = new();

        internal object[] Drivers { get; set; } = [];
        internal object[] Incidents { get; set; } = [];
        internal object[] Notices { get; set; } = [];
        internal object[]? NoticesAfterStop { get; set; }
        internal bool WhatsAppConsent { get; set; }
        internal bool EmailOnFile { get; set; }
        internal string? TrackingValidUntil { get; set; }

        /// <summary>How many of the next dashboard reads answer 429 with Retry-After: 6.</summary>
        internal int DashboardTooManyRequests
        {
            set => Volatile.Write(ref _dashboardTooManyRequests, value);
        }

        internal IReadOnlyList<DetailCall> AllCalls
        {
            get
            {
                lock (_gate) return [.. _calls];
            }
        }

        internal DetailOrder Add(DetailOrder order)
        {
            lock (_gate) _orders.Add(order);
            return order;
        }

        internal DetailCall[] Calls(string organization, string method, string path) =>
            [.. AllCalls.Where(call => call.Organization == organization && call.Method == method && call.Path == path)];

        internal DetailCall[] OrderReads(string organization) =>
            [.. AllCalls.Where(call => call.Organization == organization && call.Method == "GET" &&
                call.Path.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["api", "v1", "orders", _])];

        internal DetailCall[] Transitions(string organization) =>
            [.. AllCalls.Where(call => call.Organization == organization && call.Method == "POST" && call.Path.EndsWith("/transitions", StringComparison.Ordinal))];

        internal int NoticeReads(string organization) => NoticeReadTimes(organization).Length;

        internal DateTimeOffset[] NoticeReadTimes(string organization) =>
            [.. AllCalls.Where(call => call.Organization == organization && call.Method == "GET" && call.Path.EndsWith("/recipient-notices", StringComparison.Ordinal)).Select(call => call.At)];

        internal async Task HandleAsync(IRoute route)
        {
            var at = DateTimeOffset.UtcNow;
            var request = route.Request;
            var uri = new Uri(request.Url);
            var headers = await request.AllHeadersAsync();
            var organization = headers.GetValueOrDefault("x-organization-id") ?? string.Empty;
            var call = new DetailCall(at, organization, request.Method, uri.AbsolutePath, uri.Query.TrimStart('?'),
                headers.GetValueOrDefault("idempotency-key"), request.PostData);
            lock (_gate) _calls.Add(call);
            var member = organizations.FirstOrDefault(entry => entry.Id == organization);
            var operations = member.Role is "DISPATCHER" or "PLATFORM_ADMIN";
            var segments = call.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (call.Method, segments)
            {
                case ("GET", ["api", "v1", "me", "organization-contexts"]):
                    await FulfillAsync(route, organizations.Select((entry, index) => new
                    {
                        organization_id = entry.Id,
                        display_name = entry.Name,
                        role = entry.Role,
                        is_default = index == 0,
                    }).ToArray());
                    return;
                case ("GET", ["api", "v1", "operations", "dashboard"]):
                    if (!operations)
                    {
                        await ProblemAsync(route, 403);
                    }
                    else if (Interlocked.Decrement(ref _dashboardTooManyRequests) >= 0)
                    {
                        await TooManyRequestsAsync(route);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _dashboardTooManyRequests, 0);
                        await DashboardAsync(route, call, member.Name);
                    }

                    return;
                case ("GET", ["api", "v1", "orders", var orderId]):
                    if (member.Role is not ("DISPATCHER" or "PLATFORM_ADMIN" or "VIEWER")) await ProblemAsync(route, 403);
                    else if (Visible(orderId, organization) is { } order) await FulfillAsync(route, Locked(() => order.Detail(organization)));
                    else await ProblemAsync(route, 404);
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "transitions"]):
                    await TransitionAsync(route, call, operations, orderId);
                    return;
                case ("GET", ["api", "v1", "incidents"]):
                    await IncidentsAsync(route, call, operations);
                    return;
                case ("GET", ["api", "v1", "orders", var orderId, "assignable-drivers"]):
                    if (!operations) await ProblemAsync(route, 403);
                    else if (Visible(orderId, organization) is null) await ProblemAsync(route, 404);
                    else await FulfillAsync(route, new { items = NextDrivers(), next_cursor = (string?)null });
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "assignments"]):
                    await AssignAsync(route, call, operations, orderId);
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "tracking-link"]):
                    if (!operations) await ProblemAsync(route, 403);
                    else if (Owned(orderId, organization) is null) await ProblemAsync(route, 404);
                    else await FulfillAsync(route, new { generation = 1, order_id = orderId, token = Token, token_id = TokenId, url = TrackingUrl, valid_until = TrackingValidUntil });
                    return;
                case ("GET", ["api", "v1", "orders", var orderId, "recipient-notices"]):
                    if (!operations) await ProblemAsync(route, 403);
                    else if (Owned(orderId, organization) is null) await ProblemAsync(route, 404);
                    else await FulfillAsync(route, new
                    {
                        items = Locked(() => _stopped && NoticesAfterStop is not null ? NoticesAfterStop : Notices),
                        whatsapp_consent_attested = WhatsAppConsent,
                        email_on_file = EmailOnFile,
                    });
                    return;
                case ("POST", ["api", "v1", "orders", var orderId, "recipient-notices", "stop"]):
                    if (!operations) await ProblemAsync(route, 403);
                    else if (Owned(orderId, organization) is null) await ProblemAsync(route, 404);
                    else if (StopAnswers.TryDequeue(out var answer) && answer.Status != 200) await ProblemAsync(route, answer.Status, answer.Code);
                    else
                    {
                        lock (_gate) _stopped = true;
                        await FulfillAsync(route, new { order_id = orderId, channels = new[] { "WHATSAPP", "EMAIL" }, recorded_at = "2026-11-03T05:21:00Z" });
                    }

                    return;
                default:
                    await ProblemAsync(route, 404);
                    return;
            }
        }

        private T Locked<T>(Func<T> read)
        {
            lock (_gate) return read();
        }

        private DetailOrder? Visible(string orderId, string organization) =>
            Locked(() => _orders.FirstOrDefault(order => order.Id == orderId && (order.Owner == organization || order.Operator == organization)));

        private DetailOrder? Owned(string orderId, string organization) =>
            Locked(() => _orders.FirstOrDefault(order => order.Id == orderId && order.Owner == organization));

        private object[] NextDrivers()
        {
            lock (_gate)
            {
                var first = !_calls.Take(_calls.Count - 1).Any(call => call.Path.EndsWith("/assignable-drivers", StringComparison.Ordinal));
                if (!first && DriverListsAfterFirst.TryDequeue(out var next)) Drivers = next;
                return Drivers;
            }
        }

        private async Task DashboardAsync(IRoute route, DetailCall call, string readerName)
        {
            // The order detail reads its own row: order_id and nothing else.
            var query = System.Web.HttpUtility.ParseQueryString(call.Query);
            if (query.Count != 1 || query["order_id"] is not { } orderId)
            {
                await ProblemAsync(route, 400);
                return;
            }

            var rows = Locked(() => _orders
                .Where(order => order.Id == orderId && (order.Owner == call.Organization || order.Operator == call.Organization))
                .Select(order => order.Row(call.Organization, readerName))
                .ToArray());
            await FulfillAsync(route, new { generated_at = GeneratedAt, items = rows, next_cursor = (string?)null });
        }

        private async Task TransitionAsync(IRoute route, DetailCall call, bool operations, string orderId)
        {
            var order = Owned(orderId, call.Organization);
            if (!operations)
            {
                await ProblemAsync(route, 403);
                return;
            }

            if (order is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            if (TransitionAnswers.TryDequeue(out var answer) && answer.Status != 200)
            {
                await ProblemAsync(route, answer.Status, answer.Code);
                return;
            }

            using var body = JsonDocument.Parse(call.Body ?? "{}");
            var target = body.RootElement.GetProperty("target_status").GetString()!;
            var expected = body.RootElement.GetProperty("expected_version").GetInt32();
            Dictionary<string, object?> applied;
            lock (_gate)
            {
                if (expected != order.Version || !order.Allowed.Any(item => item.Target == target))
                {
                    applied = [];
                }
                else
                {
                    order.Status = target;
                    order.Version += 1;
                    order.Allowed = order.AllowedAfter.GetValueOrDefault(target, []);
                    order.Timeline.Add(("ORDER_STATUS_CHANGED", "2026-11-03T05:25:00Z"));
                    order.OnTransition?.Invoke(order);
                    applied = order.Order();
                }
            }

            if (applied.Count == 0) await ProblemAsync(route, 409, "VERSION_CONFLICT");
            else await FulfillAsync(route, applied);
        }

        private async Task IncidentsAsync(IRoute route, DetailCall call, bool operations)
        {
            var query = System.Web.HttpUtility.ParseQueryString(call.Query);
            if (!operations)
            {
                await ProblemAsync(route, 403);
            }
            else if (query.Count != 1 || query["order_id"] is not { } orderId)
            {
                await ProblemAsync(route, 400);
            }
            else if (IncidentAnswers.TryDequeue(out var answer) && answer.Status != 200)
            {
                await ProblemAsync(route, answer.Status, answer.Code);
            }
            else
            {
                var visible = Visible(orderId, call.Organization) is not null;
                await FulfillAsync(route, new { items = visible ? Incidents : Array.Empty<object>(), next_cursor = (string?)null });
            }
        }

        private async Task AssignAsync(IRoute route, DetailCall call, bool operations, string orderId)
        {
            var order = Visible(orderId, call.Organization);
            if (!operations)
            {
                await ProblemAsync(route, 403);
                return;
            }

            if (order is null)
            {
                await ProblemAsync(route, 404);
                return;
            }

            if (AssignmentAnswers.TryDequeue(out var answer) && answer.Status != 201)
            {
                await ProblemAsync(route, answer.Status, answer.Code);
                return;
            }

            using var body = JsonDocument.Parse(call.Body ?? "{}");
            var driverId = body.RootElement.GetProperty("driver_id").GetString()!;
            var cost = body.RootElement.GetProperty("cost_cents").GetInt64();
            lock (_gate)
            {
                order.DriverId = driverId;
                order.Status = "ASSIGNED";
                order.Version += 1;
                order.Allowed = order.AllowedAfter.GetValueOrDefault("ASSIGNED", []);
            }

            await FulfillAsync(
                route,
                new
                {
                    id = AssignmentId,
                    order_id = orderId,
                    driver_id = driverId,
                    route_id = (string?)null,
                    status = "ACCEPTED",
                    cost = new { currency = "MXN", amount_cents = cost },
                },
                201);
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
    }
}
