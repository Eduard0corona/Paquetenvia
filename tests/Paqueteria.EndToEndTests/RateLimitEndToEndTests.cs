using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Observability;
using Xunit.Abstractions;

namespace Paqueteria.EndToEndTests;

/// <summary>
/// AI-08 OPS-005, SEC-004 and the error leg of OBS-003, through the real "Nueva orden" screen against the assembled
/// API: a limited operation (<c>createQuote</c>, <c>Pricing.Quote</c>) answers 429 <c>application/problem+json</c>
/// with <c>Retry-After</c>, the wizard shows the approved text ("Demasiadas solicitudes. Espera N segundos e intenta de
/// nuevo.", owner answer Q6, SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12) with the server's N and holds
/// "Calcular precio", and the problem's <c>traceId</c> is the trace id of that request's API log lines.
/// </summary>
/// <remarks>
/// The limit is lowered for this test only through its validated settings (<c>RateLimits:Quote:PermitLimit</c> 1, range
/// 1 to 10000; <c>RateLimits:Quote:WindowSeconds</c> 45, range 1 to 3600): the first price of the user passes, the
/// second meets the limit. The window is not the default 60 s on purpose: without a usable <c>Retry-After</c> the panel
/// waits 60 s, so a wait of at most 45 s on screen can only come from the server's header. No production code has a
/// bypass.
/// </remarks>
[Collection(PlatformEndToEndCollection.Name)]
public sealed class RateLimitEndToEndTests(PlatformEndToEndFixture fixture, ITestOutputHelper output)
{
    /// <summary>Below the panel's 60 s fallback, so the countdown proves the web read the server's wait.</summary>
    private const int WindowSeconds = 45;

    [Fact]
    [Trait("Category", PlatformEndToEndCategory.Name)]
    public async Task A_limited_quote_answers_a_problem_with_Retry_After_and_the_wizard_shows_the_approved_text()
    {
        await using var hosts = PlatformHosts.Start(
            fixture,
            new Dictionary<string, string?>
            {
                ["RateLimits:Quote:PermitLimit"] = "1",
                ["RateLimits:Quote:WindowSeconds"] = WindowSeconds.ToString(CultureInfo.InvariantCulture),
            });
        await using var browser = await PlatformBrowser.LaunchAsync();
        var page = await browser.DispatcherAsync();
        var quotes = new ConcurrentQueue<IResponse>();
        page.Response += (_, response) =>
        {
            if (response.Request.Method == "POST" &&
                Uri.TryCreate(response.Url, UriKind.Absolute, out var uri) &&
                uri.GetLeftPart(UriPartial.Authority) == fixture.ApiOrigin &&
                uri.AbsolutePath == "/api/v1/quotes")
            {
                quotes.Enqueue(response);
            }
        };

        try
        {
            await OrderWizardScreen.OpenAsync(page, fixture.WebAddress);
            await OrderWizardScreen.FillWhereAsync(page, shipment: 2);
            await OrderWizardScreen.NextAsync(page, 2);
            await OrderWizardScreen.FillWhatAsync(page, shipment: 2);
            await OrderWizardScreen.NextAsync(page, 3);
            await OrderWizardScreen.ChooseSameDayAsync(page);
            await OrderWizardScreen.QuoteButton(page).ClickAsync();
            await page.GetByText("Precio calculado:", new() { Exact = false }).WaitForAsync();

            // A changed shipment needs a new price: the second "Calcular precio" is the user's second quote of the window.
            await OrderWizardScreen.BackAsync(page, 2);
            await OrderWizardScreen.FillWhatAsync(page, shipment: 2, weightGrams: 1500);
            await OrderWizardScreen.NextAsync(page, 3);
            await OrderWizardScreen.QuoteButton(page).ClickAsync();
            var countdown = page.Locator("main").GetByRole(AriaRole.Timer);
            await countdown.WaitForAsync();
            var shown = (await countdown.InnerTextAsync()).Trim();
            var heldButton = await OrderWizardScreen.QuoteButton(page).GetAttributeAsync("aria-disabled");

            var answers = await WaitForQuotesAsync(quotes, 2);
            Assert.Equal(201, answers[0].Status);
            var limited = answers[1];
            Assert.Equal(429, limited.Status);
            var headers = await limited.AllHeadersAsync();
            Assert.Equal("application/problem+json", headers["content-type"].Split(';')[0].Trim());
            Assert.Contains("no-store", headers["cache-control"], StringComparison.Ordinal);
            var retryAfter = int.Parse(headers["retry-after"], NumberStyles.None, CultureInfo.InvariantCulture);
            Assert.InRange(retryAfter, 1, WindowSeconds);
            using var problem = JsonDocument.Parse(await limited.TextAsync());
            Assert.Equal("about:blank", problem.RootElement.GetProperty("type").GetString());
            Assert.Equal("Too Many Requests", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
            var traceId = problem.RootElement.GetProperty("traceId").GetString();
            Assert.True(TraceCorrelation.IsTraceId(traceId), $"The problem traceId is not a W3C trace id: {traceId}");

            // The approved text with the server's wait; read right away, the countdown may already show one second less.
            Assert.Contains(shown, new[] { Approved(retryAfter), Approved(retryAfter - 1) });
            Assert.Equal("true", heldButton);
            output.WriteLine($"429 Retry-After={retryAfter}; the wizard shows: {shown}");

            // OBS-003, on an error: the problem's traceId is the trace id of every API log line of that one request.
            var scope = hosts.ApiLogs.RequestScopeOf(traceId!);
            var lines = hosts.ApiLogs.LinesOfRequestScope(scope);
            Assert.All(lines, line => Assert.Equal(traceId, JsonConsoleCapture.TraceIdOf(line)));
            var request = Assert.Single(ApiLogLines.HostingRequests(lines));
            Assert.Equal(("POST", "/api/v1/quotes"), (request.Method, request.Path));
            // The limiter answers before any handler: that request wrote no audit row.
            Assert.Equal(
                0L,
                await fixture.ScalarAsync<long>(
                    "SELECT count(*) FROM platform.audit_logs WHERE request_id=@trace",
                    ("trace", traceId!)));
            output.WriteLine($"trace: the problem traceId is on all {lines.Count} log lines of its request");
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            throw new InvalidOperationException(
                $"The 429 check failed: {exception.Message}{Environment.NewLine}" +
                $"browser errors: {string.Join(" | ", browser.Errors.Take(20))}{Environment.NewLine}" +
                $"pages:{Environment.NewLine}{await browser.DescribeAsync()}",
                exception);
        }
    }

    /// <summary>The panel's one text for a server wait (rateLimitMessage in apps/web/src/lib/rate-limit-wait.ts).</summary>
    private static string Approved(int seconds) =>
        Math.Max(1, seconds) == 1
            ? "Demasiadas solicitudes. Espera 1 segundo e intenta de nuevo."
            : $"Demasiadas solicitudes. Espera {seconds} segundos e intenta de nuevo.";

    private static async Task<IResponse[]> WaitForQuotesAsync(ConcurrentQueue<IResponse> quotes, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (quotes.Count < count)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"Expected {count} quote answers; saw {quotes.Count}.");
            await Task.Delay(50);
        }

        return quotes.ToArray();
    }
}
