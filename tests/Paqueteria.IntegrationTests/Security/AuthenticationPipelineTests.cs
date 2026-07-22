using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Paqueteria.IntegrationTests.Security;

public sealed class AuthenticationPipelineTests : IClassFixture<SecurityWebApplicationFactory>
{
    private const string AuthenticatedProbe = "/__tests/security/authenticated";
    private const string PrivilegedProbe = "/__tests/security/privileged";
    private const string HubNegotiate = "/__tests/hubs/security/negotiate?negotiateVersion=1";

    private readonly HttpClient _client;

    public AuthenticationPipelineTests(SecurityWebApplicationFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task Health_remains_anonymous_when_mock_authentication_is_enabled()
    {
        using var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_provider_fails_closed_with_401_instead_of_500()
    {
        using var factory = new EnvironmentWebApplicationFactory("Testing", "Disabled");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(AuthenticatedProbe);

        await AssertGenericProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_token_returns_generic_problem_details_401()
    {
        using var response = await _client.GetAsync(AuthenticatedProbe);

        await AssertGenericProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Malformed_bearer_returns_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthenticatedProbe);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token with spaces");

        using var response = await _client.SendAsync(request);

        await AssertGenericProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_bearer_returns_401_without_disclosing_the_credential()
    {
        using var response = await SendAsync(HttpMethod.Get, AuthenticatedProbe, "not-a-profile");

        await AssertGenericProblemAsync(response, HttpStatusCode.Unauthorized, "not-a-profile");
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer, HttpStatusCode.NoContent)]
    [InlineData(MockIdentityProfiles.SuspendedUser, HttpStatusCode.Forbidden)]
    [InlineData(MockIdentityProfiles.DisabledUser, HttpStatusCode.Forbidden)]
    public async Task Active_identity_is_allowed_and_inactive_identity_is_forbidden(
        string credential,
        HttpStatusCode expected)
    {
        using var response = await SendAsync(HttpMethod.Get, AuthenticatedProbe, credential);

        Assert.Equal(expected, response.StatusCode);
        AssertNoSessionCookie(response);
        if (expected == HttpStatusCode.Forbidden)
        {
            await AssertGenericProblemAsync(response, expected, credential);
        }
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, HttpStatusCode.NoContent)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, HttpStatusCode.Forbidden)]
    [InlineData(MockIdentityProfiles.ActiveViewer, HttpStatusCode.Forbidden)]
    public async Task Privileged_probe_requires_platform_admin_and_mfa(
        string credential,
        HttpStatusCode expected)
    {
        using var response = await SendAsync(HttpMethod.Get, PrivilegedProbe, credential);

        Assert.Equal(expected, response.StatusCode);
        AssertNoSessionCookie(response);
    }

    [Fact]
    public async Task Organization_authorization_keeps_multi_org_roles_separate()
    {
        var viewerPath = OrganizationProbe(MockIdentityProfiles.ViewerOrganizationId);
        var dispatcherPath = OrganizationProbe(MockIdentityProfiles.OperationsOrganizationId);
        var foreignPath = OrganizationProbe(MockIdentityProfiles.ForeignOrganizationId);

        using var viewer = await SendAsync(
            HttpMethod.Get,
            viewerPath,
            MockIdentityProfiles.ActiveMultiOrganization);
        using var dispatcher = await SendAsync(
            HttpMethod.Get,
            dispatcherPath,
            MockIdentityProfiles.ActiveMultiOrganization);
        using var foreign = await SendAsync(
            HttpMethod.Get,
            foreignPath,
            MockIdentityProfiles.ActiveMultiOrganization);

        Assert.Equal(HttpStatusCode.NoContent, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dispatcher.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.SuspendedMembership)]
    [InlineData(MockIdentityProfiles.RevokedMembership)]
    public async Task Inactive_memberships_return_403(string credential)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            OrganizationProbe(MockIdentityProfiles.ViewerOrganizationId),
            credential);

        await AssertGenericProblemAsync(response, HttpStatusCode.Forbidden, credential);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("not-a-profile", HttpStatusCode.Unauthorized)]
    [InlineData(MockIdentityProfiles.SuspendedUser, HttpStatusCode.Forbidden)]
    [InlineData(MockIdentityProfiles.ActiveViewer, HttpStatusCode.OK)]
    public async Task SignalR_negotiate_requires_an_active_identity(
        string? credential,
        HttpStatusCode expected)
    {
        using var response = credential is null
            ? await _client.PostAsync(HubNegotiate, content: null)
            : await SendAsync(HttpMethod.Post, HubNegotiate, credential);

        Assert.Equal(expected, response.StatusCode);
        AssertNoSessionCookie(response);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Mock_provider_is_rejected_outside_Development_and_Testing(string environment)
    {
        using var factory = new EnvironmentWebApplicationFactory(environment, "Mock");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(
            "Development or Testing",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mock_provider_is_allowed_in_Development_without_publishing_test_probes()
    {
        using var factory = new EnvironmentWebApplicationFactory("Development", "Mock");
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health/live");
        using var probe = await client.GetAsync(AuthenticatedProbe);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Testing_probes_do_not_exist_outside_Testing(string environment)
    {
        using var factory = new EnvironmentWebApplicationFactory(environment, "Disabled");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(AuthenticatedProbe);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var tracking = await client.GetAsync("/__tests/tracking/synthetic-token");
        Assert.Equal(HttpStatusCode.NotFound, tracking.StatusCode);
    }

    [Fact]
    public void PostgreSql_providers_require_a_connection_string_at_startup()
    {
        using var factory = new MissingPostgreSqlConnectionWebApplicationFactory();

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Paqueteria", exception.ToString(), StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string credential)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await _client.SendAsync(request);
    }

    private static string OrganizationProbe(Guid organizationId) =>
        $"/__tests/security/organization/{organizationId:D}";

    private static async Task AssertGenericProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string? forbiddenText = null)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal((int)expected, body?.Status);
        Assert.Equal(expected == HttpStatusCode.Unauthorized ? "Unauthorized" : "Forbidden", body?.Title);
        Assert.False(string.IsNullOrWhiteSpace(body?.TraceId));

        if (forbiddenText is not null)
        {
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(forbiddenText, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoSessionCookie(HttpResponseMessage response) =>
        Assert.False(response.Headers.Contains("Set-Cookie"));

    private sealed record ProblemResponse(string Title, int Status, string TraceId);
}

internal sealed class MissingPostgreSqlConnectionWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "PostgreSql",
                ["PublicTracking:Provider"] = "PostgreSql",
                ["ConnectionStrings:Paqueteria"] = null,
            }));
    }
}
