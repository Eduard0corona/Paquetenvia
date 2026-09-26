using System.Net;
using System.Text.RegularExpressions;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// Security headers of the deployable web artifact (<c>next build</c> + <c>next start</c>):
/// every page, including the driver PWA, carries a nonce-based CSP without
/// <c>'unsafe-inline'</c>, every rendered script carries that nonce, and the synthetic
/// <c>/dev</c> portal is not part of the production build.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class WebSecurityHeadersTests(DriverStopsNextServerFixture server)
{
    [Theory]
    [Trait("Category", "DriverStopsPwa")]
    [InlineData("/driver/stops")]
    [InlineData("/driver/stops/22222222-2222-2222-2222-222222222222")]
    [InlineData("/ops/dashboard")]
    [InlineData("/track")]
    [InlineData("/")]
    public async Task Every_page_has_a_nonce_csp_and_static_security_headers(string path)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains(
            $"connect-src 'self' {DriverStopsNextServerFixture.TestApiOrigin}",
            csp,
            StringComparison.Ordinal);
        Assert.Contains(DriverStopsNextServerFixture.TestStorageOrigin, csp, StringComparison.Ordinal);
        var nonce = NoncePattern().Match(csp).Groups["nonce"].Value;
        Assert.False(string.IsNullOrEmpty(nonce));

        var scripts = ScriptTagPattern().Matches(html);
        Assert.NotEmpty(scripts);
        Assert.All(scripts, script => Assert.Contains($"nonce=\"{nonce}\"", script.Value, StringComparison.Ordinal));

        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("camera=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Each_response_gets_a_fresh_nonce()
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var first = await client.GetAsync("/driver/stops");
        using var second = await client.GetAsync("/driver/stops");

        Assert.NotEqual(
            NoncePattern().Match(Assert.Single(first.Headers.GetValues("Content-Security-Policy"))).Value,
            NoncePattern().Match(Assert.Single(second.Headers.GetValues("Content-Security-Policy"))).Value);
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public async Task Production_build_does_not_contain_the_dev_portal()
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var response = await client.GetAsync("/dev");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(
            "local-dispatcher-mfa",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "DriverStopsPwa")]
    public void Production_build_output_contains_no_dev_portal_code_or_credentials()
    {
        // The fixture built this .next with `next build` (production, no opt-in).
        var build = Path.Combine(DriverStopsNextServer.FindRepositoryRoot(), "apps", "web", ".next");
        Assert.True(Directory.Exists(build));
        string[] markers = ["local-dispatcher-mfa", "Synthetic dispatcher (MFA)", "local-driver-session"];
        var leaks = new List<string>();
        foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(build, file);
            if (relative.StartsWith("cache", StringComparison.Ordinal) ||
                relative.StartsWith("dev", StringComparison.Ordinal) ||
                new FileInfo(file).Length > 16 * 1024 * 1024)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            leaks.AddRange(markers
                .Where(marker => text.Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"{relative}: {marker}"));
        }

        Assert.Empty(leaks);
        Assert.DoesNotContain(
            "\"/dev/page\"",
            File.ReadAllText(Path.Combine(build, "server", "app-paths-manifest.json")),
            StringComparison.Ordinal);
    }

    [GeneratedRegex("'nonce-(?<nonce>[A-Za-z0-9+/=]+)'")]
    private static partial Regex NoncePattern();

    [GeneratedRegex("<script\\b[^>]*>")]
    private static partial Regex ScriptTagPattern();
}
