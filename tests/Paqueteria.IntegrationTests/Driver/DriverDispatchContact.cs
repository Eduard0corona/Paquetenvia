using System.Text.Json;
using Drivers.Endpoints;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12: the driver PWA tests answer GET /api/v1/driver/me/dispatch-contact with the API's
/// own answer - <see cref="DriverDispatchContactResponse"/> serialized with the web JSON defaults - so the app reads
/// the shape the server really sends. The number is synthetic; no real number is used anywhere.
/// </summary>
internal static class DriverDispatchContact
{
    internal const string Route = "**/api/v1/driver/me/dispatch-contact";

    /// <summary>A synthetic Mexican number of the AI-05 form (+52, ten digits, the first one 2-9).</summary>
    internal const string SyntheticNumber = "+522000000001";

    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    /// <summary>The API's answer for an organization with <paramref name="phoneE164"/> (null: none configured).</summary>
    internal static string Answer(string? phoneE164) =>
        JsonSerializer.Serialize(new DriverDispatchContactResponse(phoneE164), ApiJson);

    /// <summary>Answers the page's dispatch contact read as the API does.</summary>
    internal static Task RouteAsync(IPage page, string? phoneE164)
    {
        var body = Answer(phoneE164);
        return page.RouteAsync(Route, route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json; charset=utf-8",
            Headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" },
            Body = body,
        }));
    }

    /// <summary>Waits, at most ten seconds, until the phone holds <paramref name="count"/> saved answers.</summary>
    internal static async Task<JsonElement[]> WaitForSavedAsync(IPage page, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var saved = await ReadSavedAsync(page);
            if (saved.Length == count)
            {
                return saved;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>The dispatch contacts the phone saved, one entry per partition, read from IndexedDB.</summary>
    internal static async Task<JsonElement[]> ReadSavedAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            """
            () => new Promise((resolve, reject) => {
              const open = indexedDB.open("paquetenvia-driver-stops-v1", 5);
              open.onerror = () => reject(open.error);
              open.onsuccess = () => {
                const database = open.result;
                const request = database.transaction("dispatch_contacts", "readonly")
                  .objectStore("dispatch_contacts").getAll();
                request.onerror = () => {
                  database.close();
                  reject(request.error);
                };
                request.onsuccess = () => {
                  database.close();
                  resolve(JSON.stringify(request.result));
                };
              };
            })
            """);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    /// <summary>
    /// The "Llamar a despacho" links on the page with their href and rendered size, so a test checks the tel: URI
    /// and the 44 px target of each.
    /// </summary>
    internal static async Task<(string Href, double Width, double Height)[]> LinksAsync(IPage page)
    {
        var links = page.GetByRole(AriaRole.Link, new() { Name = "Llamar a despacho", Exact = true });
        var result = new List<(string, double, double)>();
        for (var index = 0; index < await links.CountAsync(); index++)
        {
            var link = links.Nth(index);
            var box = await link.BoundingBoxAsync() ?? throw new InvalidOperationException("The link is not rendered.");
            result.Add(((await link.GetAttributeAsync("href")) ?? string.Empty, box.Width, box.Height));
        }

        return result.ToArray();
    }
}
