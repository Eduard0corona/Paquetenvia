using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Paqueteria.IntegrationTests.Finance;

/// <summary>
/// SET-001 settlements through the real API and a real PostgreSQL store: the published 200 / 201 / 401 /
/// 403 / 404 / 409 / 503 matrix of all seven operations, exact idempotent replay, the approval blockers
/// with their stable codes, and the CSV export as a client downloads it.
/// </summary>
public sealed class SettlementHttpTests(SettlementHttpFixture fixture) : IClassFixture<SettlementHttpFixture>
{
    private const string PeriodFrom = "2026-09-14";
    private const string PeriodTo = "2026-09-20";

    /// <summary>Inside the period: America/Mazatlan is UTC-07:00, so it runs [14 Sep 07:00Z, 21 Sep 07:00Z).</summary>
    private static readonly DateTimeOffset InPeriod = new(2026, 9, 16, 18, 0, 0, TimeSpan.Zero);

    private readonly HttpClient client = fixture.Api.CreateClient();

    [Fact]
    public async Task Finance_creates_a_settlement_with_201_and_the_persisted_Settlement()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        var delivered = await fixture.SeedWorkAsync(driver, "CLOSED", "DELIVERED", InPeriod, 4_500);
        var returned = await fixture.SeedWorkAsync(driver, "RETURNED", "RETURNED", InPeriod.AddHours(1), 3_000, 5_000);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero), 99);

        using var response = await client.SendAsync(Post("/api/v1/settlements", CreateBody(driver)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(
            ["created_at", "id", "lines", "payee_id", "payee_type", "period_from", "period_to", "status", "total_cents"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("CALCULATED", root.GetProperty("status").GetString());
        Assert.Equal("DRIVER", root.GetProperty("payee_type").GetString());
        Assert.Equal(driver, root.GetProperty("payee_id").GetGuid());
        Assert.Equal(PeriodFrom, root.GetProperty("period_from").GetString());
        Assert.Equal(PeriodTo, root.GetProperty("period_to").GetString());
        Assert.Equal(7_500, root.GetProperty("total_cents").GetInt64());
        var lines = root.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(
            ["amount_cents", "created_at", "id", "line_type", "order_id", "source_reference"],
            line.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)));
        Assert.Equal(
            [(delivered.Source, "DELIVERY", 4_500L), (returned.Source, "RETURN", 3_000L)],
            lines.Select(line => (
                    line.GetProperty("source_reference").GetString()!,
                    line.GetProperty("line_type").GetString()!,
                    line.GetProperty("amount_cents").GetInt64()))
                .OrderBy(line => line.Item3 == 4_500 ? 0 : 1));

        var settlement = root.GetProperty("id").GetGuid();
        Assert.Equal(new SettlementSnapshot("CALCULATED", 7_500, 2, 7_500m, 1), await fixture.ReadAsync(settlement));

        // GET returns exactly what create persisted.
        using var get = await client.SendAsync(Get($"/api/v1/settlements/{settlement:D}"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(root.ToString(), JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.ToString());
    }

    [Fact]
    public async Task A_settlement_moves_through_adjustment_approval_and_payment_and_exports_its_ledger()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);

        using (var adjusted = await client.SendAsync(Post(
            $"/api/v1/settlements/{settlement:D}/adjustments",
            """{"amount_cents":-250,"reason":"Descuento por daño en empaque"}""")))
        {
            Assert.Equal(HttpStatusCode.Created, adjusted.StatusCode);
            using var document = JsonDocument.Parse(await adjusted.Content.ReadAsStringAsync());
            Assert.Equal(4_250, document.RootElement.GetProperty("total_cents").GetInt64());
            var adjustment = Assert.Single(
                document.RootElement.GetProperty("lines").EnumerateArray(),
                line => line.GetProperty("line_type").GetString() == "ADJUSTMENT");
            Assert.Equal(JsonValueKind.Null, adjustment.GetProperty("order_id").ValueKind);
            Assert.StartsWith("platform.audit_logs/", adjustment.GetProperty("source_reference").GetString(), StringComparison.Ordinal);
        }

        foreach (var (operation, status) in new[] { ("approve", "APPROVED"), ("pay", "PAID") })
        {
            using var response = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/{operation}", null));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
            Assert.Equal(4_250, document.RootElement.GetProperty("total_cents").GetInt64());
        }

        using var export = await client.SendAsync(Get($"/api/v1/settlements/{settlement:D}/export.csv"));
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", export.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"settlement-{settlement:D}.csv", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await export.Content.ReadAsByteArrayAsync();
        var records = Encoding.UTF8.GetString(bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "settlement_id,payee_type,payee_id,period_from,period_to,settlement_status,settlement_total_cents," +
            "line_id,line_type,order_id,amount_cents,source_reference,line_created_at",
            records[0]);
        Assert.Equal(2, records.Length - 1);
        Assert.All(records.Skip(1), record => Assert.StartsWith(
            $"{settlement:D},DRIVER,{driver:D},{PeriodFrom},{PeriodTo},PAID,4250,", record, StringComparison.Ordinal));
        Assert.Equal(4_250, records.Skip(1).Sum(record => long.Parse(record.Split(',')[10], CultureInfo.InvariantCulture)));

        // The same persisted settlement always downloads as the same bytes.
        using var again = await client.SendAsync(Get($"/api/v1/settlements/{settlement:D}/export.csv"));
        Assert.Equal(bytes, await again.Content.ReadAsByteArrayAsync());
        Assert.Equal(new SettlementSnapshot("PAID", 4_250, 2, 4_250m, 4), await fixture.ReadAsync(settlement));
    }

    [Fact]
    public async Task A_retry_with_the_same_key_and_body_returns_the_original_response()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var key = NewKey();

        using var first = await client.SendAsync(Post("/api/v1/settlements", CreateBody(driver), key: key));
        var firstBody = await first.Content.ReadAsStringAsync();
        using var retry = await client.SendAsync(Post("/api/v1/settlements", CreateBody(driver), key: key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, await fixture.CountSettlementsForAsync(driver));

        using var mismatch = await client.SendAsync(Post(
            "/api/v1/settlements", CreateBody(driver, periodTo: "2026-09-21"), key: key));
        await AssertConflictAsync(mismatch, "CONFLICT");
        Assert.Equal(1, await fixture.CountSettlementsForAsync(driver));

        var settlement = JsonDocument.Parse(firstBody).RootElement.GetProperty("id").GetGuid();
        var approveKey = NewKey();
        using var approve = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/approve", null, key: approveKey));
        using var approveRetry = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/approve", null, key: approveKey));
        Assert.Equal(HttpStatusCode.OK, approveRetry.StatusCode);
        Assert.Equal(await approve.Content.ReadAsStringAsync(), await approveRetry.Content.ReadAsStringAsync());
        Assert.Equal(2, (await fixture.ReadAsync(settlement)).Audits);
    }

    [Theory]
    [InlineData("CASH_PENDING")]
    [InlineData("INCIDENT_PENDING")]
    [InlineData("CLAIM_PENDING")]
    public async Task Pending_cash_incidents_and_claims_block_approval_with_their_stable_code(string code)
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        var work = await fixture.SeedWorkAsync(
            driver,
            code == "CLAIM_PENDING" ? "CLAIM_OPEN" : "DELIVERED",
            "DELIVERED",
            InPeriod,
            4_500,
            code == "CASH_PENDING" ? 5_000 : 0);
        if (code == "INCIDENT_PENDING")
        {
            await fixture.SeedIncidentAsync(work.OrderId, "INVESTIGATING");
        }

        var settlement = await CreateAsync(driver);
        using var response = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/approve", null));

        var body = await AssertConflictAsync(response, code);
        Assert.DoesNotContain(work.OrderId.ToString("D"), body, StringComparison.Ordinal);
        Assert.Equal("CALCULATED", (await fixture.ReadAsync(settlement)).Status);
    }

    [Fact]
    public async Task A_returned_order_with_COD_expectation_approves()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "RETURNED", "RETURNED", InPeriod, 3_000, 5_000);
        var settlement = await CreateAsync(driver);

        using var response = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/approve", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("APPROVED", (await fixture.ReadAsync(settlement)).Status);
    }

    [Fact]
    public async Task Terminal_settlements_answer_a_state_conflict_that_discloses_nothing()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);
        using (var voided = await client.SendAsync(Post($"/api/v1/settlements/{settlement:D}/void", VoidBody)))
        {
            Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        }

        var before = await fixture.ReadAsync(settlement);
        foreach (var (path, body) in new[]
        {
            ($"/api/v1/settlements/{settlement:D}/void", VoidBody),
            ($"/api/v1/settlements/{settlement:D}/approve", (string?)null),
            ($"/api/v1/settlements/{settlement:D}/pay", null),
            ($"/api/v1/settlements/{settlement:D}/adjustments", """{"amount_cents":100,"reason":"Bono"}"""),
        })
        {
            using var response = await client.SendAsync(Post(path, body));
            var problem = await AssertConflictAsync(response, "SETTLEMENT_STATE_CONFLICT");
            Assert.DoesNotContain("VOID", problem, StringComparison.Ordinal);
        }

        Assert.Equal(before, await fixture.ReadAsync(settlement));
        Assert.Equal(("VOID", 4_500L, 1L), (before.Status, before.TotalCents, before.Lines));
    }

    [Fact]
    public async Task A_missing_authentication_is_401_for_every_operation()
    {
        foreach (var request in AllOperations(Guid.NewGuid(), Guid.NewGuid(), profile: null))
        {
            using (request)
            {
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
    }

    [Theory]
    [InlineData(SettlementHttpFixture.DispatcherProfile)]
    [InlineData(MockIdentityProfiles.ActiveDriver)]
    [InlineData(MockIdentityProfiles.ActiveViewer)]
    [InlineData(MockIdentityProfiles.ActivePlatformAdminNoMfa)]
    public async Task An_actor_without_the_settlement_capability_is_403_for_every_operation(string profile)
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);
        var before = await fixture.ReadAsync(settlement);

        // An existing and a missing settlement are refused alike, before anything about them is read.
        foreach (var target in new[] { settlement, Guid.NewGuid() })
        {
            foreach (var request in AllOperations(target, driver, profile))
            {
                using (request)
                {
                    using var response = await client.SendAsync(request);
                    await AssertProblemAsync(response, HttpStatusCode.Forbidden, "Forbidden.");
                }
            }
        }

        Assert.Equal(before, await fixture.ReadAsync(settlement));
        Assert.Equal(1, await fixture.CountSettlementsForAsync(driver));
    }

    [Fact]
    public async Task An_MFA_satisfied_platform_admin_may_operate_settlements()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);

        using var created = await client.SendAsync(Post(
            "/api/v1/settlements", CreateBody(driver), profile: MockIdentityProfiles.ActivePlatformAdminMfa));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var settlement = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var get = await client.SendAsync(Get(
            $"/api/v1/settlements/{settlement:D}", MockIdentityProfiles.ActivePlatformAdminMfa));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task A_request_without_an_active_organization_is_403()
    {
        using var response = await client.SendAsync(Get($"/api/v1/settlements/{Guid.NewGuid():D}", includeTenant: false));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_foreign_settlement_and_a_random_settlement_are_the_same_uniform_404()
    {
        var foreign = await fixture.SeedForeignSettlementAsync();
        var random = Guid.NewGuid();

        foreach (var (foreignRequest, randomRequest) in AllOperations(foreign, Guid.NewGuid(), SettlementHttpFixture.FinanceProfile)
                     .Zip(AllOperations(random, Guid.NewGuid(), SettlementHttpFixture.FinanceProfile))
                     .Skip(1))
        {
            using (foreignRequest)
            using (randomRequest)
            {
                using var foreignResponse = await client.SendAsync(foreignRequest);
                using var randomResponse = await client.SendAsync(randomRequest);
                Assert.Equal(
                    await AssertProblemAsync(foreignResponse, HttpStatusCode.NotFound, "Not Found."),
                    await AssertProblemAsync(randomResponse, HttpStatusCode.NotFound, "Not Found."));
            }
        }

        // A driver of another tenant is the same 404 as a driver that does not exist.
        var foreignDriver = await fixture.SeedDriverAsync(SettlementHttpFixture.ForeignTenantId);
        using var foreignCreate = await client.SendAsync(Post("/api/v1/settlements", CreateBody(foreignDriver)));
        using var randomCreate = await client.SendAsync(Post("/api/v1/settlements", CreateBody(Guid.NewGuid())));
        Assert.Equal(
            await AssertProblemAsync(foreignCreate, HttpStatusCode.NotFound, "Not Found."),
            await AssertProblemAsync(randomCreate, HttpStatusCode.NotFound, "Not Found."));
        Assert.Equal(0, await fixture.CountSettlementsForAsync(foreignDriver));
    }

    public static TheoryData<string, string?> MalformedRequests => new()
    {
        { "/api/v1/settlements", """{"driver_id":"{driver}","period_from":"2026-09-20","period_to":"2026-09-14"}""" },
        { "/api/v1/settlements", """{"driver_id":"{driver}","period_from":"2026-09-14"}""" },
        { "/api/v1/settlements", """{"driver_id":"{driver}","period_from":"14/09/2026","period_to":"2026-09-20"}""" },
        { "/api/v1/settlements", """{"driver_id":"{driver}","period_from":"2025-01-01","period_to":"2026-09-20"}""" },
        { "/api/v1/settlements", """{"driver_id":"{driver}","period_from":"2026-09-14","period_to":"2026-09-20","payee_type":"ALLY"}""" },
        { "/api/v1/settlements", """{"Driver_Id":"{driver}","period_from":"2026-09-14","period_to":"2026-09-20"}""" },
        { "/api/v1/settlements", "not json" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":0,"reason":"Sin efecto"}""" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":"250","reason":"Bono"}""" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":2.5,"reason":"Bono"}""" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":250,"reason":" Bono"}""" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":250,"reason":"Bono\ncon salto"}""" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":250}""" },
        { "/api/v1/settlements/{settlement}/adjustments", """{"amount_cents":250,"reason":"Bono","order_id":null}""" },
        { "/api/v1/settlements/{settlement}/void", """{}""" },
        { "/api/v1/settlements/{settlement}/void", """{"reason":""}""" },
        { "/api/v1/settlements/not-a-uuid/approve", null },
    };

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task A_malformed_request_is_409_invalid_request_and_changes_nothing(string path, string? body)
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);
        var before = await fixture.ReadAsync(settlement);

        using var response = await client.SendAsync(Post(
            path.Replace("{settlement}", settlement.ToString("D"), StringComparison.Ordinal),
            body?.Replace("{driver}", driver.ToString("D"), StringComparison.Ordinal)));

        await AssertConflictAsync(response, "INVALID_REQUEST");
        Assert.Equal(before, await fixture.ReadAsync(settlement));
        Assert.Equal(1, await fixture.CountSettlementsForAsync(driver));
    }

    [Fact]
    public async Task Every_mutation_without_an_Idempotency_Key_is_409_invalid_request()
    {
        var driver = await fixture.SeedDriverAsync(SettlementHttpFixture.TenantId);
        await fixture.SeedWorkAsync(driver, "DELIVERED", "DELIVERED", InPeriod, 4_500);
        var settlement = await CreateAsync(driver);
        var before = await fixture.ReadAsync(settlement);

        foreach (var request in AllOperations(settlement, driver, SettlementHttpFixture.FinanceProfile)
                     .Where(request => request.Method == HttpMethod.Post))
        {
            using (request)
            {
                request.Headers.Remove("Idempotency-Key");
                using var response = await client.SendAsync(request);
                await AssertConflictAsync(response, "INVALID_REQUEST");
            }
        }

        Assert.Equal(before, await fixture.ReadAsync(settlement));
        Assert.Equal(1, await fixture.CountSettlementsForAsync(driver));
    }

    [Fact]
    public async Task A_disabled_finance_provider_is_503_for_every_operation_without_internal_detail()
    {
        await using var disabled = fixture.Api.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Finance:Provider"] = "Disabled",
                })));
        using var disabledClient = disabled.CreateClient();

        foreach (var request in AllOperations(Guid.NewGuid(), Guid.NewGuid(), SettlementHttpFixture.FinanceProfile))
        {
            using (request)
            {
                using var response = await disabledClient.SendAsync(request);
                var body = await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "Service unavailable.");
                Assert.DoesNotContain("disabled", body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private const string VoidBody = """{"reason":"Periodo calculado por error"}""";

    private async Task<Guid> CreateAsync(Guid driver)
    {
        using var response = await client.SendAsync(Post("/api/v1/settlements", CreateBody(driver)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>One request per settlement operation, in the published order: create first.</summary>
    private static IEnumerable<HttpRequestMessage> AllOperations(Guid settlement, Guid driver, string? profile)
    {
        yield return Post("/api/v1/settlements", CreateBody(driver), profile: profile);
        yield return Get($"/api/v1/settlements/{settlement:D}", profile);
        yield return Post($"/api/v1/settlements/{settlement:D}/adjustments", """{"amount_cents":100,"reason":"Bono"}""", profile: profile);
        yield return Post($"/api/v1/settlements/{settlement:D}/approve", null, profile: profile);
        yield return Post($"/api/v1/settlements/{settlement:D}/pay", null, profile: profile);
        yield return Post($"/api/v1/settlements/{settlement:D}/void", VoidBody, profile: profile);
        yield return Get($"/api/v1/settlements/{settlement:D}/export.csv", profile);
    }

    private static string CreateBody(Guid driver, string periodTo = PeriodTo) =>
        $$"""{"driver_id":"{{driver:D}}","period_from":"{{PeriodFrom}}","period_to":"{{periodTo}}"}""";

    private static string NewKey() => $"set001-http-{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(
        string path,
        string? body,
        string? key = null,
        string? profile = SettlementHttpFixture.FinanceProfile,
        bool includeTenant = true)
    {
        var request = Request(HttpMethod.Post, path, profile, includeTenant);
        request.Headers.Add("Idempotency-Key", key ?? NewKey());
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static HttpRequestMessage Get(
        string path,
        string? profile = SettlementHttpFixture.FinanceProfile,
        bool includeTenant = true) =>
        Request(HttpMethod.Get, path, profile, includeTenant);

    private static HttpRequestMessage Request(HttpMethod method, string path, string? profile, bool includeTenant)
    {
        var request = new HttpRequestMessage(method, path);
        if (profile is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        }

        if (includeTenant)
        {
            request.Headers.Add("X-Organization-Id", SettlementHttpFixture.TenantId.ToString("D"));
        }

        return request;
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
