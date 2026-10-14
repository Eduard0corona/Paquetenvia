using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>
/// "Nueva orden" (<c>/ops/orders/new</c>, UI-PHASE3-ORDER-WIZARD-2026-10-10) driven as a dispatcher fills it: the
/// controls are found by the ids the wizard gives each field (<c>fieldControlId</c>) and the buttons by their names.
/// Every value is synthetic (SEC-003): the addresses, names and descriptions say so, and the phone numbers start with
/// 0000, which no Mexican number does.
/// </summary>
internal static class OrderWizardScreen
{
    internal static async Task OpenAsync(IPage page, Uri webAddress)
    {
        await page.GotoAsync(new Uri(webAddress, "/ops/orders/new").AbsoluteUri);
        await StepAsync(page, 1);
    }

    /// <summary>Step 1 "Dónde", origin and destination with exact coordinates (a DISPATCHER handles them, D5).</summary>
    internal static async Task FillWhereAsync(IPage page, int shipment)
    {
        await FillAddressAsync(
            page,
            "origin",
            $"OPS-005 SINTETICO ORIGEN {shipment:D2}, CALLE FICTICIA 1, COLONIA PRUEBA",
            $"OPS005 REMITENTE {shipment:D2}",
            $"00000501{shipment:D2}",
            "24.8000",
            "-107.4000");
        await FillAddressAsync(
            page,
            "destination",
            $"OPS-005 SINTETICO DESTINO {shipment:D2}, CALLE FICTICIA 2, COLONIA PRUEBA",
            $"OPS005 DESTINATARIO {shipment:D2}",
            $"00000502{shipment:D2}",
            "24.8100",
            "-107.3900");
    }

    /// <summary>
    /// Step 2 "Qué se envía": one synthetic package with its measurements, typed as a dispatcher can. The measurements
    /// are optional on the screen; an order whose package has none is assignable too since #265 (a JSON null in
    /// <c>dimensions_mm</c> reads as no measurement in the Dispatch, Routing and Orders readers). The journey keeps
    /// typing them because it is a valid dispatcher path.
    /// </summary>
    internal static async Task FillWhatAsync(IPage page, int shipment, int weightGrams = 1000)
    {
        await page.Locator("#order-packages-0-description").FillAsync($"OPS-005 PAQUETE SINTETICO {shipment:D2}");
        await page.Locator("#order-packages-0-weightGrams").FillAsync(weightGrams.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.Locator("#order-packages-0-declaredValue").FillAsync("100.00");
        await page.Locator("#order-packages-0-dimensions").FillAsync("300");
        await page.Locator("#order-packages-0-width").FillAsync("200");
        await page.Locator("#order-packages-0-height").FillAsync("150");
    }

    /// <summary>Step 3 "Servicio y precio": same day, then "Calcular precio".</summary>
    internal static async Task ChooseSameDayAsync(IPage page) =>
        await page.Locator("#order-serviceType").SelectOptionAsync("SAME_DAY");

    internal static ILocator QuoteButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Calcular precio", Exact = true });

    internal static async Task NextAsync(IPage page, int expectedStep)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Siguiente", Exact = true }).ClickAsync();
        await StepAsync(page, expectedStep);
    }

    internal static async Task BackAsync(IPage page, int expectedStep)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Atrás", Exact = true }).ClickAsync();
        await StepAsync(page, expectedStep);
    }

    private static Task StepAsync(IPage page, int step) =>
        page.Locator("#order-step-title").GetByText($"Paso {step} de 4:", new() { Exact = false }).WaitForAsync();

    private static async Task FillAddressAsync(
        IPage page,
        string side,
        string address,
        string contact,
        string phone,
        string latitude,
        string longitude)
    {
        await page.Locator($"#order-{side}-addressText").FillAsync(address);
        await page.Locator($"#order-{side}-contactName").FillAsync(contact);
        await page.Locator($"#order-{side}-phone").FillAsync(phone);
        await page.Locator($"#order-{side}-coordinates").FillAsync(latitude);
        await page.Locator($"#order-{side}-longitude").FillAsync(longitude);
        await page.Locator($"#order-{side}-references").FillAsync("OPS-005 SYNTHETIC FIXTURE");
    }
}
