using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Paqueteria.IntegrationTests.Security;

public sealed class TenantSelectionPipelineTests : IClassFixture<SecurityWebApplicationFactory>
{
    private readonly HttpClient client;

    public TenantSelectionPipelineTests(SecurityWebApplicationFactory factory)
    {
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Protected_tenant_endpoint_requires_authentication_before_tenant_selection()
    {
        using var response = await client.GetAsync("/__tests/tenancy/active");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData(" 0f0e0d0c-0b0a-0908-0706-050403020100")]
    public async Task Authenticated_tenant_endpoint_rejects_missing_or_invalid_header(string? header)
    {
        using var request = Authenticated("/__tests/tenancy/active", MockIdentityProfiles.ActiveViewer);
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Organization-Id", header);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "Invalid organization context.", 400);
    }

    [Fact]
    public async Task Foreign_organization_is_forbidden_without_disclosure()
    {
        using var request = Authenticated("/__tests/tenancy/active", MockIdentityProfiles.ActiveViewer);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ForeignOrganizationId.ToString("D"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, "Forbidden.", 403);
    }

    [Fact]
    public async Task Active_membership_selects_exactly_the_requested_organization()
    {
        using var request = Authenticated("/__tests/tenancy/active", MockIdentityProfiles.ActiveMultiOrganization);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.OperationsOrganizationId.ToString("D"));

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(MockIdentityProfiles.OperationsOrganizationId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminMfa, HttpStatusCode.NoContent)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa, HttpStatusCode.Forbidden)]
    [InlineData(MockIdentityProfiles.ActiveViewer, HttpStatusCode.Forbidden)]
    public async Task Platform_admin_policy_requires_role_in_active_org_and_mfa(string profile, HttpStatusCode expected)
    {
        using var request = Authenticated("/__tests/tenancy/platform-admin", profile);
        request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));

        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Product_context_list_does_not_require_active_organization_header()
    {
        using var request = Authenticated("/api/v1/me/organization-contexts", MockIdentityProfiles.ActiveViewer);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// The tenant context problem goes out as exactly <c>application/problem+json</c> (AI-05), with exactly the members
    /// <c>type</c>, <c>title</c>, <c>status</c> and the request's W3C trace id as <c>traceId</c> (OBS-003).
    /// </summary>
    private static async Task AssertProblemAsync(HttpResponseMessage response, string title, int status)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            ["status", "title", "traceId", "type"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal(title, root.GetProperty("title").GetString());
        Assert.Equal(status, root.GetProperty("status").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("traceId").GetString());
    }

    private static HttpRequestMessage Authenticated(string path, string profile)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        return request;
    }
}
