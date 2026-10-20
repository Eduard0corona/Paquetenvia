using System.Text.Json;
using Microsoft.Playwright;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// The AuthCenter BFF contract as the driver PWA reads it in the pilot (<c>NEXT_PUBLIC_AUTH_MODE=bff</c>), fulfilled in
/// the browser on the page's own origin, the way the Mock suites fulfil the API: <c>GET /auth/session</c>
/// (AuthCenterBffEndpoints.Session, no token) and <c>GET /api/v1/me/organization-contexts</c>. The server half of the
/// contract (cookie, CSRF, OIDC with FakeAuthCenterServer) is covered by the AUTH-001 suites; these suites cover what the
/// page does with the answers.
/// </summary>
internal static class DriverBffSession
{
    internal const string OrganizationId = "11111111-1111-4111-8111-111111111111";

    /// <summary>Synthetic session-bound CSRF token (the API's own pattern, 32 to 256 Base64URL characters).</summary>
    internal const string CsrfToken = "synthetic-bff-csrf-token-0123456789abcdef";

    internal const string SessionRoute = "**/auth/session";
    internal const string OrganizationContextsRoute = "**/api/v1/me/organization-contexts";

    /// <summary>
    /// A browser for a BFF suite only: it ignores the TLS error of the process-local loopback certificate that
    /// <see cref="DriverStopsBffNextServerFixture"/> serves the page with. The context option covers the page's own
    /// requests; the Service Worker script is fetched by the browser itself, which needs the launch flag.
    /// </summary>
    internal static Task<IBrowser> LaunchAsync(IPlaywright playwright) =>
        playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--ignore-certificate-errors"],
        });

    internal static Task<IBrowserContext> NewContextAsync(IBrowser browser) =>
        browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });

    /// <summary>
    /// Registered first, so the routes of each test take precedence: a same-origin API, hub or BFF request the test did
    /// not fulfil is blocked instead of reaching the Next server, and the assertions that depend on it then fail.
    /// </summary>
    internal static async Task BlockUnfulfilledAsync(IPage page, Uri origin)
    {
        var authority = origin.GetLeftPart(UriPartial.Authority);
        await page.RouteAsync($"{authority}/api/**", route => route.AbortAsync("blockedbyclient"));
        await page.RouteAsync($"{authority}/auth/**", route => route.AbortAsync("blockedbyclient"));
        await page.RouteAsync($"{authority}/hubs/**", route => route.AbortAsync());
    }

    /// <summary>
    /// The person signed in as <paramref name="sessionNamespace"/> (the API's per-identity cache key), a DRIVER of
    /// <paramref name="organizationId"/>: the session answer and that one organization context.
    /// </summary>
    internal static async Task RouteSignedInAsync(
        IPage page,
        string sessionNamespace,
        string organizationId = OrganizationId)
    {
        await RouteSessionAsync(page, () => SessionAnswer.SignedIn(sessionNamespace));
        await RouteOrganizationContextsAsync(page, organizationId);
    }

    /// <summary><c>GET /auth/session</c> answered by <paramref name="answer"/> on every read.</summary>
    internal static Task RouteSessionAsync(IPage page, Func<SessionAnswer> answer) =>
        page.RouteAsync(SessionRoute, route =>
        {
            var current = answer();
            return route.FulfillAsync(current.SessionNamespace is { } sessionNamespace
                ? Json(SessionBody(sessionNamespace))
                : new RouteFulfillOptions
                {
                    Status = 401,
                    ContentType = "application/problem+json",
                    Body = """{"type":"about:blank","title":"Unauthorized","status":401}""",
                });
        });

    internal static Task RouteOrganizationContextsAsync(IPage page, string organizationId = OrganizationId) =>
        page.RouteAsync(OrganizationContextsRoute, route => route.FulfillAsync(Json(JsonSerializer.Serialize(new[]
        {
            new
            {
                organization_id = organizationId,
                display_name = "Organización sintética",
                role = "DRIVER",
                is_default = true,
            },
        }))));

    /// <summary>AuthCenterBffEndpoints.Session: no token, the CSRF token and the per-identity namespace only.</summary>
    internal static string SessionBody(string sessionNamespace) => JsonSerializer.Serialize(new
    {
        authenticated = true,
        authorized = true,
        mfa = false,
        csrfToken = CsrfToken,
        sessionNamespace,
        user = new { name = "Repartidor Sintético", email = (string?)null },
    });

    internal static RouteFulfillOptions Json(string body) => new()
    {
        Status = 200,
        ContentType = "application/json; charset=utf-8",
        Body = body,
    };

    /// <summary>What <c>GET /auth/session</c> answers: a signed-in person, or 401 (no session or it ended).</summary>
    internal sealed record SessionAnswer(string? SessionNamespace)
    {
        internal static SessionAnswer SignedIn(string sessionNamespace) => new(sessionNamespace);

        internal static readonly SessionAnswer Anonymous = new((string?)null);
    }
}
