using System.Net;
using System.Text.RegularExpressions;
using Paqueteria.IntegrationTests.Driver;

namespace Paqueteria.IntegrationTests.Tracking;

/// <summary>
/// GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10 on the deployable web artifact (<c>next build</c> +
/// <c>next start</c>): every public tracking page, and the not-found page a malformed tracking link
/// reaches, has the neutral title "Seguimiento de envío" or "Página no encontrada", never names the
/// unvalidated commercial name and links no driver PWA manifest.
/// </summary>
[Collection(DriverStopsPwaCollection.Name)]
public sealed partial class PublicTrackingNeutralBrandTests(DriverStopsNextServerFixture server)
{
    private const string Token = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";
    private const string Heading = "Seguimiento de envío";

    [Theory]
    [Trait("Category", "DriverStopsPwa")]
    [InlineData("/track", HttpStatusCode.OK, Heading)]
    [InlineData("/track/" + Token, HttpStatusCode.OK, Heading)]
    [InlineData("/track/not-valid", HttpStatusCode.OK, Heading)]
    [InlineData("/track/" + Token + "/extra", HttpStatusCode.NotFound, "Página no encontrada")]
    public async Task Public_tracking_pages_show_the_neutral_heading_and_no_brand(
        string path,
        HttpStatusCode status,
        string title)
    {
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            title,
            WebUtility.HtmlDecode(Assert.Single(TitlePattern().Matches(html)).Groups["title"].Value));
        Assert.DoesNotMatch(ManifestLinkPattern(), html);
        Assert.DoesNotContain("paquetenv", html, StringComparison.OrdinalIgnoreCase);
        if (status == HttpStatusCode.OK)
        {
            Assert.Contains($"<p class=\"trackingHeading\">{Heading}</p>", html, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex("<title>(?<title>[^<]*)</title>")]
    private static partial Regex TitlePattern();

    [GeneratedRegex("<link\\b[^>]*\\brel=\"manifest\"")]
    private static partial Regex ManifestLinkPattern();
}
