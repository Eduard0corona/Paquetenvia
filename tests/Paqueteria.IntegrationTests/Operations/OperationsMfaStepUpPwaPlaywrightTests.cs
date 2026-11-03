using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Operations;

/// <summary>
/// Z19 ("Ofrecer Verificar identidad (Recommended)", AUTH-001-MFA-STEP-UP) in a real browser against the production Next
/// build, for the two screens besides the inbox that read through the operations dashboard client: the operations
/// dashboard (<c>/ops/dashboard</c>) and the order detail (<c>/ops/orders/:id</c>). A PLATFORM_ADMIN whose session lacks
/// the second factor gets 403 MFA_REQUIRED from every read: the screen shows "Esta acción requiere verificar tu
/// identidad (MFA)." with "Verificar identidad", which returns to the same view, and no order data. Any other 403 still
/// shows "Acceso no disponible" without the link. The inbox's own case is in WorkInboxPwaPlaywrightTests. The API is
/// fulfilled in the browser: /me/organization-contexts with AI-05 shapes, and the problem bodies of every other read.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed class OperationsMfaStepUpPwaPlaywrightTests(DriverStopsNextServerFixture server)
{
    private const string OrganizationA = "11111111-1111-1111-1111-111111111111";
    private const string OrderId = "a1000000-0000-4000-8000-000000000001";

    private static readonly Regex[] AllowedCalls =
    [
        new("^GET /api/v1/me/organization-contexts$"),
        new("^GET /api/v1/operations/dashboard$"),
        new("^GET /api/v1/operations/queue-counts$"),
        new("^GET /api/v1/orders/[0-9a-f-]{36}$"),
        new("^POST /hubs/operations/negotiate$"),
    ];

    [Theory]
    [Trait("Category", "OperationsDashboardPwa")]
    [InlineData("dashboard", true)]
    [InlineData("dashboard", false)]
    [InlineData("order", true)]
    [InlineData("order", false)]
    public async Task A_403_offers_Verificar_identidad_only_when_the_second_factor_is_missing(string screen, bool mfaRequired)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });
        await using var trace = await PlaywrightFailureTrace.StartAsync(
            context, $"{screen}-{(mfaRequired ? "mfa" : "forbidden")}", PlaywrightFailureTrace.OperationsDashboardJob);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var console = CaptureConsole(page);
        var calls = RecordCalls(page);
        await InstallAsync(page, mfaRequired);

        // The order detail opened from the inbox keeps that view; "Verificar identidad" comes back to the same URL.
        var path = screen == "dashboard"
            ? "/ops/dashboard"
            : $"/ops/orders/{OrderId}?inbox=queue%3Dneeds_attention";
        await page.GotoAsync(new Uri(server.BaseAddress, path).AbsoluteUri);
        var stepUp = page.GetByRole(AriaRole.Link, new() { Name = "Verificar identidad", Exact = true });
        var accessUnavailable = page.GetByRole(AriaRole.Heading, new() { Name = "Acceso no disponible", Exact = true });
        if (mfaRequired)
        {
            await page.Locator("main").GetByRole(AriaRole.Status)
                .GetByText("Esta acción requiere verificar tu identidad (MFA).", new() { Exact = false })
                .WaitForAsync();
            Assert.Equal($"/login?mfa=required&return_url={Uri.EscapeDataString(path)}", await stepUp.GetAttributeAsync("href"));
            Assert.Equal(0, await accessUnavailable.CountAsync());
        }
        else
        {
            await accessUnavailable.WaitForAsync();
            Assert.Equal(0, await stepUp.CountAsync());
        }

        await Task.Delay(500);
        var main = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("ORD_", main, StringComparison.Ordinal);
        Assert.DoesNotContain(OrderId, main, StringComparison.Ordinal);
        Assert.Equal(0, await page.Locator("main section.opsDriverAssignment").CountAsync());

        await AssertNothingStoredAsync(page);
        Assert.Empty(console);
        AssertOnlyAllowedCalls(calls, server.BaseAddress);
        trace.Passed();
    }

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

    private static void AssertOnlyAllowedCalls(CallRecorder calls, Uri web)
    {
        Assert.All(calls.Api, call => Assert.Contains(AllowedCalls, pattern => pattern.IsMatch($"{call.Method} {call.Path}")));
        var webOrigin = web.GetLeftPart(UriPartial.Authority) + "/";
        Assert.DoesNotContain(calls.Foreign, url => !url.StartsWith(webOrigin, StringComparison.Ordinal));
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
    /// The in-memory operations session of a PLATFORM_ADMIN (Mock mode, as the other panel suites). Every API read but
    /// the role read answers 403: with the MFA_REQUIRED code when only the second factor is missing, without it
    /// otherwise. Playwright runs the last matching route first, so the catch-all is registered before the role read.
    /// </summary>
    private static async Task InstallAsync(IPage page, bool mfaRequired)
    {
        await page.AddInitScriptAsync(
            $$"""
            window.__paquetenviaOperationsSession = {
              organizationId: {{JsonSerializer.Serialize(OrganizationA)}},
              sessionNamespace: "ui-001-mfa-step-up-playwright",
              getAccessToken: () => "synthetic-operations-token",
            };
            """);
        await page.RouteAsync("**/api/v1/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 403,
            ContentType = "application/problem+json",
            Body = mfaRequired
                ? JsonSerializer.Serialize(new { type = "about:blank", title = "Forbidden", status = 403, code = "MFA_REQUIRED" })
                : JsonSerializer.Serialize(new { type = "about:blank", title = "Forbidden", status = 403 }),
        }));
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.RouteAsync("**/api/v1/me/organization-contexts**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "application/json",
            Body = JsonSerializer.Serialize(new[]
            {
                new { organization_id = OrganizationA, display_name = "Logística Sintética", role = "PLATFORM_ADMIN", is_default = true },
            }),
        }));
    }

    private sealed class CallRecorder
    {
        internal ConcurrentQueue<(string Method, string Path)> Api { get; } = new();
        internal ConcurrentQueue<string> Foreign { get; } = new();
    }
}
