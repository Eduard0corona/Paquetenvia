using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// API-INC-LIST-PROOFS-2026-09-29 through the real API and a real PostgreSQL store: listIncidents,
/// getIncident and listOrderProofs return exactly the published representations, admit only DISPATCHER
/// and MFA-satisfied PLATFORM_ADMIN before any resource is read, answer a foreign and a missing
/// resource with the same uniform 404, reject malformed queries with 409 INVALID_REQUEST and never write.
/// </summary>
public sealed class IncidentAndProofReadHttpTests(IncidentResolutionHttpFixture fixture)
    : IClassFixture<IncidentResolutionHttpFixture>
{
    private static readonly string[] IncidentFields =
    [
        "custody_acquired", "evidence_proof_ids", "id", "next_action", "occurred_at",
        "order_id", "reason_code", "severity", "sla_due_at", "status",
    ];

    private readonly HttpClient client = fixture.Api.CreateClient();

    [Fact]
    public async Task A_dispatcher_lists_the_incidents_of_an_order_as_the_Incident_representation()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var before = await fixture.ReadAsync(seeded);

        using var response = await client.SendAsync(Get($"/api/v1/incidents?order_id={seeded.OrderId:D}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(["items", "next_cursor"], root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_cursor").ValueKind);
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(IncidentFields, item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(seeded.IncidentId, item.GetProperty("id").GetGuid());
        Assert.Equal(seeded.OrderId, item.GetProperty("order_id").GetGuid());
        Assert.Equal("OPEN", item.GetProperty("status").GetString());
        Assert.Equal(seeded.ProofId, Assert.Single(item.GetProperty("evidence_proof_ids").EnumerateArray()).GetGuid());
        Assert.DoesNotContain(IncidentResolutionHttpFixture.SeededDescription, root.GetRawText(), StringComparison.Ordinal);

        // Reading never writes.
        Assert.Equal(before, await fixture.ReadAsync(seeded));
    }

    [Fact]
    public async Task The_status_filter_keeps_only_that_status()
    {
        var open = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var investigating = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId, "INVESTIGATING");

        using var response = await client.SendAsync(Get("/api/v1/incidents?status=INVESTIGATING"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = await ReadIdsAsync(response);
        Assert.Contains(investigating.IncidentId, ids);
        Assert.DoesNotContain(open.IncidentId, ids);
    }

    [Fact]
    public async Task A_dispatcher_reads_one_incident()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Get($"/api/v1/incidents/{seeded.IncidentId:D}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(IncidentFields, document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(seeded.IncidentId, document.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_dispatcher_lists_the_proof_metadata_of_an_order_and_nothing_else()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Get($"/api/v1/orders/{seeded.OrderId:D}/proofs"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next_cursor").ValueKind);
        var proof = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["captured_at", "id", "proof_type", "sha256"],
            proof.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(seeded.ProofId, proof.GetProperty("id").GetGuid());
        Assert.Equal("DELIVERY_PHOTO", proof.GetProperty("proof_type").GetString());
        Assert.Equal(string.Concat(Enumerable.Repeat("04", 32)), proof.GetProperty("sha256").GetString());
        // No storage key, quarantine key or URL is ever part of the listing.
        Assert.DoesNotContain("proofs/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("quarantine", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_MFA_satisfied_platform_admin_may_read()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        foreach (var path in Paths(seeded.IncidentId, seeded.OrderId))
        {
            using var response = await client.SendAsync(Get(path, MockIdentityProfiles.ActivePlatformAdminMfa));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    public async Task Every_other_actor_gets_the_same_403_for_a_real_and_a_random_resource(string profile)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var before = await fixture.ReadAsync(seeded);

        var real = Paths(seeded.IncidentId, seeded.OrderId);
        var random = Paths(Guid.NewGuid(), Guid.NewGuid());
        for (var index = 0; index < real.Length; index++)
        {
            using var realResponse = await client.SendAsync(Get(real[index], profile));
            using var randomResponse = await client.SendAsync(Get(random[index], profile));
            Assert.Equal(
                await AssertProblemAsync(realResponse, HttpStatusCode.Forbidden, "Forbidden."),
                await AssertProblemAsync(randomResponse, HttpStatusCode.Forbidden, "Forbidden."));
        }

        Assert.Equal(before, await fixture.ReadAsync(seeded));
    }

    [Fact]
    public async Task A_missing_authentication_is_401_and_a_missing_tenant_is_403()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        foreach (var path in Paths(seeded.IncidentId, seeded.OrderId))
        {
            using var anonymous = await client.SendAsync(Get(path, profile: null));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var noTenant = await client.SendAsync(Get(path, includeTenant: false));
            Assert.Equal(HttpStatusCode.Forbidden, noTenant.StatusCode);
        }
    }

    [Fact]
    public async Task A_foreign_a_random_and_a_malformed_resource_are_the_same_uniform_404()
    {
        var foreign = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.ForeignTenantId);

        foreach (var (foreignPath, randomPath, malformedPath) in new[]
                 {
                     ($"/api/v1/incidents/{foreign.IncidentId:D}", $"/api/v1/incidents/{Guid.NewGuid():D}",
                         $"/api/v1/incidents/{foreign.IncidentId:N}"),
                     ($"/api/v1/orders/{foreign.OrderId:D}/proofs", $"/api/v1/orders/{Guid.NewGuid():D}/proofs",
                         "/api/v1/orders/not-a-uuid/proofs"),
                 })
        {
            using var foreignResponse = await client.SendAsync(Get(foreignPath));
            using var randomResponse = await client.SendAsync(Get(randomPath));
            using var malformedResponse = await client.SendAsync(Get(malformedPath));
            var expected = await AssertProblemAsync(foreignResponse, HttpStatusCode.NotFound, "Not Found.");
            Assert.Equal(expected, await AssertProblemAsync(randomResponse, HttpStatusCode.NotFound, "Not Found."));
            Assert.Equal(expected, await AssertProblemAsync(malformedResponse, HttpStatusCode.NotFound, "Not Found."));
        }

        using var list = await client.SendAsync(Get($"/api/v1/incidents?order_id={foreign.OrderId:D}"));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty(await ReadIdsAsync(list));
    }

    [Theory]
    [InlineData("/api/v1/incidents?status=open")]
    [InlineData("/api/v1/incidents?status=CLOSED")]
    [InlineData("/api/v1/incidents?status=")]
    [InlineData("/api/v1/incidents?status=OPEN&status=RESOLVED")]
    [InlineData("/api/v1/incidents?order_id=not-a-uuid")]
    [InlineData("/api/v1/incidents?order_id=00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/v1/incidents?cursor=not-a-cursor")]
    [InlineData("/api/v1/incidents?limit=10")]
    [InlineData("/api/v1/incidents?page_size=500")]
    public async Task A_malformed_incident_query_is_409_invalid_request(string path)
    {
        using var response = await client.SendAsync(Get(path));

        var body = await AssertProblemAsync(response, HttpStatusCode.Conflict, "Conflict.");
        using var document = JsonDocument.Parse(body);
        Assert.Equal("INVALID_REQUEST", document.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=a&cursor=b")]
    [InlineData("status=OPEN")]
    public async Task A_malformed_proof_query_is_409_invalid_request(string query)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Get($"/api/v1/orders/{seeded.OrderId:D}/proofs?{query}"));

        var body = await AssertProblemAsync(response, HttpStatusCode.Conflict, "Conflict.");
        using var document = JsonDocument.Parse(body);
        Assert.Equal("INVALID_REQUEST", document.RootElement.GetProperty("code").GetString());
    }

    private static string[] Paths(Guid incidentId, Guid orderId) =>
    [
        "/api/v1/incidents",
        $"/api/v1/incidents/{incidentId:D}",
        $"/api/v1/orders/{orderId:D}/proofs",
    ];

    private static HttpRequestMessage Get(
        string path,
        string? profile = MockIdentityProfiles.ActiveDispatcher,
        bool includeTenant = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add("X-Organization-Id", IncidentResolutionHttpFixture.TenantId.ToString("D"));
        }

        return request;
    }

    private static async Task<Guid[]> ReadIdsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
    }

    /// <summary>Returns the problem body without its per-request trace identifier.</summary>
    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string title)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal((int)expected, root.GetProperty("status").GetInt32());
        Assert.Equal(title, root.GetProperty("title").GetString());
        Assert.False(root.TryGetProperty("detail", out _));
        return JsonSerializer.Serialize(root.EnumerateObject()
            .Where(property => property.Name != "traceId")
            .ToDictionary(property => property.Name, property => property.Value.ToString()));
    }
}
