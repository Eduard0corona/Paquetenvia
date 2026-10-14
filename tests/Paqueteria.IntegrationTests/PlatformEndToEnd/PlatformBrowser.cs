using System.Collections.Concurrent;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.PlatformEndToEnd;

/// <summary>
/// Chromium (the pre-installed Playwright browser) with one context per person of the journey, each with its own
/// storage. The contexts ignore TLS errors only because the API and the storage tunnel listen on loopback TLS with a
/// certificate generated in memory for this test process. Sign-in uses the Mock identity provider's bearer sessions
/// (<c>active-dispatcher</c>, <c>active-driver</c>), installed before the page loads exactly as the existing browser
/// suites do; the AuthCenter BFF is outside OPS-005.
/// </summary>
internal sealed class PlatformBrowser : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly List<IPage> _pages = [];

    private PlatformBrowser(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        _browser = browser;
    }

    /// <summary>Page errors and console errors of every page, kept for the failure message.</summary>
    internal IReadOnlyCollection<string> Errors => _errors.ToArray();

    /// <summary>
    /// For a failure message: each page's path and the start of its visible text. The public tracking path carries the
    /// tracking token, so it is never written.
    /// </summary>
    internal async Task<string> DescribeAsync()
    {
        var parts = new List<string>();
        foreach (var page in _pages)
        {
            try
            {
                var path = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : string.Empty;
                if (path.StartsWith("/track/", StringComparison.Ordinal))
                {
                    path = "/track/[redacted]";
                }

                var text = await page.Locator("body").InnerTextAsync(new() { Timeout = 2_000 });
                text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                parts.Add($"{path}: {(text.Length > 700 ? text[..700] : text)}");
            }
            catch (PlaywrightException exception)
            {
                parts.Add($"(page unreadable: {exception.GetType().Name})");
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    internal static async Task<PlatformBrowser> LaunchAsync()
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return new PlatformBrowser(playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    /// <summary>The dispatcher, on a desktop viewport, signed in to the synthetic organization.</summary>
    internal async Task<IPage> DispatcherAsync()
    {
        var page = await PageAsync(new ViewportSize { Width = 1280, Height = 900 });
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(PlatformEndToEndFixture.OrganizationId.ToString("D"))}},
              sessionNamespace: "ops005-dispatcher",
              getAccessToken: () => {{JsonSerializer.Serialize(MockIdentityProfiles.ActiveDispatcher)}},
            };
            """);
        return page;
    }

    /// <summary>The driver, on a phone viewport, signed in to the driver PWA of the synthetic organization.</summary>
    internal async Task<IPage> DriverAsync()
    {
        var page = await PageAsync(new ViewportSize { Width = 390, Height = 844 });
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaDriverSession = {
              organizationId: {{JsonSerializer.Serialize(PlatformEndToEndFixture.OrganizationId.ToString("D"))}},
              cacheNamespace: "ops005-driver-synthetic-01",
              getAccessToken: () => {{JsonSerializer.Serialize(MockIdentityProfiles.ActiveDriver)}},
            };
            """);
        return page;
    }

    /// <summary>A recipient with the public link only: no session.</summary>
    internal Task<IPage> RecipientAsync() => PageAsync(new ViewportSize { Width = 390, Height = 844 });

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _browser.DisposeAsync();
        }
        finally
        {
            _playwright.Dispose();
        }
    }

    private async Task<IPage> PageAsync(ViewportSize viewport)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = viewport,
            IgnoreHTTPSErrors = true,
            Locale = "es-MX",
        });
        var page = await context.NewPageAsync();
        _pages.Add(page);
        page.SetDefaultTimeout(30_000);
        page.PageError += (_, error) => _errors.Enqueue($"page: {error}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                _errors.Enqueue($"console: {message.Text}");
            }
        };
        return page;
    }
}
