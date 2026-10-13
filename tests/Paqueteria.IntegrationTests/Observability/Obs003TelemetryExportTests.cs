extern alias WorkerHost;

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Identity.Infrastructure.Mock;
using Locations.Infrastructure.Geocoding.GoogleMaps;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Paqueteria.Hosting.Observability;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.IntegrationTests.Hosting;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Observability;

/// <summary>
/// OBS-003 phase 4 PR 6 in the real host compositions, with the real Azure Monitor exporter over a transport that
/// keeps every ingestion request in memory instead of sending it: off by default with nothing registered, 10 % of the
/// request traces chosen by trace id, only allowlisted attributes on the wire, the organization pseudonym on spans and
/// log lines with an organization context, the metric tag allowlist (rate limiting included), propagation still a
/// no-op, and the start failing without the pseudonym key outside Development and Testing.
/// </summary>
/// <remarks>
/// The OpenTelemetry listeners are process wide, so these tests run alone (a collection without parallelization).
/// </remarks>
[Collection(TelemetryExportCollection.Name)]
public sealed class Obs003TelemetryExportTests
{
    private const string TraceHeader = "X-Obs003-Trace-Id";
    private const string RecordedHeader = "X-Obs003-Recorded";
    private const string OutboundProbePath = "/__obs003/export-outbound";
    private const string ActivePath = "/__tests/tenancy/active";
    private const string SecretQuery = "obs003-secret-query";
    private const string SecretCookie = "obs003-secret-cookie";
    private const string SecretAgent = "obs003-secret-agent";

    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=obs003;Username=unused;Password=unused;Timeout=1;Pooling=false";

    private static readonly TraceIdRatioBasedSampler Ratio = new(TelemetryExportPolicy.TraceSamplingRatio);

    /// <summary>Starts the process-wide recorder of real HTTP requests before the first host of this class exists.</summary>
    public Obs003TelemetryExportTests() => _ = RealHttpRequests.Instance;

    [Fact]
    public async Task The_export_is_off_by_default_registers_nothing_and_sends_nothing()
    {
        var capture = new IngestionCapture();
        // A connection string alone does not turn the export on.
        await using var api = new ApiFactory(capture, enabled: false);
        using var client = api.CreateClient();
        for (var index = 0; index < 20; index++)
        {
            using var response = await client.SendAsync(Request(HttpMethod.Get, ActivePath, MockIdentityProfiles.ActiveViewer, MockIdentityProfiles.ViewerOrganizationId));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("False", Single(response, RecordedHeader));
        }

        AssertNothingRegistered(api.Services, api.Descriptors);

        var workerCapture = new IngestionCapture();
        await using var worker = new WorkerFactory(workerCapture, enabled: false);
        AssertNothingRegistered(worker.Services, worker.Descriptors);

        Assert.Empty(capture.Requests);
        Assert.Empty(workerCapture.Requests);
    }

    [Fact]
    public async Task Ten_percent_of_the_request_traces_go_out_by_trace_id_with_only_allowlisted_attributes()
    {
        var capture = new IngestionCapture();
        await using var api = new ApiFactory(capture, enabled: true);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 120; index++)
        {
            using var member = await client.SendAsync(Hostile(Request(
                HttpMethod.Get, $"{ActivePath}?q={SecretQuery}", MockIdentityProfiles.ActiveViewer, MockIdentityProfiles.ViewerOrganizationId)));
            Assert.Equal(HttpStatusCode.OK, member.StatusCode);
            kinds.Add(Single(member, TraceHeader), "member");

            using var foreign = await client.SendAsync(Hostile(Request(
                HttpMethod.Get, ActivePath, MockIdentityProfiles.ActiveViewer, MockIdentityProfiles.ForeignOrganizationId)));
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            kinds.Add(Single(foreign, TraceHeader), "foreign");
        }

        for (var index = 0; index < 30; index++)
        {
            using var probe = await client.SendAsync(Hostile(new HttpRequestMessage(HttpMethod.Get, "/health/live")));
            kinds.Add(Single(probe, TraceHeader), "probe");
        }

        Assert.True(api.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));

        // The tracer listens process wide, so a request of another host in this process could be exported too: the
        // decision is checked on this test's own traces, and every exported item passes the allowlist checks below.
        var requests = capture.Items
            .Where(item => BaseType(item) == "RequestData" && kinds.ContainsKey(OperationId(item)))
            .ToArray();
        var exported = requests.Select(OperationId).Order(StringComparer.Ordinal).ToArray();
        var expected = kinds
            .Where(entry => entry.Value != "probe" && IsSampled(entry.Key))
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        // Exactly the sampled traces, decided from the trace id; no probe and nothing else.
        Assert.Equal(expected, exported);
        Assert.InRange(expected.Length, 6, 60);

        var pseudonym = api.Services.GetRequiredService<IOrganizationPseudonymizer>().Pseudonymize(MockIdentityProfiles.ViewerOrganizationId);
        Assert.Contains(requests, item => kinds[OperationId(item)] == "member");
        foreach (var item in requests)
        {
            Assert.Equal(10, item.GetProperty("sampleRate").GetDouble());
            Assert.False(item.GetProperty("tags").TryGetProperty("ai.operation.parentId", out _));
            var data = BaseData(item);
            Assert.Equal("GET " + ActivePath, data.GetProperty("name").GetString());
            var properties = Properties(data);
            if (kinds[OperationId(item)] == "member")
            {
                Assert.Equal("200", data.GetProperty("responseCode").GetString());
                Assert.Equal(
                    [
                        $"{TelemetryExportPolicy.ActorRoleKindAttribute}=VIEWER",
                        $"{TelemetryExportPolicy.OrganizationPseudonymAttribute}={pseudonym}",
                    ],
                    properties);
            }
            else
            {
                Assert.Equal("403", data.GetProperty("responseCode").GetString());
                Assert.Empty(properties);
            }
        }

        AssertNothingSensitiveOnTheWire(capture);
    }

    [Fact]
    public async Task Exported_metrics_keep_only_allowlisted_tags_including_the_rate_limiting_ones()
    {
        var capture = new IngestionCapture();
        await using var api = new ApiFactory(capture, enabled: true);
        using var client = api.CreateClient();
        var limited = 0;
        for (var index = 0; index < 3; index++)
        {
            using var negotiate = await client.SendAsync(Hostile(new HttpRequestMessage(HttpMethod.Post, "/hubs/tracking/negotiate?negotiateVersion=1")));
            limited += negotiate.StatusCode == HttpStatusCode.TooManyRequests ? 1 : 0;
        }

        Assert.True(limited > 0);
        (await client.GetAsync("/health/live")).Dispose();

        Assert.True(api.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000));

        var metrics = capture.Items.Where(item => BaseType(item) == "MetricData").ToArray();
        var points = metrics
            .SelectMany(item => BaseData(item).GetProperty("metrics").EnumerateArray()
                .Select(metric => (Name: metric.GetProperty("name").GetString()!, Properties: Properties(BaseData(item)))))
            .ToArray();
        var names = points.Select(point => point.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("http.server.request.duration", names);
        Assert.Contains("aspnetcore.rate_limiting.requests", names);
        Assert.Contains("rate_limit.rejected", names);
        Assert.All(
            points.SelectMany(point => point.Properties),
            property => Assert.Contains(property[..property.IndexOf('=', StringComparison.Ordinal)], TelemetryExportPolicy.MetricTagKeys));
        Assert.Contains(
            points,
            point => point.Name == "aspnetcore.rate_limiting.requests" &&
                     point.Properties.Contains("aspnetcore.rate_limiting.policy=Realtime.Connections") &&
                     point.Properties.Any(property => property.StartsWith("aspnetcore.rate_limiting.result=", StringComparison.Ordinal)));
        Assert.Contains(points, point => point.Name == "rate_limit.rejected" && point.Properties.SequenceEqual(["policy=Realtime.Connections"]));
        AssertNothingSensitiveOnTheWire(capture);
    }

    [Fact]
    public async Task With_the_export_on_inbound_context_is_ignored_and_no_outbound_request_carries_trace_headers()
    {
        await using var server = await TraceHeaderCaptureServer.StartAsync();
        var capture = new IngestionCapture();
        await using var api = new ApiFactory(capture, enabled: true, outbound: server);
        using var client = api.CreateClient();
        var traces = new List<string>();
        for (var index = 0; index < 80; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{OutboundProbePath}?call={index}");
            Obs003.AddInboundTraceContext(request);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var traceId = Single(response, TraceHeader);
            Assert.NotEqual(Obs003.InboundTraceId, traceId);
            traces.Add(traceId);
        }

        // The SDK changed neither the process propagator, nor the one hosting resolves, nor its own default.
        Assert.Empty(DistributedContextPropagator.Current.Fields);
        Assert.Equal("NoTraceContextPropagator", DistributedContextPropagator.Current.GetType().Name);
        Assert.Same(DistributedContextPropagator.Current, api.Services.GetRequiredService<DistributedContextPropagator>());
        Assert.Empty(Propagators.DefaultTextMapPropagator.Fields!);
        using (var activity = TraceCorrelation.StartJobCycle("obs003.propagator"))
        {
            var carrier = new Dictionary<string, string>();
            Propagators.DefaultTextMapPropagator.Inject(
                new PropagationContext(activity.Context, Baggage.Create(new Dictionary<string, string> { ["tenant"] = "synthetic-obs003-org" })),
                carrier,
                static (map, key, value) => map[key] = value);
            Assert.Empty(carrier);
        }

        Assert.Equal(80, server.Requests.Count);
        Assert.All(server.Requests, TraceHeaderCaptureServer.AssertNoTraceContext);

        Assert.True(api.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
        var sampled = traces.Where(IsSampled).ToHashSet(StringComparer.Ordinal);
        var dependencies = capture.Items
            .Where(item => BaseType(item) == "RemoteDependencyData" && traces.Contains(OperationId(item)))
            .ToArray();
        Assert.Equal(sampled.Order(StringComparer.Ordinal), dependencies.Select(OperationId).Order(StringComparer.Ordinal));
        Assert.NotEmpty(dependencies);
        foreach (var dependency in dependencies)
        {
            var data = BaseData(dependency);
            Assert.Equal("GET", data.GetProperty("name").GetString());
            Assert.Equal("200", data.GetProperty("resultCode").GetString());
            Assert.DoesNotContain("call=", dependency.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("obs003-outbound", dependency.GetRawText(), StringComparison.Ordinal);
            Assert.True(dependency.GetProperty("tags").TryGetProperty("ai.operation.parentId", out _));
        }

        Assert.DoesNotContain(Obs003.InboundTraceId, string.Join('\n', capture.Requests.Select(request => request.Body)), StringComparison.Ordinal);
        AssertNothingSensitiveOnTheWire(capture);
    }

    [Fact]
    public async Task The_exporter_sends_no_statsbeat_or_resource_metric_and_keeps_nothing_on_disk()
    {
        var capture = new IngestionCapture();
        await using var api = new ApiFactory(capture, enabled: true);
        using var client = api.CreateClient();
        for (var index = 0; index < 60; index++)
        {
            (await client.SendAsync(Request(HttpMethod.Get, ActivePath, MockIdentityProfiles.ActiveViewer, MockIdentityProfiles.ForeignOrganizationId))).Dispose();
        }

        Assert.True(api.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
        Assert.True(api.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000));

        Assert.Equal("true", Environment.GetEnvironmentVariable(AzureMonitorTelemetryExport.StatsbeatDisabledVariable));
        Assert.Equal("true", Environment.GetEnvironmentVariable(AzureMonitorTelemetryExport.SdkStatsDisabledVariable));
        Assert.Equal("false", Environment.GetEnvironmentVariable(AzureMonitorTelemetryExport.ResourceMetricVariable));

        // The recorder sees real requests (here a refused loopback connection), so what it lacks below was not sent.
        var check = new Uri($"http://127.0.0.1:1/obs003-recorder-{Guid.NewGuid():N}");
        using (var probe = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) }))
        {
            try
            {
                (await probe.GetAsync(check)).Dispose();
            }
            catch (HttpRequestException)
            {
                // Refused, as expected; the request was still started.
            }
        }

        Assert.Contains(check, RealHttpRequests.Instance.Uris);
        // The only destination is the ingestion endpoint (in memory here): since the first test of this class, every real
        // request of this process went to loopback (these tests' own servers), never to the instance metadata endpoint
        // (169.254.169.254, customer SDK stats), Statsbeat or Live Metrics.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.All(RealHttpRequests.Instance.Uris, uri => Assert.True(uri.IsLoopback, $"a real request left the process: {uri.GetLeftPart(UriPartial.Authority)}"));
        // The exporter's own transmitter for this connection string (pinned exporter 1.10.0 internals, test only).
        var transmitter = Transmitter(capture.ConnectionString);
        Assert.Null(Field(transmitter, "_statsbeat"));
        Assert.Null(Field(transmitter, "_fileBlobProvider"));
        Assert.Null(Field(transmitter, "_transmitFromStorageHandler"));
        Assert.Contains(capture.Items, item => BaseType(item) == "RequestData");
        Assert.DoesNotContain(capture.Items, item => BaseType(item) is not ("RequestData" or "RemoteDependencyData" or "MetricData"));
        AssertNothingSensitiveOnTheWire(capture);
    }

    [Fact]
    public void Only_scheduled_job_cycles_and_their_children_are_sampled_and_the_decision_follows_the_trace_id()
    {
        TraceCorrelation.ConfigureProcess();
        var collector = new CollectingExporter();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddPaqueteriaTracing(TelemetryExportProfile.Worker)
            .AddProcessor(new SimpleActivityExportProcessor(collector))
            .Build();
        using var outgoing = new ActivitySource(TelemetryExportPolicy.OutgoingHttpActivitySourceName);
        var jobs = Cycles(1_500, () => TraceCorrelation.StartJobCycle("orders.claim-window-finalization"), outgoing);
        var dispatch = Cycles(500, () => TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Notifications), outgoing);
        var polls = Cycles(500, () => TraceCorrelation.StartPollCycle("custody.proof-validation"), outgoing);
        var parentless = new List<string>();
        for (var index = 0; index < 500; index++)
        {
            Activity.Current = null;
            using var root = outgoing.StartActivity("System.Net.Http.HttpRequestOut", ActivityKind.Client);
            if (root is not null)
            {
                parentless.Add(root.TraceId.ToHexString());
            }
        }

        Assert.True(provider.ForceFlush(10_000));

        var expected = jobs.Where(IsSampled).Order(StringComparer.Ordinal).ToArray();
        Assert.InRange(expected.Length, 90, 220);
        var exported = collector.Items;
        Assert.Equal(
            expected,
            exported.Where(item => item.Operation == TraceCorrelation.JobCycleOperation && jobs.Contains(item.TraceId))
                .Select(item => item.TraceId)
                .Order(StringComparer.Ordinal));
        // Children follow their parent's decision.
        Assert.Equal(
            expected,
            exported.Where(item => item.Source == TelemetryExportPolicy.OutgoingHttpActivitySourceName && jobs.Contains(item.TraceId))
                .Select(item => item.TraceId)
                .Order(StringComparer.Ordinal));
        Assert.DoesNotContain(exported, item => dispatch.Contains(item.TraceId) || polls.Contains(item.TraceId) || parentless.Contains(item.TraceId));
        Assert.All(
            exported.Where(item => jobs.Contains(item.TraceId)),
            item => Assert.Equal(
                item.Source == TelemetryExportPolicy.OutgoingHttpActivitySourceName ? ["http.request.method=GET"] : [],
                item.Tags));
    }

    [Fact]
    public void Outbox_messages_are_exported_only_inside_a_sampled_producer_trace_as_children_without_attributes()
    {
        // OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13 with the PR 6 sampler: a message's processing follows the sampling
        // decision of the trace that wrote it, stored with the row; dispatch cycles and messages without a stored context
        // are never exported, and a context taken as remote would never be either.
        TraceCorrelation.ConfigureProcess();
        var collector = new CollectingExporter();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddPaqueteriaTracing(TelemetryExportProfile.Worker)
            .AddProcessor(new SimpleActivityExportProcessor(collector))
            .Build();

        // Producers: scheduled job cycles (an exported root, sampled 10 % by trace id) store their own context.
        var stored = new List<(string TraceParent, string TraceId, string SpanId, bool Sampled)>();
        for (var index = 0; index < 1_500; index++)
        {
            using var job = TraceCorrelation.StartJobCycle("orders.claim-window-finalization");
            var traceParent = OutboxTraceContext.Current();
            Assert.NotNull(traceParent);
            stored.Add((traceParent, job.TraceId.ToHexString(), job.SpanId.ToHexString(), job.Recorded));
        }

        // Consumers: each claimed message inside its lane's dispatch cycle, as a child of the stored context.
        var cycles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in stored)
        {
            using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Notifications);
            cycles.Add(cycle.TraceId.ToHexString());
            using var processing = TraceCorrelation.StartOutboxMessage(OutboxLanes.Notifications, message.TraceParent);
            Assert.Same(processing, Activity.Current);
            Assert.Equal(message.TraceId, processing.TraceId.ToHexString());
            Assert.Equal(message.SpanId, processing.ParentSpanId.ToHexString());
            Assert.Equal(message.Sampled, processing.Recorded);
            processing.SetTag("messaging.message.id", "synthetic-message-id");
        }

        // Rows written before the column or outside any trace: the message stays in the dispatch cycle's trace.
        var withoutContext = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 300; index++)
        {
            using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Dispatch);
            using var processing = TraceCorrelation.StartOutboxMessage(OutboxLanes.Dispatch, null);
            Assert.Equal(cycle.TraceId, processing.TraceId);
            Assert.False(processing.Recorded);
            withoutContext.Add(processing.TraceId.ToHexString());
        }

        // Control: the same sampled contexts taken as remote parents are dropped (PR 6), which is why the stored
        // context is read as a local parent.
        using var remoteSource = new ActivitySource(TraceCorrelation.ActivitySourceName);
        var remote = 0;
        foreach (var message in stored.Where(item => item.Sampled).Take(20))
        {
            Activity.Current = null;
            var parent = new ActivityContext(
                ActivityTraceId.CreateFromString(message.TraceId),
                ActivitySpanId.CreateFromString(message.SpanId),
                ActivityTraceFlags.Recorded,
                traceState: null,
                isRemote: true);
            using var taken = remoteSource.StartActivity("Obs003.RemoteControl", ActivityKind.Consumer, parent);
            Assert.False(taken?.Recorded ?? false);
            remote++;
        }

        Assert.True(provider.ForceFlush(10_000));

        var sampled = stored.Where(item => item.Sampled).ToArray();
        Assert.InRange(sampled.Length, 90, 220);
        Assert.InRange(remote, 1, 20);
        Assert.All(stored, item => Assert.Equal(IsSampled(item.TraceId), item.Sampled));
        var exported = collector.Items.Where(item => item.Operation == TraceCorrelation.OutboxMessageOperation).ToArray();
        Assert.Equal(
            sampled.Select(item => item.TraceId).Order(StringComparer.Ordinal),
            exported.Select(item => item.TraceId).Order(StringComparer.Ordinal));
        Assert.All(exported, item =>
        {
            Assert.Equal(TraceCorrelation.ActivitySourceName, item.Source);
            Assert.Equal(OutboxLanes.Notifications, item.DisplayName);
            Assert.Equal(ActivityKind.Consumer, item.Kind);
            Assert.Empty(item.Tags);
            Assert.Equal(sampled.Single(message => message.TraceId == item.TraceId).SpanId, item.ParentSpanId);
        });
        Assert.DoesNotContain(collector.Items, item => cycles.Contains(item.TraceId) || withoutContext.Contains(item.TraceId));
        Assert.DoesNotContain(collector.Items, item => item.Operation == "Obs003.RemoteControl");
    }

    [Fact]
    public async Task The_worker_exports_metrics_and_no_request_or_dispatch_cycle()
    {
        var capture = new IngestionCapture();
        await using var worker = new WorkerFactory(capture, enabled: true);
        using var client = worker.CreateClient();
        Assert.NotNull(worker.Services.GetService<TracerProvider>());
        Assert.NotNull(worker.Services.GetService<MeterProvider>());
        for (var index = 0; index < 5; index++)
        {
            (await client.GetAsync("/health/live")).Dispose();
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(worker.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000));
        Assert.True(worker.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000));

        Assert.DoesNotContain(capture.Items, item => BaseType(item) == "RequestData");
        Assert.Contains(capture.Items, item => BaseType(item) == "MetricData");
        Assert.All(
            capture.Items.Where(item => BaseType(item) == "RemoteDependencyData"),
            item => Assert.DoesNotContain(
                BaseData(item).GetProperty("name").GetString(),
                OutboxLanes.All.Append("custody.proof-validation").Append(TraceCorrelation.OutboxDispatchCycleOperation)));
        AssertNothingSensitiveOnTheWire(capture);
    }

    [Fact]
    public async Task Log_lines_with_an_organization_context_carry_the_pseudonym_and_role_kind_also_with_the_export_off()
    {
        using var logs = new JsonConsoleCapture();
        var capture = new IngestionCapture();
        await using var api = new ApiFactory(capture, enabled: false, logs: logs);
        using var client = api.CreateClient();

        using var member = await client.SendAsync(Request(HttpMethod.Get, ActivePath, MockIdentityProfiles.ActiveViewer, MockIdentityProfiles.ViewerOrganizationId));
        using var foreign = await client.SendAsync(Request(HttpMethod.Get, ActivePath, MockIdentityProfiles.ActiveViewer, MockIdentityProfiles.ForeignOrganizationId));
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        var pseudonym = api.Services.GetRequiredService<IOrganizationPseudonymizer>().Pseudonymize(MockIdentityProfiles.ViewerOrganizationId);
        Assert.StartsWith("v0-", pseudonym, StringComparison.Ordinal);
        var memberLines = logs.LinesOf(Single(member, TraceHeader)).Select(Parse).ToArray();
        var withContext = memberLines.Where(line => line.GetProperty("State").TryGetProperty(TelemetryOrganizationContext.PseudonymLogProperty, out _)).ToArray();
        Assert.NotEmpty(withContext);
        Assert.All(withContext, line =>
        {
            Assert.Equal(pseudonym, line.GetProperty("State").GetProperty(TelemetryOrganizationContext.PseudonymLogProperty).GetString());
            Assert.Equal("VIEWER", line.GetProperty("State").GetProperty(TelemetryOrganizationContext.RoleKindLogProperty).GetString());
        });
        Assert.All(
            logs.LinesOf(Single(foreign, TraceHeader)).Select(Parse),
            line => Assert.False(line.GetProperty("State").TryGetProperty(TelemetryOrganizationContext.PseudonymLogProperty, out _)));
        Assert.All(logs.Lines, line =>
        {
            Assert.DoesNotContain(MockIdentityProfiles.ViewerOrganizationId.ToString("D"), line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MockIdentityProfiles.ForeignOrganizationId.ToString("D"), line, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(capture.Requests);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_development_and_testing_neither_host_starts_without_the_pseudonym_key(string environment)
    {
        using var api = new KeylessFactory<Program>(environment);
        using var worker = new KeylessFactory<WorkerProgram>(environment);

        foreach (var start in new Action[] { () => _ = api.Services, () => _ = worker.Services })
        {
            var failure = Record.Exception(start);
            Assert.NotNull(failure);
            Assert.Contains("TelemetryPseudonym:Keys is required outside Development and Testing", Messages(failure), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_export_turned_on_without_a_connection_string_stops_the_host()
    {
        using var api = new InvalidExportFactory();

        var failure = Record.Exception(() => _ = api.Services);

        Assert.NotNull(failure);
        Assert.Contains("TelemetryExport:Enabled is true but TelemetryExport:ConnectionString is missing", Messages(failure), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private static bool IsSampled(string traceId) =>
        Ratio.ShouldSample(new SamplingParameters(default, ActivityTraceId.CreateFromString(traceId), "root", ActivityKind.Server)).Decision ==
        SamplingDecision.RecordAndSample;

    private static HashSet<string> Cycles(int count, Func<Activity> start, ActivitySource outgoing)
    {
        var traces = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            using var cycle = start();
            traces.Add(cycle.TraceId.ToHexString());
            using var child = outgoing.StartActivity("System.Net.Http.HttpRequestOut", ActivityKind.Client);
            child?.SetTag("http.request.method", "GET");
            child?.SetTag("url.full", "https://graph.facebook.invalid/v20.0/synthetic-sender-id/messages");
        }

        return traces;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string profile, Guid organization)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add("X-Organization-Id", organization.ToString("D"));
        return request;
    }

    /// <summary>Everything a request could leak: an inbound trace context, a cookie and a user agent.</summary>
    private static HttpRequestMessage Hostile(HttpRequestMessage request)
    {
        Obs003.AddInboundTraceContext(request);
        request.Headers.Add("Cookie", $"session={SecretCookie}");
        request.Headers.UserAgent.ParseAdd(SecretAgent);
        return request;
    }

    private static void AssertNothingSensitiveOnTheWire(IngestionCapture capture)
    {
        Assert.NotEmpty(capture.Requests);
        foreach (var request in capture.Requests)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(IngestionCapture.Host, request.Uri.Host);
            Assert.Equal("/v2.1/track", request.Uri.AbsolutePath);
            foreach (var header in Obs003.TraceHeaders)
            {
                Assert.DoesNotContain(header, request.HeaderNames);
            }

            Assert.DoesNotContain("_OTELRESOURCE_", request.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("telemetry.sdk", request.Body, StringComparison.Ordinal);
            foreach (var secret in new[]
                     {
                         SecretQuery, SecretCookie, SecretAgent, Obs003.InboundTraceId, Obs003.InboundParentSpanId, "synthetic-obs003",
                         MockIdentityProfiles.ViewerOrganizationId.ToString("D"), MockIdentityProfiles.ForeignOrganizationId.ToString("D"),
                         "mock-subject", "graph.facebook.invalid/v20.0", "synthetic-sender-id", "Username=", "url.full",
                         "url.path", "client.address", "user_agent", "enduser",
                     })
            {
                Assert.DoesNotContain(secret, request.Body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static void AssertNothingRegistered(IServiceProvider services, IReadOnlyList<ServiceDescriptor> descriptors)
    {
        Assert.Null(services.GetService<TracerProvider>());
        Assert.Null(services.GetService<MeterProvider>());
        Assert.DoesNotContain(descriptors, descriptor =>
            (descriptor.ServiceType.Namespace ?? string.Empty).StartsWith("OpenTelemetry", StringComparison.Ordinal) ||
            (descriptor.ServiceType.Namespace ?? string.Empty).StartsWith("Azure.Monitor", StringComparison.Ordinal) ||
            (descriptor.ImplementationType?.Namespace ?? string.Empty).StartsWith("OpenTelemetry", StringComparison.Ordinal));
        Assert.DoesNotContain(descriptors, descriptor => descriptor.IsKeyedService is false && descriptor.ServiceType == typeof(HttpPipelineTransport));
    }

    private static string Single(HttpResponseMessage response, string header) => Assert.Single(response.Headers.GetValues(header));

    private static object Transmitter(string connectionString)
    {
        var factory = typeof(AzureMonitorTraceExporter).Assembly
            .GetType("Azure.Monitor.OpenTelemetry.Exporter.Internals.TransmitterFactory", throwOnError: true)!;
        var instance = factory.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var transmitters = (IDictionary)Field(instance, "_transmitters")!;
        return transmitters[connectionString]!;
    }

    private static object? Field(object instance, string name) =>
        (instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{instance.GetType().Name} has no field {name}"))
        .GetValue(instance);

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    private static string BaseType(JsonElement item) =>
        item.TryGetProperty("data", out var data) && data.TryGetProperty("baseType", out var type) ? type.GetString() ?? string.Empty : string.Empty;

    private static JsonElement BaseData(JsonElement item) => item.GetProperty("data").GetProperty("baseData");

    private static string OperationId(JsonElement item) => item.GetProperty("tags").GetProperty("ai.operation.id").GetString()!;

    private static string[] Properties(JsonElement data) =>
        data.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => $"{property.Name}={property.Value.GetString()}").Order(StringComparer.Ordinal).ToArray()
            : [];

    private static string Messages(Exception exception)
    {
        var builder = new StringBuilder();
        var pending = new Stack<Exception>([exception]);
        while (pending.TryPop(out var current))
        {
            builder.AppendLine(current.Message);
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The ingestion endpoint, in memory: every request the Azure Monitor exporter sends lands here and gets the answer
    /// the service gives when it accepts every item. A fresh instrumentation key per test gives each host its own
    /// exporter transmitter (the exporter shares one per connection string in the process).
    /// </summary>
    private sealed class IngestionCapture : HttpMessageHandler
    {
        internal const string Host = "mexicocentral-0.in.applicationinsights.azure.com";
        private readonly ConcurrentQueue<CapturedIngestion> _requests = new();

        internal IngestionCapture()
        {
            ConnectionString = $"InstrumentationKey={Guid.NewGuid():D};IngestionEndpoint=https://{Host}/";
            Transport = new HttpClientTransport(new HttpClient(this));
        }

        internal string ConnectionString { get; }

        internal HttpPipelineTransport Transport { get; }

        internal IReadOnlyList<CapturedIngestion> Requests => _requests.ToArray();

        internal IReadOnlyList<JsonElement> Items => Requests.SelectMany(request => request.Items).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var items = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToArray();
            var headers = request.Headers.Select(header => header.Key)
                .Concat(request.Content?.Headers.Select(header => header.Key) ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _requests.Enqueue(new CapturedIngestion(request.Method, request.RequestUri!, headers, body, items));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"itemsReceived":{{items.Length}},"itemsAccepted":{{items.Length}},"errors":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record CapturedIngestion(HttpMethod Method, Uri Uri, IReadOnlySet<string> HeaderNames, string Body, IReadOnlyList<JsonElement> Items);

    /// <summary>
    /// Every request a real HTTP handler (<see cref="SocketsHttpHandler"/>, through its diagnostics handler) starts in this
    /// process, from the first test of this class on. The in-memory ingestion transport is not a real handler, so the
    /// exporter's ingestion never shows here; anything else it sent (Statsbeat, Live Metrics, the instance metadata
    /// endpoint) would. Only the request event is enabled, so no activity is created and no header is injected.
    /// </summary>
    private sealed class RealHttpRequests : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private const string ListenerName = "HttpHandlerDiagnosticListener";
        private const string RequestEvent = "System.Net.Http.Request";
        private readonly ConcurrentQueue<Uri> _uris = new();
        private readonly ConcurrentBag<IDisposable> _subscriptions = [];

        private RealHttpRequests() => _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

        internal static RealHttpRequests Instance { get; } = new();

        internal IReadOnlyList<Uri> Uris => _uris.ToArray();

        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
        {
            if (value.Name == ListenerName)
            {
                _subscriptions.Add(value.Subscribe(this, name => name == RequestEvent));
            }
        }

        void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RequestEvent &&
                value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) is HttpRequestMessage { RequestUri: { } uri })
            {
                _uris.Enqueue(uri);
            }
        }

        void IObserver<DiagnosticListener>.OnCompleted()
        {
        }

        void IObserver<DiagnosticListener>.OnError(Exception error)
        {
        }

        void IObserver<KeyValuePair<string, object?>>.OnCompleted()
        {
        }

        void IObserver<KeyValuePair<string, object?>>.OnError(Exception error)
        {
        }
    }

    private sealed record ExportedSpan(
        string Source,
        string Operation,
        string TraceId,
        string[] Tags,
        string DisplayName,
        string ParentSpanId,
        ActivityKind Kind);

    /// <summary>An exporter that keeps what the pipeline hands it, after the allowlist ran.</summary>
    private sealed class CollectingExporter : BaseExporter<Activity>
    {
        private readonly ConcurrentQueue<ExportedSpan> _items = new();

        internal IReadOnlyList<ExportedSpan> Items => _items.ToArray();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                _items.Enqueue(new ExportedSpan(
                    activity.Source.Name,
                    activity.OperationName,
                    activity.TraceId.ToHexString(),
                    activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}").Order(StringComparer.Ordinal).ToArray(),
                    activity.DisplayName,
                    activity.ParentSpanId.ToHexString(),
                    activity.Kind));
            }

            return ExportResult.Success;
        }
    }

    /// <summary>
    /// The API in Testing with the mock identity, SignalR (one connection a window, so a second negotiate is limited)
    /// and the export on or off; the ingestion transport is the capture. The outermost middleware writes each request's
    /// trace id and whether its activity is recorded in response headers; an optional probe, after the whole pipeline,
    /// calls the Google Maps client against a loopback server.
    /// </summary>
    private sealed class ApiFactory(
        IngestionCapture capture,
        bool enabled,
        TraceHeaderCaptureServer? outbound = null,
        JsonConsoleCapture? logs = null) : WebApplicationFactory<Program>
    {
        internal IReadOnlyList<ServiceDescriptor> Descriptors { get; private set; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Mock",
                ["IdentityBootstrap:Provider"] = "Mock",
                ["Realtime:Provider"] = "SignalR",
                ["Realtime:AllowedOrigins:0"] = "https://web.synthetic.local",
                ["Realtime:ConnectionPermitLimit"] = "1",
                ["Realtime:ConnectionWindowSeconds"] = "300",
                ["ConnectionStrings:Paqueteria"] = UnreachableDatabase,
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
            }));
            UseExportSettings(builder, capture, enabled);
            if (logs is not null)
            {
                builder.ConfigureLogging(logs.AddTo);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddKeyedSingleton(AzureMonitorTelemetryExport.TransportServiceKey, capture.Transport);
                services.AddSingleton<IStartupFilter>(new TraceHeadersStartupFilter(outbound));
                Descriptors = services.Where(descriptor => !descriptor.IsKeyedService || !Equals(descriptor.ServiceKey, AzureMonitorTelemetryExport.TransportServiceKey)).ToArray();
            });
        }
    }

    /// <summary>The Worker in Production (with the synthetic pseudonym key) and the export on or off.</summary>
    private sealed class WorkerFactory(IngestionCapture capture, bool enabled) : WebApplicationFactory<WorkerProgram>
    {
        internal IReadOnlyList<ServiceDescriptor> Descriptors { get; private set; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(TelemetryPseudonymTestKey.With(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaqueteriaWorker"] = UnreachableDatabase,
                ["Orders:ClaimWindowFinalization:Enabled"] = "true",
                ["Orders:ClaimWindowFinalization:PollIntervalSeconds"] = "1",
                ["Dispatch:AssignmentLifecycle:Provider"] = "PostgreSql",
                ["Dispatch:AssignmentLifecycle:PollIntervalMilliseconds"] = "100",
            })));
            UseExportSettings(builder, capture, enabled);
            builder.ConfigureTestServices(services =>
            {
                services.AddKeyedSingleton(AzureMonitorTelemetryExport.TransportServiceKey, capture.Transport);
                Descriptors = services.Where(descriptor => !descriptor.IsKeyedService || !Equals(descriptor.ServiceKey, AzureMonitorTelemetryExport.TransportServiceKey)).ToArray();
            });
        }
    }

    /// <summary>A host in <paramref name="environment"/> with no pseudonym key at all.</summary>
    private sealed class KeylessFactory<TEntryPoint>(string environment) : StartupFailureSurfacingWebApplicationFactory<TEntryPoint>
        where TEntryPoint : class
    {
        protected override void ConfigureHost(IWebHostBuilder builder) => builder.UseEnvironment(environment);
    }

    private sealed class InvalidExportFactory : StartupFailureSurfacingWebApplicationFactory<Program>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("TelemetryExport:Enabled", "true");
        }
    }

    /// <summary>
    /// The hosts read <c>TelemetryExport</c> while they compose (the export is registered or not), so the test sets it as
    /// a host setting, which the factory hands to the entry point before its code runs, like the pilot's environment.
    /// </summary>
    private static void UseExportSettings(IWebHostBuilder builder, IngestionCapture capture, bool enabled)
    {
        builder.UseSetting("TelemetryExport:Enabled", enabled ? "true" : "false");
        builder.UseSetting("TelemetryExport:ConnectionString", capture.ConnectionString);
    }

    private sealed class TraceHeadersStartupFilter(TraceHeaderCaptureServer? outbound) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(static (context, nextMiddleware) =>
            {
                var activity = context.Features.Get<IHttpActivityFeature>()?.Activity;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[TraceHeader] = activity?.TraceId.ToHexString();
                    context.Response.Headers[RecordedHeader] = (activity?.Recorded ?? false).ToString();
                    return Task.CompletedTask;
                });
                return nextMiddleware(context);
            });
            next(app);
            if (outbound is null)
            {
                return;
            }

            app.Run(async context =>
            {
                if (context.Request.Path != OutboundProbePath)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var clients = context.RequestServices.GetRequiredService<IHttpClientFactory>();
                using var google = clients.CreateClient(GoogleMapsGeocodingProvider.HttpClientName);
                (await google.GetAsync(outbound.For($"obs003-outbound?{context.Request.QueryString.Value?.TrimStart('?')}"), context.RequestAborted)).Dispose();
                await context.Response.WriteAsync("ok", context.RequestAborted);
            });
        };
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryExportCollection
{
    public const string Name = "OBS-003 telemetry export (process-wide OpenTelemetry listeners)";
}
