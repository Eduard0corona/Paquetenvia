using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Incidents.Application.Incidents;
using Incidents.Infrastructure.Incidents;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// INC-001 resolution through the real API and a real PostgreSQL store: the published 200 / 401 /
/// 403 / 404 / 409 / 503 matrix, idempotent replay of the original response, and the guarantee
/// that closing an incident leaves its order and its evidence untouched.
/// </summary>
public sealed class IncidentResolutionHttpTests(IncidentResolutionHttpFixture fixture)
    : IClassFixture<IncidentResolutionHttpFixture>
{
    private const string Reason = "El cliente confirmó una nueva ventana de entrega.";

    private readonly HttpClient client = fixture.Api.CreateClient();

    [Theory]
    [InlineData("RESOLVED")]
    [InlineData("REJECTED")]
    public async Task A_dispatcher_closes_an_incident_with_200_and_the_Incident_representation(string outcome)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var before = await fixture.ReadAsync(seeded);

        using var response = await client.SendAsync(Request(seeded.IncidentId, Body(outcome)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(seeded.IncidentId, root.GetProperty("id").GetGuid());
        Assert.Equal(seeded.OrderId, root.GetProperty("order_id").GetGuid());
        Assert.Equal(outcome, root.GetProperty("status").GetString());
        Assert.Equal("MEDIUM", root.GetProperty("severity").GetString());
        Assert.Equal("RECIPIENT_ABSENT", root.GetProperty("reason_code").GetString());
        Assert.Equal("RESCHEDULED", root.GetProperty("next_action").GetString());
        Assert.True(root.GetProperty("custody_acquired").GetBoolean());
        Assert.Equal(
            seeded.ProofId,
            Assert.Single(root.GetProperty("evidence_proof_ids").EnumerateArray()).GetGuid());
        // Exactly the published Incident fields: no resolved_at, no reason, no description.
        Assert.Equal(
            [
                "custody_acquired", "evidence_proof_ids", "id", "next_action", "occurred_at",
                "order_id", "reason_code", "severity", "sla_due_at", "status",
            ],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        var after = await fixture.ReadAsync(seeded);
        Assert.Equal(outcome, after.Status);
        Assert.NotNull(after.ResolvedAt);
        Assert.Equal(1, after.ResolutionAudits);
        Assert.Equal(1, after.ResolutionReservations);
        // The order and the evidence are exactly as they were.
        Assert.Equal("DELIVERING", after.OrderStatus);
        Assert.Equal(before.OrderStatus, after.OrderStatus);
        Assert.Equal(before.OrderVersion, after.OrderVersion);
        Assert.Equal(before.OrderEvents, after.OrderEvents);
        Assert.Equal(before.Evidence, after.Evidence);
    }

    [Fact]
    public async Task An_MFA_satisfied_platform_admin_may_close_an_incident()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(
            seeded.IncidentId,
            Body("REJECTED"),
            profile: MockIdentityProfiles.ActivePlatformAdminMfa));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("REJECTED", (await fixture.ReadAsync(seeded)).Status);
    }

    [Fact]
    public async Task A_retry_with_the_same_key_and_body_returns_the_original_response()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var key = NewKey();

        using var first = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED"), key: key));
        var firstBody = await first.Content.ReadAsStringAsync();
        var resolvedAt = (await fixture.ReadAsync(seeded)).ResolvedAt;
        using var retry = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED"), key: key));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
        var after = await fixture.ReadAsync(seeded);
        Assert.Equal(resolvedAt, after.ResolvedAt);
        Assert.Equal(1, after.ResolutionAudits);
        Assert.Equal(1, after.ResolutionReservations);
    }

    [Fact]
    public async Task The_same_key_with_another_body_is_an_idempotency_conflict()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var key = NewKey();
        using (var first = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED"), key: key)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var mismatch = await client.SendAsync(Request(
            seeded.IncidentId,
            Body("RESOLVED", "Otro motivo del cierre."),
            key: key));

        await AssertConflictAsync(mismatch, "IDEMPOTENCY_CONFLICT");
        Assert.Equal(1, (await fixture.ReadAsync(seeded)).ResolutionAudits);
    }

    [Theory]
    [InlineData("RESOLVED", "RESOLVED")]
    [InlineData("RESOLVED", "REJECTED")]
    [InlineData("REJECTED", "RESOLVED")]
    public async Task Closing_a_terminal_incident_again_is_a_state_conflict(string first, string second)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        using (var closing = await client.SendAsync(Request(seeded.IncidentId, Body(first))))
        {
            Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
        }

        var closed = await fixture.ReadAsync(seeded);
        using var again = await client.SendAsync(Request(seeded.IncidentId, Body(second)));

        var body = await AssertConflictAsync(again, "INCIDENT_STATE_CONFLICT");
        // The current status of the incident is never disclosed by the conflict.
        Assert.DoesNotContain(first, body, StringComparison.Ordinal);
        Assert.Equal(closed, await fixture.ReadAsync(seeded));
    }

    [Fact]
    public async Task An_investigating_incident_can_be_closed()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId, "INVESTIGATING");

        using var response = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("RESOLVED", (await fixture.ReadAsync(seeded)).Status);
    }

    [Fact]
    public async Task A_missing_authentication_is_401()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED"), profile: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUntouchedAsync(seeded);
    }

    [Theory]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    public async Task An_actor_without_the_resolution_capability_is_403(string profile)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED"), profile: profile));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden.");
        await AssertUntouchedAsync(seeded);
    }

    [Fact]
    public async Task A_forbidden_actor_gets_the_same_403_for_a_real_and_a_random_incident()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var real = await client.SendAsync(Request(
            seeded.IncidentId, Body("RESOLVED"), profile: MockIdentityProfiles.ActiveDriver));
        using var random = await client.SendAsync(Request(
            Guid.NewGuid(), Body("RESOLVED"), profile: MockIdentityProfiles.ActiveDriver));

        // Capability is settled before incident visibility, so a driver cannot probe existence.
        Assert.Equal(
            await AssertProblemAsync(real, HttpStatusCode.Forbidden, "Forbidden."),
            await AssertProblemAsync(random, HttpStatusCode.Forbidden, "Forbidden."));
    }

    [Fact]
    public async Task A_missing_tenant_context_is_403()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(seeded.IncidentId, Body("RESOLVED"), includeTenant: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUntouchedAsync(seeded);
    }

    [Fact]
    public async Task A_foreign_incident_and_a_random_incident_are_the_same_uniform_404()
    {
        var foreign = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.ForeignTenantId);

        using var foreignResponse = await client.SendAsync(Request(foreign.IncidentId, Body("RESOLVED")));
        using var randomResponse = await client.SendAsync(Request(Guid.NewGuid(), Body("RESOLVED")));

        Assert.Equal(
            await AssertProblemAsync(foreignResponse, HttpStatusCode.NotFound, "Not Found."),
            await AssertProblemAsync(randomResponse, HttpStatusCode.NotFound, "Not Found."));
        await AssertUntouchedAsync(foreign);
    }

    public static TheoryData<string> MalformedBodies => new()
    {
        """{"outcome":"OPEN","reason":"motivo"}""",
        """{"outcome":"INVESTIGATING","reason":"motivo"}""",
        """{"outcome":"resolved","reason":"motivo"}""",
        """{"outcome":"RESOLVED"}""",
        """{"reason":"motivo"}""",
        """{"outcome":"RESOLVED","reason":""}""",
        """{"outcome":"RESOLVED","reason":"   "}""",
        """{"outcome":"RESOLVED","reason":" padded"}""",
        """{"outcome":"RESOLVED","reason":"line\nbreak"}""",
        """{"outcome":"RESOLVED","reason":"motivo","resolved_at":"2026-09-25T00:00:00Z"}""",
        """{"outcome":"RESOLVED","reason":"motivo","status":"RESOLVED"}""",
        """{"outcome":1,"reason":"motivo"}""",
        "not json",
        "null",
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task A_malformed_or_unknown_member_body_is_409_invalid_request(string body)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(seeded.IncidentId, body));

        await AssertConflictAsync(response, "INVALID_REQUEST");
        await AssertUntouchedAsync(seeded);
    }

    [Fact]
    public async Task A_reason_over_the_published_bound_is_409_invalid_request()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(
            seeded.IncidentId,
            Body("RESOLVED", new string('a', IncidentRequestPolicy.MaximumResolutionReasonLength + 1))));

        await AssertConflictAsync(response, "INVALID_REQUEST");
        await AssertUntouchedAsync(seeded);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_non_canonical_incident_id_is_409_invalid_request(string form)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var id = form switch
        {
            "N" => seeded.IncidentId.ToString("N"),
            "B" => Uri.EscapeDataString(seeded.IncidentId.ToString("B")),
            _ => form,
        };

        using var response = await client.SendAsync(Request(id, Body("RESOLVED"), NewKey()));

        await AssertConflictAsync(response, "INVALID_REQUEST");
        await AssertUntouchedAsync(seeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task A_missing_or_malformed_idempotency_key_is_409_invalid_request(string? key)
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await client.SendAsync(Request(seeded.IncidentId.ToString("D"), Body("RESOLVED"), key));

        await AssertConflictAsync(response, "INVALID_REQUEST");
        await AssertUntouchedAsync(seeded);
    }

    [Fact]
    public async Task A_disabled_incident_provider_is_503_without_internal_detail()
    {
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        await using var disabled = fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIncidentService>();
                services.AddSingleton<IIncidentService, DisabledIncidentService>();
            }));
        using var disabledClient = disabled.CreateClient();

        using var response = await disabledClient.SendAsync(Request(seeded.IncidentId, Body("RESOLVED")));

        var body = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Service unavailable.");
        Assert.DoesNotContain("disabled", body, StringComparison.OrdinalIgnoreCase);
        await AssertUntouchedAsync(seeded);
    }

    private static string Body(string outcome, string reason = Reason) =>
        JsonSerializer.Serialize(new { outcome, reason });

    private static string NewKey() => $"inc001-http-{Guid.NewGuid():N}";

    private static HttpRequestMessage Request(
        Guid incidentId,
        string body,
        string? profile = MockIdentityProfiles.ActiveDispatcher,
        bool includeTenant = true,
        string? key = null) =>
        Request(incidentId.ToString("D"), body, key ?? NewKey(), profile, includeTenant);

    private static HttpRequestMessage Request(
        string incidentId,
        string body,
        string? key,
        string? profile = MockIdentityProfiles.ActiveDispatcher,
        bool includeTenant = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/incidents/{incidentId}/resolution")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

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

    private async Task AssertUntouchedAsync(SeededIncident seeded)
    {
        var state = await fixture.ReadAsync(seeded);
        Assert.NotEqual("RESOLVED", state.Status);
        Assert.NotEqual("REJECTED", state.Status);
        Assert.Null(state.ResolvedAt);
        Assert.Equal(0, state.ResolutionAudits);
        Assert.Equal(0, state.ResolutionReservations);
        Assert.Equal("DELIVERING", state.OrderStatus);
        Assert.Equal(1, state.OrderVersion);
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

    private static async Task<string> AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await AssertProblemAsync(response, HttpStatusCode.Conflict, "Conflict.");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return body;
    }
}
