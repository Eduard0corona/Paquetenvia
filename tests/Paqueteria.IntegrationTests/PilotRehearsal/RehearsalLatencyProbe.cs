using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.PlatformEndToEnd;

namespace Paqueteria.IntegrationTests.PilotRehearsal;

/// <summary>Z8: the critical screens of the 2-second limit, in AI-24 <c>pilot_readiness.latency.critical_screens</c> order.</summary>
internal static class RehearsalScreens
{
    internal static readonly string[] Critical =
        ["/ops/inbox", "/ops/orders/:id", "/driver/stops", "/driver/stops/:id", "/track/:token"];
}

/// <summary>
/// Z7 and Z8, measured in Chromium against the production web build: each sample is a full navigation, timed from the
/// navigation start to the first authoritative render of the screen, the element that only exists once the screen's
/// REST answer is drawn (a row, a stop or the order of a known public id, or the public tracking status). Order creation
/// is timed from the "Crear y confirmar orden" click to "Orden creada y confirmada" (createOrder and the CONFIRMED
/// transition), so the quote and its geocoding (external providers, excluded by AI-02) are left out.
/// </summary>
internal sealed class RehearsalLatencyProbe(PlatformEndToEndFixture fixture, PlatformBrowser browser)
{
    internal async Task<LatencySummary> InboxAsync(string unassignedPublicId, int samples)
    {
        var page = await browser.DispatcherAsync();
        return await MeasureAsync(page, "/ops/inbox", samples, async () =>
        {
            await page.Locator("div.page[aria-busy='false'] table.opsInboxTable tbody tr")
                .Filter(new() { HasText = unassignedPublicId }).First.WaitForAsync();
        });
    }

    internal async Task<LatencySummary> OrderDetailAsync(Guid orderId, string publicId, int samples)
    {
        var page = await browser.DispatcherAsync();
        return await MeasureAsync(page, $"/ops/orders/{orderId:D}", samples, async () =>
        {
            await page.Locator("div.page[aria-busy='false']")
                .GetByRole(AriaRole.Heading, new() { Name = publicId, Exact = true }).First.WaitForAsync();
        });
    }

    internal async Task<LatencySummary> DriverStopsAsync(string assignedPublicId, int samples)
    {
        var page = await browser.DriverAsync();
        return await MeasureAsync(page, "/driver/stops", samples, async () =>
        {
            await page.Locator("main[aria-busy='false'] li").Filter(new() { HasText = assignedPublicId }).First.WaitForAsync();
        });
    }

    internal async Task<LatencySummary> StopDetailAsync(Guid orderId, string publicId, int samples)
    {
        var page = await browser.DriverAsync();
        return await MeasureAsync(page, $"/driver/stops/{orderId:D}", samples, async () =>
        {
            await page.Locator("main[aria-busy='false']").GetByText(publicId, new() { Exact = true }).First.WaitForAsync();
        });
    }

    /// <summary>The public link opened by a recipient without a session; the token never leaves this method.</summary>
    internal async Task<LatencySummary> TrackingAsync(Uri trackingLink, int samples)
    {
        var page = await browser.RecipientAsync();
        return await MeasureAsync(page, trackingLink.PathAndQuery, samples, async () =>
        {
            await page.Locator(".trackingShell .trackingStatus").First.WaitForAsync();
        });
    }

    /// <summary>The "Nueva orden" wizard <paramref name="samples"/> times; only the create-and-confirm step is timed.</summary>
    internal async Task<(LatencySummary Summary, IReadOnlyList<Guid> Orders)> OrderCreationAsync(int samples)
    {
        var page = await browser.DispatcherAsync();
        var durations = new List<TimeSpan>(samples);
        var orders = new List<Guid>(samples);
        for (var sample = 0; sample < samples; sample++)
        {
            await OrderWizardScreen.OpenAsync(page, fixture.WebAddress);
            // The wizard's synthetic phones take a two-digit shipment number (0000 numbers, SEC-003).
            var shipment = sample % 99 + 1;
            await OrderWizardScreen.FillWhereAsync(page, shipment);
            await OrderWizardScreen.NextAsync(page, 2);
            await OrderWizardScreen.FillWhatAsync(page, shipment);
            await OrderWizardScreen.NextAsync(page, 3);
            await OrderWizardScreen.ChooseSameDayAsync(page);
            await OrderWizardScreen.QuoteButton(page).ClickAsync();
            await page.GetByText("Precio calculado:", new() { Exact = false }).WaitForAsync();
            await OrderWizardScreen.NextAsync(page, 4);
            await page.Locator("#order-payerType").SelectOptionAsync("SENDER");
            await page.Locator("#order-accepted").CheckAsync();
            await page.Locator("#order-restrictedGoodsAcknowledged").CheckAsync();
            var create = page.GetByRole(AriaRole.Button, new() { Name = "Crear y confirmar orden", Exact = true });
            var started = Stopwatch.GetTimestamp();
            await create.ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Orden creada y confirmada", Exact = true }).WaitForAsync();
            durations.Add(Stopwatch.GetElapsedTime(started));
            await page.GetByRole(AriaRole.Link, new() { Name = "Abrir orden", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(new Regex("/ops/orders/[0-9a-f-]{36}$", RegexOptions.CultureInvariant));
            orders.Add(Guid.Parse(new Uri(page.Url).Segments[^1]));
        }

        return (LatencySummary.From(durations), orders);
    }

    private async Task<LatencySummary> MeasureAsync(IPage page, string path, int samples, Func<Task> rendered)
    {
        var address = new Uri(fixture.WebAddress, path).AbsoluteUri;
        var durations = new List<TimeSpan>(samples);
        for (var sample = 0; sample < samples; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            await rendered();
            durations.Add(Stopwatch.GetElapsedTime(started));
        }

        return LatencySummary.From(durations);
    }
}

/// <summary>The ASP.NET Core hosting "Request finished" lines (event 2) of the API: each request's elapsed time.</summary>
internal static class HostingDurations
{
    internal static IReadOnlyList<TimeSpan> Read(IEnumerable<string> lines)
    {
        var durations = new List<TimeSpan>();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("Category", out var category) &&
                category.GetString() == "Microsoft.AspNetCore.Hosting.Diagnostics" &&
                root.TryGetProperty("EventId", out var eventId) &&
                eventId.ValueKind == JsonValueKind.Number &&
                eventId.GetInt32() == 2 &&
                root.TryGetProperty("State", out var state) &&
                state.ValueKind == JsonValueKind.Object &&
                state.TryGetProperty("ElapsedMilliseconds", out var elapsed) &&
                elapsed.ValueKind == JsonValueKind.Number)
            {
                durations.Add(TimeSpan.FromMilliseconds(elapsed.GetDouble()));
            }
        }

        return durations;
    }
}
