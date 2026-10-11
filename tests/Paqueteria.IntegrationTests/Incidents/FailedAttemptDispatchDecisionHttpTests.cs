using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Incidents.Infrastructure.Incidents;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11 through the real API on a real PostgreSQL store, in the order the
/// driver PWA will call it: the driver reports the failed delivery with a reason and the photo it took
/// (openIncident without severity, description or next_action), takes the order to FAILED_ATTEMPT with the incident,
/// and from there only dispatch decides — "Reprogramar", "Reintentar hoy" or "Regresar" — as getOrder lists it.
/// </summary>
public sealed class FailedAttemptDispatchDecisionHttpTests(FailedAttemptDispatchDecisionHttpFixture fixture)
    : IClassFixture<FailedAttemptDispatchDecisionHttpFixture>
{
    private const string Driver = MockIdentityProfiles.ActiveDriver;
    private const string Dispatcher = MockIdentityProfiles.ActiveDispatcher;

    private readonly HttpClient client = fixture.Api.CreateClient();

    [Fact]
    public async Task A_driver_report_reaches_FAILED_ATTEMPT_and_waits_for_dispatch()
    {
        var order = await fixture.SeedDeliveringOrderAsync();

        // 1. The report: reason and photo, nothing else.
        using var opened = await client.SendAsync(DriverReport(order, "SECURITY_RISK"));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        using var incident = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
        var report = incident.RootElement;
        Assert.Equal(JsonValueKind.Null, report.GetProperty("next_action").ValueKind);
        Assert.Equal("HIGH", report.GetProperty("severity").GetString());
        Assert.Equal("SECURITY_RISK", report.GetProperty("reason_code").GetString());
        Assert.True(report.GetProperty("custody_acquired").GetBoolean());
        Assert.Equal(
            TimeSpan.FromHours(8),
            report.GetProperty("sla_due_at").GetDateTimeOffset() - report.GetProperty("occurred_at").GetDateTimeOffset());
        Assert.Equal(order.ProofId, Assert.Single(report.GetProperty("evidence_proof_ids").EnumerateArray()).GetGuid());
        var incidentId = report.GetProperty("id").GetGuid();

        var stored = await fixture.ReadReportAsync(incidentId);
        Assert.True(stored.NextActionIsNull);
        Assert.Equal("HIGH", stored.Severity);
        Assert.Equal(FailedAttemptDispatchDecisionHttpFixture.ActiveDriverUserId, stored.CreatedBy);
        Assert.Equal(DispatchDecisionApiFactory.PiiKeyVersion, stored.PiiKeyVersion);
        Assert.Equal(
            new DeterministicMockIncidentPiiProtector(DispatchDecisionApiFactory.PiiKeyVersion).Protect(
                "Intento fallido reportado por el repartidor. Motivo: Riesgo de seguridad.",
                DispatchDecisionApiFactory.PiiKeyVersion),
            stored.DescriptionCiphertext);

        // 2. The driver takes the order to FAILED_ATTEMPT with the incident, as today.
        using (var failed = await client.SendAsync(Transition(Driver, order.OrderId, "FAILED_ATTEMPT", order.Version, incidentId)))
        {
            await AssertOrderAsync(failed, "FAILED_ATTEMPT", order.Version + 1);
        }

        // 3. ...and can go no further: the decision belongs to dispatch.
        foreach (var target in new[] { "RESCHEDULED", "DELIVERING", "RETURNING" })
        {
            using var refused = await client.SendAsync(Transition(Driver, order.OrderId, target, order.Version + 1));
            await AssertCodedConflictAsync(refused, "DISPATCH_DECISION_REQUIRED");
        }

        // 4. Dispatch is offered the three exits, and reads the report with its decision pending.
        Assert.Equal(["DELIVERING", "RESCHEDULED", "RETURNING"], await AllowedTargetsAsync(order.OrderId));
        using var listed = await client.SendAsync(Get(Dispatcher, $"/api/v1/incidents?order_id={order.OrderId:D}"));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var page = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(incidentId, item.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("next_action").ValueKind);
    }

    [Theory]
    [InlineData("RESCHEDULED")]
    [InlineData("DELIVERING")]
    [InlineData("RETURNING")]
    public async Task Dispatch_takes_each_decision_through_the_API(string successor)
    {
        var order = await fixture.SeedDeliveringOrderAsync();
        var incidentId = await ReportAsync(order, "RECIPIENT_ABSENT");
        using (var failed = await client.SendAsync(Transition(Driver, order.OrderId, "FAILED_ATTEMPT", order.Version, incidentId)))
        {
            await AssertOrderAsync(failed, "FAILED_ATTEMPT", order.Version + 1);
        }

        using var decided = await client.SendAsync(Transition(Dispatcher, order.OrderId, successor, order.Version + 1));

        await AssertOrderAsync(decided, successor, order.Version + 2);
    }

    [Fact]
    public async Task The_report_shape_follows_who_reports()
    {
        var order = await fixture.SeedDeliveringOrderAsync();

        // A driver never sends severity, description or next_action, not even the value the server would derive.
        foreach (var extra in new Dictionary<string, object>[]
                 {
                     new() { ["next_action"] = "RESCHEDULED" },
                     new() { ["severity"] = "MEDIUM" },
                     new() { ["description"] = "Nadie atendió." },
                     new()
                     {
                         ["severity"] = "MEDIUM",
                         ["description"] = "Nadie atendió.",
                         ["next_action"] = "RESCHEDULED",
                     },
                 })
        {
            using var refused = await client.SendAsync(Open(Driver, order, Body(order, "RECIPIENT_ABSENT", extra)));
            await AssertCodedConflictAsync(refused, "INVALID_REQUEST");
        }

        // Dispatch reports as today: without its three fields the request is not a report.
        using (var bare = await client.SendAsync(Open(Dispatcher, order, Body(order, "RECIPIENT_ABSENT"))))
        {
            await AssertCodedConflictAsync(bare, "INVALID_REQUEST");
        }

        Assert.Equal(0L, await fixture.CountIncidentsAsync(order.OrderId));

        using var dispatch = await client.SendAsync(Open(
            Dispatcher,
            order,
            Body(order, "RECIPIENT_ABSENT", new Dictionary<string, object>
            {
                ["severity"] = "LOW",
                ["description"] = "El destinatario pidió volver otro día.",
                ["next_action"] = "RESCHEDULED",
            })));
        Assert.Equal(HttpStatusCode.Created, dispatch.StatusCode);
        using var body = JsonDocument.Parse(await dispatch.Content.ReadAsStringAsync());
        Assert.Equal("RESCHEDULED", body.RootElement.GetProperty("next_action").GetString());
        Assert.Equal("LOW", body.RootElement.GetProperty("severity").GetString());
        Assert.Equal(1L, await fixture.CountIncidentsAsync(order.OrderId));
    }

    private async Task<Guid> ReportAsync(SeededDelivery order, string reasonCode)
    {
        using var opened = await client.SendAsync(DriverReport(order, reasonCode));
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        using var body = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("next_action").ValueKind);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string[]> AllowedTargetsAsync(Guid orderId)
    {
        using var response = await client.SendAsync(Get(Dispatcher, $"/api/v1/orders/{orderId:D}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return
        [
            .. body.RootElement.GetProperty("allowed_transitions").EnumerateArray().Select(item =>
            {
                Assert.Empty(item.GetProperty("required_metadata").EnumerateArray());
                return item.GetProperty("target_status").GetString()!;
            }),
        ];
    }

    /// <summary>What the driver PWA sends: type, reason, the moment of the attempt and the photo.</summary>
    private static HttpRequestMessage DriverReport(SeededDelivery order, string reasonCode) =>
        Open(Driver, order, Body(order, reasonCode));

    private static Dictionary<string, object> Body(
        SeededDelivery order,
        string reasonCode,
        IReadOnlyDictionary<string, object>? extra = null)
    {
        var body = new Dictionary<string, object>
        {
            ["type"] = "FAILED_DELIVERY_ATTEMPT",
            ["reason_code"] = reasonCode,
            ["occurred_at"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture),
            ["evidence_proof_ids"] = new[] { order.ProofId },
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, object>())
        {
            body[key] = value;
        }

        return body;
    }

    private static HttpRequestMessage Open(string profile, SeededDelivery order, object body) =>
        Post(profile, $"/api/v1/orders/{order.OrderId:D}/incidents", body);

    private static HttpRequestMessage Transition(
        string profile,
        Guid orderId,
        string target,
        int expectedVersion,
        Guid? incidentId = null)
    {
        // Only FAILED_ATTEMPT takes metadata (the incident); every other edge takes an empty object.
        object metadata = incidentId is { } id ? new { incident_id = id } : new { };
        return Post(
            profile,
            $"/api/v1/orders/{orderId:D}/transitions",
            new
            {
                target_status = target,
                reason = "Paso sintético INC-002.",
                expected_version = expectedVersion,
                metadata,
            });
    }

    private static HttpRequestMessage Post(string profile, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", FailedAttemptDispatchDecisionHttpFixture.TenantId.ToString("D"));
        request.Headers.Add("Idempotency-Key", $"inc002-http-{Guid.NewGuid():N}");
        return request;
    }

    private static HttpRequestMessage Get(string profile, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", FailedAttemptDispatchDecisionHttpFixture.TenantId.ToString("D"));
        return request;
    }

    private static async Task AssertOrderAsync(HttpResponseMessage response, string status, int version)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 {status}, got {(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(status, body.RootElement.GetProperty("status").GetString());
        Assert.Equal(version, body.RootElement.GetProperty("version").GetInt32());
    }

    private static async Task AssertCodedConflictAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict,
            $"Expected 409 {code}, got {(int)response.StatusCode}: {text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        // A rule code names the rule, never the incident, the report or the actor.
        Assert.DoesNotContain("incident_id", text, StringComparison.Ordinal);
    }
}
