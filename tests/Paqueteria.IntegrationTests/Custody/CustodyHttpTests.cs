using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Custody;

public sealed class CustodyHttpTests : IClassFixture<CustodyHttpWebApplicationFactory>
{
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly HttpClient client;

    public CustodyHttpTests(CustodyHttpWebApplicationFactory factory) =>
        client = factory.CreateClient();

    [Fact]
    public async Task Endpoints_require_authentication_and_tenant_context()
    {
        using var anonymous = UploadRequest(Guid.NewGuid(), null, includeTenant: true);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noTenant = UploadRequest(
            Guid.NewGuid(),
            MockIdentityProfiles.ActiveDispatcher,
            includeTenant: false);
        using var noTenantResponse = await client.SendAsync(noTenant);
        Assert.Equal(HttpStatusCode.Forbidden, noTenantResponse.StatusCode);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    public async Task Upload_session_denies_unauthorized_capabilities(string profile)
    {
        using var request = UploadRequest(Guid.NewGuid(), profile);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Upload_session_returns_only_the_normative_fields_and_signed_headers()
    {
        using var request = UploadRequest(
            Guid.NewGuid(),
            MockIdentityProfiles.ActiveDispatcher,
            body: """{"proof_type":"PICKUP_PHOTO","content_type":"image/png","size_bytes":8}""");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["expires_at", "id", "object_key", "required_headers", "status", "upload_url"],
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal("CREATED", body.RootElement.GetProperty("status").GetString());
        Assert.StartsWith(
            "quarantine/",
            body.RootElement.GetProperty("object_key").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "image/png",
            body.RootElement.GetProperty("required_headers")
                .GetProperty("Content-Type")
                .GetString());
        Assert.False(body.RootElement.GetProperty("required_headers").TryGetProperty(
            "x-amz-meta-sha256",
            out _));
    }

    public static TheoryData<string> InvalidUploadBodies => new()
    {
        $$"""{"proof_type":"UNKNOWN","content_type":"image/png","size_bytes":8,"sha256":"{{Sha256}}"}""",
        $$"""{"proof_type":"PICKUP_PHOTO","content_type":"image/png","size_bytes":0,"sha256":"{{Sha256}}"}""",
        $$"""{"proof_type":"PICKUP_PHOTO","content_type":"image/png","size_bytes":8,"sha256":"bad"}""",
        $$"""{"proof_type":"PICKUP_PHOTO","content_type":"image/png","size_bytes":8,"sha256":"{{Sha256}}","extra":true}""",
    };

    [Theory]
    [MemberData(nameof(InvalidUploadBodies))]
    public async Task Upload_session_rejects_invalid_bodies_with_uniform_conflict(string body)
    {
        using var request = UploadRequest(
            Guid.NewGuid(),
            MockIdentityProfiles.ActiveDispatcher,
            body: body);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("INVALID_REQUEST", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalization_returns_only_the_normative_proof_fields()
    {
        using var request = FinalizeRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MockIdentityProfiles.ActiveDispatcher);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ["captured_at", "id", "proof_type", "sha256"],
            body.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal("DELIVERY_PHOTO", body.RootElement.GetProperty("proof_type").GetString());
        Assert.Equal(Sha256, body.RootElement.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Finalization_maps_not_ready_and_pii_fail_closed()
    {
        using var notReady = FinalizeRequest(
            Guid.NewGuid(),
            CustodyHttpWebApplicationFactory.NotReadySessionId,
            MockIdentityProfiles.ActiveDispatcher);
        using var notReadyResponse = await client.SendAsync(notReady);
        Assert.Equal(HttpStatusCode.Conflict, notReadyResponse.StatusCode);
        Assert.Contains(
            "UPLOAD_SESSION_NOT_READY",
            await notReadyResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var recipientBody = $$"""
            {
              "upload_session_id":"{{Guid.NewGuid():D}}",
              "proof_type":"DELIVERY_PHOTO",
              "captured_at":"2026-07-25T12:00:00Z",
              "sha256":"{{Sha256}}",
              "recipient_name":"Synthetic Recipient"
            }
            """;
        using var recipient = FinalizeRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MockIdentityProfiles.ActiveDispatcher,
            recipientBody);
        using var recipientResponse = await client.SendAsync(recipient);
        Assert.Equal(HttpStatusCode.Conflict, recipientResponse.StatusCode);
        Assert.Contains(
            "PII_PROTECTION_UNAVAILABLE",
            await recipientResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_and_cross_tenant_resources_use_uniform_not_found()
    {
        using var upload = UploadRequest(
            CustodyHttpWebApplicationFactory.MissingOrderId,
            MockIdentityProfiles.ActiveDispatcher);
        using var uploadResponse = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.NotFound, uploadResponse.StatusCode);
        using var proof = FinalizeRequest(
            CustodyHttpWebApplicationFactory.MissingOrderId,
            Guid.NewGuid(),
            MockIdentityProfiles.ActiveDispatcher);
        using var proofResponse = await client.SendAsync(proof);
        Assert.Equal(HttpStatusCode.NotFound, proofResponse.StatusCode);
        using var uploadBody = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        using var proofBody = JsonDocument.Parse(await proofResponse.Content.ReadAsStringAsync());
        foreach (var property in new[] { "type", "title", "status" })
        {
            Assert.Equal(
                uploadBody.RootElement.GetProperty(property).ToString(),
                proofBody.RootElement.GetProperty(property).ToString());
        }
    }

    private static HttpRequestMessage UploadRequest(
        Guid orderId,
        string? profile,
        bool includeTenant = true,
        string? body = null)
    {
        body ??= $$"""
            {
              "proof_type":"PICKUP_PHOTO",
              "content_type":"image/png",
              "size_bytes":8,
              "sha256":"{{Sha256}}"
            }
            """;
        return Request(
            $"/api/v1/orders/{orderId:D}/proof-upload-sessions",
            profile,
            includeTenant,
            body);
    }

    private static HttpRequestMessage FinalizeRequest(
        Guid orderId,
        Guid sessionId,
        string? profile,
        string? body = null)
    {
        body ??= $$"""
            {
              "upload_session_id":"{{sessionId:D}}",
              "proof_type":"DELIVERY_PHOTO",
              "captured_at":"2026-07-25T12:00:00Z",
              "sha256":"{{Sha256}}"
            }
            """;
        return Request($"/api/v1/orders/{orderId:D}/proofs", profile, true, body);
    }

    private static HttpRequestMessage Request(
        string path,
        string? profile,
        bool includeTenant,
        string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", $"pod001-{Guid.NewGuid():N}");
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add(
                "X-Organization-Id",
                MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        }

        return request;
    }
}
