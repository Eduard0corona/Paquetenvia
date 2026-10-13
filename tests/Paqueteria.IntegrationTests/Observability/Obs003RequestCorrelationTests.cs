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
/// <c>Accept</c>, and the <c>TraceId</c> of every JSON console line the request writes, as the API's own formatter
/// writes it with the host's scopes. The public tracking hub keeps its uniform body without it.
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

        // Every line of each request carries that request's id. The lines of a request are found by the hosting
        // request scope they were written in (set before any middleware), not by their TraceId, so a line of the
        // request without the id, or with another one, fails here.
        foreach (var traceId in new[] { json, html, plain })
        {
            var lines = logs.LinesOfRequestScope(logs.RequestScopeOf(traceId));
            Assert.Contains(lines, line => line.Contains("\"Category\":\"Microsoft.AspNetCore.Hosting.Diagnostics\"", StringComparison.Ordinal));
            Assert.All(lines, line => Assert.Equal(traceId, JsonConsoleCapture.TraceIdOf(line)));
        }

        foreach (var line in logs.Lines)
        {
            Assert.DoesNotContain(Obs003.InboundTraceId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Obs003.InboundParentSpanId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("synthetic-obs003", line, StringComparison.Ordinal);
            // One identifier per line. The capture formats with the API's own JSON console formatter and the host's
            // scopes (the hosting scope holds Kestrel's RequestId, the activity scope a second TraceId), so a console
            // that wrote scopes would fail here (the control is Scopes_would_be_seen_if_the_console_wrote_them).
            // Api_and_worker_json_consoles_do_not_include_scopes guards the configuration of both hosts.
            Assert.DoesNotContain("\"RequestId\"", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Scopes\"", line, StringComparison.Ordinal);
            if (JsonConsoleCapture.TraceIdOf(line) is { } lineTraceId)
            {
                Assert.True(TraceCorrelation.IsTraceId(lineTraceId), line);
            }
        }
    }

    [Fact]
    public async Task Scopes_would_be_seen_if_the_console_wrote_them()
    {
        // The control of the scope assertions above: with IncludeScopes on, the same capture writes the hosting
        // scope's RequestId into the lines of the request.
        using var logs = new JsonConsoleCapture();
        await using var api = new CorrelationApiFactory(logs, includeScopes: true);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var traceId = await ProblemAsync(client, "application/json", inbound: false);

        var lines = logs.LinesOfRequestScope(logs.RequestScopeOf(traceId));
        Assert.Contains(lines, line => line.Contains("\"Scopes\"", StringComparison.Ordinal) &&
                                       line.Contains("\"RequestId\"", StringComparison.Ordinal));
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

    private sealed class CorrelationApiFactory(JsonConsoleCapture logs, bool includeScopes = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            var settings = new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                // Hosting, routing and authentication lines too, so every request writes some.
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
            };
            if (includeScopes)
            {
                settings["Logging:Console:FormatterOptions:IncludeScopes"] = "true";
            }

            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logs.AddTo);
        }
    }
}
