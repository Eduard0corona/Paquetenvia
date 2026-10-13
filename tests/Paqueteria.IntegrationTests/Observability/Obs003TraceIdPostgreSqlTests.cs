using System.Net;
using System.Net.Http.Headers;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Security;
using Paqueteria.IntegrationTests.Tracking;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// OBS-003 over PostgreSQL 18 with the productive transactional RLS: one request, one identifier. The W3C trace id
/// of a PLATFORM_ADMIN request is the same 32-hex value in a JSON console line of the request, in the
/// <c>platform.audit_logs.request_id</c> of its tenant activation audit and in the <c>traceId</c> of its problem;
/// an inbound <c>traceparent</c> (sampled), <c>tracestate</c> and <c>baggage</c> reach none of them.
/// </summary>
[Collection(PublicTrackingPostgreSqlCollection.Name)]
[Trait("Category", "PublicTrackingPostgreSql")]
public sealed class Obs003TraceIdPostgreSqlTests(PostgreSqlSecurityWebApplicationFactory factory)
{
    private const string Dashboard = "/api/v1/operations/dashboard?status=NOT_A_STATUS";

    [Fact]
    public async Task One_trace_id_is_in_the_log_line_the_audit_row_and_the_problem()
    {
        using var logs = new JsonConsoleCapture();
        await using var host = WithLogs(logs);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var traceId = await PlatformAdminProblemAsync(client, inbound: false);

        Assert.Matches(Obs003.TraceIdPattern(), traceId);
        Assert.Equal(1, await CountAuditRowsAsync("action='TENANT_CONTEXT_ACTIVATED' AND request_id=@value", traceId));
        var lines = logs.LinesOf(traceId);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.DoesNotContain("\"RequestId\"", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inbound_trace_context_reaches_neither_the_audit_row_nor_the_problem_nor_the_logs()
    {
        using var logs = new JsonConsoleCapture();
        await using var host = WithLogs(logs);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = await PlatformAdminProblemAsync(client, inbound: true);
        var second = await PlatformAdminProblemAsync(client, inbound: true);

        Assert.NotEqual(Obs003.InboundTraceId, first);
        Assert.NotEqual(Obs003.InboundTraceId, second);
        Assert.NotEqual(first, second);
        Assert.Equal(1, await CountAuditRowsAsync("action='TENANT_CONTEXT_ACTIVATED' AND request_id=@value", first));
        Assert.Equal(1, await CountAuditRowsAsync("action='TENANT_CONTEXT_ACTIVATED' AND request_id=@value", second));
        Assert.Equal(0, await CountAuditRowsAsync("strpos(request_id, @value) > 0", Obs003.InboundTraceId));
        Assert.NotEmpty(logs.LinesOf(first));
        Assert.NotEmpty(logs.LinesOf(second));
        Assert.All(logs.Lines, line =>
        {
            Assert.DoesNotContain(Obs003.InboundTraceId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("synthetic-obs003", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_tenant_context_problem_carries_the_trace_id_of_its_request()
    {
        using var logs = new JsonConsoleCapture();
        await using var host = WithLogs(logs);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, Dashboard);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActivePlatformAdminMfa);
        request.Headers.Add("X-Organization-Id", "not-an-organization");
        Obs003.AddInboundTraceContext(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The body is the AI-05 Problem, sent as application/problem+json (#244).
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid organization context.", body, StringComparison.Ordinal);
        var traceId = Obs003.ProblemTraceId(body);
        Assert.NotEqual(Obs003.InboundTraceId, traceId);
        Assert.NotEmpty(logs.LinesOf(traceId));
    }

    private WebApplicationFactory<Program> WithLogs(JsonConsoleCapture logs) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
                }));
            builder.ConfigureLogging(logs.AddTo);
        });

    /// <summary>
    /// A PLATFORM_ADMIN with MFA selects an organization (the tenant activation audit is written with the request
    /// id) and sends an invalid dashboard filter: the answer is a 400 problem.
    /// </summary>
    private static async Task<string> PlatformAdminProblemAsync(HttpClient client, bool inbound)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Dashboard);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActivePlatformAdminMfa);
        request.Headers.Add("X-Organization-Id", PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"));
        if (inbound)
        {
            Obs003.AddInboundTraceContext(request);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var traceId = Obs003.ProblemTraceId(await response.Content.ReadAsStringAsync());
        Assert.True(TraceCorrelation.IsTraceId(traceId));
        return traceId;
    }

    private async Task<int> CountAuditRowsAsync(string predicate, string value)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*)::integer FROM platform.audit_logs WHERE {predicate}",
            connection);
        command.Parameters.AddWithValue("value", value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
