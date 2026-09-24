using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// FIN-001 HTTP failure semantics for the four AI-05 Finance operations: capability failures are 403,
/// inaccessible resources the uniform 404, state and concurrency conflicts 409, and an unavailable
/// provider or store 503 — never collapsed into one another and never echoing internal evidence.
/// </summary>
public sealed class FinanceHttpTests : IClassFixture<FinanceHttpWebApplicationFactory>
{
    private readonly HttpClient client;

    public FinanceHttpTests(FinanceHttpWebApplicationFactory factory)
    {
        client = factory.CreateClient();
    }

    public static TheoryData<string> Operations => new()
    {
        "recordCodCollection",
        "reconcileCod",
        "getOrderFinancials",
        "getRouteFinancials",
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Unavailable_finance_is_503_without_internal_detail(string operation)
    {
        using var request = Request(operation, FinanceHttpWebApplicationFactory.Unavailable);
        using var response = await client.SendAsync(request);

        var body = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Service unavailable.");
        Assert.DoesNotContain("10.20.30.40", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fin001-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cod_transactions", body, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Genuine_capability_failure_is_403(string operation)
    {
        using var request = Request(operation, FinanceHttpWebApplicationFactory.Forbidden);
        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden.");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Inaccessible_resource_is_the_uniform_404(string operation)
    {
        using var request = Request(operation, FinanceHttpWebApplicationFactory.Missing);
        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found.");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task State_and_concurrency_conflicts_are_409_with_a_stable_code(string operation)
    {
        using (var request = Request(operation, FinanceHttpWebApplicationFactory.AmountMismatch))
        using (var response = await client.SendAsync(request))
        {
            await AssertConflictAsync(response, "COD_AMOUNT_MISMATCH");
        }

        using (var request = Request(operation, FinanceHttpWebApplicationFactory.ConcurrencyConflict))
        using (var response = await client.SendAsync(request))
        {
            await AssertConflictAsync(response, "CONFLICT");
        }
    }

    [Theory]
    [InlineData("recordCodCollection", HttpStatusCode.Created)]
    [InlineData("reconcileCod", HttpStatusCode.OK)]
    [InlineData("getOrderFinancials", HttpStatusCode.OK)]
    [InlineData("getRouteFinancials", HttpStatusCode.OK)]
    public async Task Accepted_requests_keep_their_success_status(string operation, HttpStatusCode expected)
    {
        using var request = Request(operation, FinanceHttpWebApplicationFactory.Succeeds);
        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Uncollected_cod_has_a_null_status_and_no_amount_and_is_not_pending()
    {
        using (var request = Request("getOrderFinancials", FinanceHttpWebApplicationFactory.Succeeds))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var cod = document.RootElement.GetProperty("cod");
            Assert.Equal(5_000, cod.GetProperty("expected_cents").GetInt64());
            Assert.Equal(JsonValueKind.Null, cod.GetProperty("status").ValueKind);
            Assert.False(cod.TryGetProperty("amount_cents", out _));
            Assert.False(cod.GetProperty("recorded").GetBoolean());
        }

        using (var request = Request("getRouteFinancials", FinanceHttpWebApplicationFactory.Succeeds))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(5_000, document.RootElement.GetProperty("cod_expected_cents_total").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("cod_pending_reconciliation_cents_total").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("cod_pending_reconciliation_count").GetInt32());
        }
    }

    [Fact]
    public async Task Malformed_identifiers_are_404_on_reads_and_invalid_request_on_writes()
    {
        foreach (var operation in new[] { "getOrderFinancials", "getRouteFinancials" })
        {
            using var request = Request(operation, "not-a-uuid");
            using var response = await client.SendAsync(request);
            await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found.");
        }

        foreach (var operation in new[] { "recordCodCollection", "reconcileCod" })
        {
            using var request = Request(operation, "not-a-uuid");
            using var response = await client.SendAsync(request);
            await AssertConflictAsync(response, "INVALID_REQUEST");
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Authentication_and_tenant_context_keep_their_401_and_403(string operation)
    {
        using (var request = Request(operation, FinanceHttpWebApplicationFactory.Succeeds, profile: null))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = Request(operation, FinanceHttpWebApplicationFactory.Succeeds, includeTenant: false))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Disabled_finance_provider_is_503_not_403(string operation)
    {
        await using var disabled = new DisabledFinanceWebApplicationFactory();
        using var disabledClient = disabled.CreateClient();
        using var request = Request(operation, FinanceHttpWebApplicationFactory.Succeeds);
        using var response = await disabledClient.SendAsync(request);

        var body = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Service unavailable.");
        Assert.DoesNotContain("disabled", body, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage Request(
        string operation,
        Guid id,
        string? profile = MockIdentityProfiles.ActiveDispatcher,
        bool includeTenant = true) =>
        Request(operation, id.ToString("D"), profile, includeTenant);

    private static HttpRequestMessage Request(
        string operation,
        string id,
        string? profile = MockIdentityProfiles.ActiveDispatcher,
        bool includeTenant = true)
    {
        var request = operation switch
        {
            "recordCodCollection" => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{id}/cod-records")
            {
                Content = new StringContent(
                    """{"amount_cents":5000,"reference":"cash-fin001-http"}""",
                    Encoding.UTF8,
                    "application/json"),
            },
            "reconcileCod" => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cod-records/{id}/reconcile"),
            "getOrderFinancials" => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders/{id}/financials"),
            "getRouteFinancials" => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/routes/{id}/financials"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (request.Method == HttpMethod.Post)
        {
            request.Headers.Add("Idempotency-Key", $"fin001-http-{Guid.NewGuid():N}");
        }

        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add("X-Organization-Id", MockIdentityProfiles.ViewerOrganizationId.ToString("D"));
        }

        return request;
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string title)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal((int)expected, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(title, document.RootElement.GetProperty("title").GetString());
        Assert.False(document.RootElement.TryGetProperty("detail", out _));
        return body;
    }

    private static async Task AssertConflictAsync(HttpResponseMessage response, string code)
    {
        var body = await AssertProblemAsync(response, HttpStatusCode.Conflict, "Conflict.");
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
