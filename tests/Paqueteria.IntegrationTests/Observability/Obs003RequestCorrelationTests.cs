using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Realtime;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// OBS-003 in the real API pipeline (no database): inbound <c>traceparent</c>, <c>tracestate</c> and <c>baggage</c>
/// are ignored, so every request is a new trace; its 32-hex id is the problem <c>traceId</c>, whatever the request's
/// <c>Accept</c>, and the <c>TraceId</c> of every JSON console line the request writes. The public tracking hub keeps
/// its uniform body without it.
/// </summary>
public sealed class Obs003RequestCorrelationTests
{
    private const string IdentityProbe = "/__tests/security/authenticated";

    [Fact]
    public async Task Inbound_trace_context_is_ignored_and_the_problem_and_log_lines_carry_the_new_trace_id()
    {
        using var logs = new JsonConsoleCapture();
        await using var api = new CorrelationApiFactory(logs);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Activity.Current = null;

        var json = await ProblemAsync(client, "application/json", inbound: true);
        var html = await ProblemAsync(client, "text/html", inbound: true);
        var plain = await ProblemAsync(client, accept: null, inbound: false);

        Assert.Equal(3, new[] { json, html, plain }.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(Obs003.InboundTraceId, new[] { json, html, plain });

        // Every line of each request carries that request's id, and nothing inbound reaches any line.
        foreach (var traceId in new[] { json, html, plain })
        {
            var lines = logs.LinesOf(traceId);
            Assert.NotEmpty(lines);
            Assert.Contains(lines, line => line.Contains("\"Category\":\"Microsoft.AspNetCore.Hosting.Diagnostics\"", StringComparison.Ordinal));
        }

        foreach (var line in logs.Lines)
        {
            Assert.DoesNotContain(Obs003.InboundTraceId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Obs003.InboundParentSpanId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("synthetic-obs003", line, StringComparison.Ordinal);
            // One identifier per line: no hosting scope (RequestId, ConnectionId) is written by the JSON console.
            Assert.DoesNotContain("\"RequestId\"", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Scopes\"", line, StringComparison.Ordinal);
            if (JsonConsoleCapture.TraceIdOf(line) is { } lineTraceId)
            {
                Assert.True(TraceCorrelation.IsTraceId(lineTraceId), line);
            }
        }
    }

    [Fact]
    public async Task Private_hub_problems_carry_the_trace_id_and_the_tracking_hub_keeps_its_uniform_body()
    {
        await using var realtime = new RealtimeWebApplicationFactory();
        using var client = realtime.CreateClient();

        using var operations = new HttpRequestMessage(HttpMethod.Post, "/hubs/operations/negotiate?negotiateVersion=1");
        Obs003.AddInboundTraceContext(operations);
        using var unauthorized = await client.SendAsync(operations);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("application/problem+json", unauthorized.Content.Headers.ContentType?.MediaType);
        var body = await unauthorized.Content.ReadAsStringAsync();
        Assert.NotEqual(Obs003.InboundTraceId, Obs003.ProblemTraceId(body));
        using (var problem = JsonDocument.Parse(body))
        {
            Assert.Equal(
                ["status", "title", "traceId", "type"],
                problem.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }

        using var tracking = new HttpRequestMessage(
            HttpMethod.Post,
            "/hubs/tracking/negotiate?negotiateVersion=1&access_token=unknown-obs003-token");
        Obs003.AddInboundTraceContext(tracking);
        using var notFound = await client.SendAsync(tracking);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(
            """{"type":"about:blank","title":"Not Found","status":404}""",
            await notFound.Content.ReadAsStringAsync());
    }

    private static async Task<string> ProblemAsync(HttpClient client, string? accept, bool inbound)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, IdentityProbe);
        if (accept is not null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        if (inbound)
        {
            Obs003.AddInboundTraceContext(request);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return Obs003.ProblemTraceId(await response.Content.ReadAsStringAsync());
    }

    private sealed class CorrelationApiFactory(JsonConsoleCapture logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Provider"] = "Mock",
                    ["IdentityBootstrap:Provider"] = "Mock",
                    // Hosting, routing and authentication lines too, so every request writes some.
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
                }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }
}
