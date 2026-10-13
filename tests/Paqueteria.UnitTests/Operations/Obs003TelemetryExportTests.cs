using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OBS-003 phase 4 PR 6, without the OpenTelemetry SDK: the <c>TelemetryExport</c> section fails closed, the span
/// attribute allowlist, the exported roots, the organization pseudonym (fixed vectors, key rules, start failure outside
/// Development and Testing) and the pseudonym and role kind on the log lines of an organization context.
/// </summary>
public sealed class Obs003TelemetryExportTests
{
    private static readonly Guid Organization = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // ----------------------------------------------------------------------------------------------- pseudonym

    [Fact]
    public void The_pseudonym_matches_its_fixed_vectors()
    {
        // HMAC-SHA256(key, "paquetenvia.telemetry.organization-pseudonym.v1\n" + id), first 16 bytes as lowercase hex.
        Assert.Equal("v1-92d9e53b9ba938cafe99ce9cbd949926", OrganizationPseudonym.Compute(SyntheticKey(0), 1, Organization));
        Assert.Equal("v2-b5a56ecd32a82fb876464b7adabb9ce6", OrganizationPseudonym.Compute(SyntheticKey(1), 2, Organization));
        // The Development and Testing key: SHA-256 of a public constant, version 0.
        Assert.Equal("v0-a7d07edf91e714c657196d1683f30a96", OrganizationPseudonym.Compute(OrganizationPseudonym.SyntheticKey(), 0, Organization));
    }

    [Fact]
    public void The_pseudonym_carries_its_key_version_and_never_the_organization_id()
    {
        var other = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var first = OrganizationPseudonym.Compute(SyntheticKey(0), 7, Organization);
        Assert.StartsWith("v7-", first, StringComparison.Ordinal);
        Assert.True(OrganizationPseudonym.IsPseudonym(first));
        Assert.NotEqual(first, OrganizationPseudonym.Compute(SyntheticKey(0), 7, other));
        Assert.NotEqual(first[3..], OrganizationPseudonym.Compute(SyntheticKey(1), 7, Organization)[3..]);
        Assert.DoesNotContain(Organization.ToString("N"), first, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => OrganizationPseudonym.Compute(SyntheticKey(0), 1, Guid.Empty));
        Assert.False(OrganizationPseudonym.IsPseudonym(Organization.ToString("D")));
        Assert.False(OrganizationPseudonym.IsPseudonym("v1-" + new string('A', 32)));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("DevSynthetic")]
    public async Task Outside_development_and_testing_the_host_fails_to_start_without_a_key(string environment)
    {
        using var host = PseudonymHost(environment, []);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("TelemetryPseudonym:Keys is required outside Development and Testing", string.Join(' ', failure.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Development_and_testing_use_the_synthetic_key_version_0(string environment)
    {
        using var host = PseudonymHost(environment, []);
        await host.StartAsync();

        var pseudonymizer = host.Services.GetRequiredService<IOrganizationPseudonymizer>();
        Assert.Equal(0, pseudonymizer.KeyVersion);
        Assert.Equal("v0-a7d07edf91e714c657196d1683f30a96", pseudonymizer.Pseudonymize(Organization));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_configured_key_is_used_with_its_version(string environment)
    {
        using var host = PseudonymHost(environment, new()
        {
            ["TelemetryPseudonym:CurrentKeyVersion"] = "1",
            ["TelemetryPseudonym:Keys:1"] = Convert.ToBase64String(SyntheticKey(0)),
        });
        await host.StartAsync();

        var pseudonymizer = host.Services.GetRequiredService<IOrganizationPseudonymizer>();
        Assert.Equal(1, pseudonymizer.KeyVersion);
        Assert.Equal("v1-92d9e53b9ba938cafe99ce9cbd949926", pseudonymizer.Pseudonymize(Organization));
        await host.StopAsync();
    }

    public static TheoryData<string, Dictionary<string, string?>> InvalidKeys() => new()
    {
        { "Development", new() { ["TelemetryPseudonym:Keys:1"] = "not base64!" } },
        { "Production", new() { ["TelemetryPseudonym:Keys:1"] = Convert.ToBase64String(new byte[16]) } },
        { "Production", new() { ["TelemetryPseudonym:Keys:1"] = Convert.ToBase64String(new byte[129]) } },
        { "Testing", new() { ["TelemetryPseudonym:CurrentKeyVersion"] = "0", ["TelemetryPseudonym:Keys:0"] = Convert.ToBase64String(SyntheticKey(0)) } },
        { "Production", new() { ["TelemetryPseudonym:CurrentKeyVersion"] = "2", ["TelemetryPseudonym:Keys:1"] = Convert.ToBase64String(SyntheticKey(0)) } },
        {
            "Production", new()
            {
                ["TelemetryPseudonym:CurrentKeyVersion"] = "2",
                ["TelemetryPseudonym:Keys:1"] = Convert.ToBase64String(SyntheticKey(0)),
                ["TelemetryPseudonym:Keys:2"] = Convert.ToBase64String(SyntheticKey(1)),
            }
        },
    };

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public async Task An_invalid_key_fails_the_start_in_every_environment(string environment, Dictionary<string, string?> settings)
    {
        using var host = PseudonymHost(environment, settings);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("TelemetryPseudonym must hold exactly one key", string.Join(' ', failure.Failures), StringComparison.Ordinal);
        // The message names the settings, never a key value.
        foreach (var value in settings.Values.Where(value => value!.Length > 8))
        {
            Assert.DoesNotContain(value!, failure.Message, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------ export settings

    [Fact]
    public void The_export_is_off_by_default()
    {
        var settings = TelemetryExportSettings.Read(Configuration([]));

        Assert.False(settings.Enabled);
        Assert.Null(settings.ConnectionString);
    }

    [Fact]
    public void A_valid_connection_string_turns_the_export_on_only_with_enabled()
    {
        var connection = ConnectionString();
        var off = TelemetryExportSettings.Read(Configuration(new() { ["TelemetryExport:ConnectionString"] = connection }));
        var on = TelemetryExportSettings.Read(Configuration(new()
        {
            ["TelemetryExport:Enabled"] = "true",
            ["TelemetryExport:ConnectionString"] = connection,
        }));

        Assert.False(off.Enabled);
        Assert.True(on.Enabled);
        Assert.Equal(connection, on.ConnectionString);
    }

    public static TheoryData<Dictionary<string, string?>> InvalidExportSettings() => new()
    {
        new() { ["TelemetryExport:Enabled"] = "true" },
        new() { ["TelemetryExport:Enabled"] = "yes", ["TelemetryExport:ConnectionString"] = ConnectionString() },
        new() { ["TelemetryExport:SamplingRatio"] = "1" },
        new() { ["TelemetryExport:ConnectionString"] = ConnectionString().Replace("https://", "http://", StringComparison.Ordinal) },
        new() { ["TelemetryExport:ConnectionString"] = ConnectionString().Replace(".applicationinsights.azure.com", ".collector.example.invalid", StringComparison.Ordinal) },
        new() { ["TelemetryExport:ConnectionString"] = "IngestionEndpoint=https://mexicocentral-0.in.applicationinsights.azure.com/" },
        new() { ["TelemetryExport:ConnectionString"] = ConnectionString() + ";Authorization=aad" },
        new() { ["TelemetryExport:ConnectionString"] = ConnectionString().Replace("azure.com/", "azure.com/v2.1?x=1", StringComparison.Ordinal) },
        new() { ["TelemetryExport:ConnectionString"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://mexicocentral-0.in.applicationinsights.azure.com/" },
    };

    [Theory]
    [MemberData(nameof(InvalidExportSettings))]
    public void An_invalid_export_section_fails_closed(Dictionary<string, string?> settings)
    {
        var failure = Assert.Throws<TelemetryExportConfigurationException>(() => TelemetryExportSettings.Read(Configuration(settings)));

        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("0f1e2d3c", failure.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------- span allowlist

    [Fact]
    public void Only_request_roots_and_scheduled_job_cycles_can_be_sampled()
    {
        Assert.True(TelemetryExportPolicy.IsExportedRoot(TelemetryExportPolicy.HostingRequestOperation, ActivityKind.Server));
        Assert.True(TelemetryExportPolicy.IsExportedRoot(TraceCorrelation.JobCycleOperation, ActivityKind.Internal));
        Assert.False(TelemetryExportPolicy.IsExportedRoot(TraceCorrelation.OutboxDispatchCycleOperation, ActivityKind.Internal));
        Assert.False(TelemetryExportPolicy.IsExportedRoot(TraceCorrelation.PollCycleOperation, ActivityKind.Internal));
        Assert.False(TelemetryExportPolicy.IsExportedRoot(TraceCorrelation.RequestOperation, ActivityKind.Internal));
        Assert.False(TelemetryExportPolicy.IsExportedRoot("System.Net.Http.HttpRequestOut", ActivityKind.Client));
        Assert.False(TelemetryExportPolicy.IsExportedRoot("postgresql", ActivityKind.Client));
        Assert.Equal(0.10, TelemetryExportPolicy.TraceSamplingRatio);
    }

    [Fact]
    public void A_request_span_keeps_only_the_allowlisted_attributes()
    {
        using var recorded = new RecordedSource(TelemetryExportPolicy.HostingActivitySourceName);
        using var activity = recorded.Start(TelemetryExportPolicy.HostingRequestOperation, ActivityKind.Server);
        TelemetryExportPolicy.EnrichRequest(activity, "GET", "/api/v1/tracking/{token}", 404);
        TelemetryExportPolicy.SetOrganization(activity, "v1-92d9e53b9ba938cafe99ce9cbd949926", "DISPATCHER,FINANCE");
        foreach (var (key, value) in SensitiveTags())
        {
            activity.SetTag(key, value);
        }

        activity.SetStatus(ActivityStatusCode.Error, "synthetic failure for obs003@example.invalid");
        activity.Stop();

        Assert.True(TelemetryExportPolicy.PrepareForExport(activity));
        Assert.Equal(
            [
                "http.request.method=GET",
                "http.response.status_code=404",
                "http.route=/api/v1/tracking/{token}",
                "paqueteria.actor.role_kind=DISPATCHER,FINANCE",
                "paqueteria.organization.pseudonym=v1-92d9e53b9ba938cafe99ce9cbd949926",
            ],
            Tags(activity));
        Assert.Equal("GET /api/v1/tracking/{token}", activity.DisplayName);
        Assert.Null(activity.StatusDescription);
        Assert.True(activity.Recorded);
    }

    [Fact]
    public void Allowlisted_attributes_with_an_unexpected_shape_are_removed()
    {
        using var recorded = new RecordedSource(TelemetryExportPolicy.HostingActivitySourceName);
        using var activity = recorded.Start(TelemetryExportPolicy.HostingRequestOperation, ActivityKind.Server);
        TelemetryExportPolicy.EnrichRequest(activity, "GET<script>", "/api/v1/orders/obs003 secret", 200);
        activity.SetTag(TelemetryExportPolicy.OrganizationPseudonymAttribute, Organization.ToString("D"));
        activity.SetTag(TelemetryExportPolicy.ActorRoleKindAttribute, "obs003@example.invalid");
        activity.Stop();

        Assert.True(TelemetryExportPolicy.PrepareForExport(activity));
        Assert.Equal(["http.request.method=_OTHER", "http.response.status_code=200"], Tags(activity));
        Assert.Equal("_OTHER", activity.DisplayName);
    }

    [Fact]
    public void An_outgoing_http_span_never_keeps_its_url()
    {
        using var recorded = new RecordedSource(TelemetryExportPolicy.OutgoingHttpActivitySourceName);
        using var activity = recorded.Start("System.Net.Http.HttpRequestOut", ActivityKind.Client);
        activity.SetTag("http.request.method", "POST");
        activity.SetTag("server.address", "graph.facebook.invalid");
        activity.SetTag("server.port", 443);
        activity.SetTag("url.full", "https://graph.facebook.invalid/v20.0/synthetic-sender-id/messages?*");
        activity.SetTag("http.response.status_code", 500);
        activity.SetTag("error.type", "500");
        activity.SetTag("network.peer.address", "203.0.113.7");
        activity.Stop();

        Assert.True(TelemetryExportPolicy.PrepareForExport(activity));
        Assert.Equal(
            ["error.type=500", "http.request.method=POST", "http.response.status_code=500", "server.address=graph.facebook.invalid", "server.port=443"],
            Tags(activity));
        Assert.Equal("POST", activity.DisplayName);
    }

    [Fact]
    public void A_scheduled_job_cycle_keeps_no_attribute_and_its_fixed_name()
    {
        using var recorded = new RecordedSource(TraceCorrelation.ActivitySourceName);
        using var activity = recorded.Start(TraceCorrelation.JobCycleOperation, ActivityKind.Internal);
        activity.DisplayName = "orders.claim-window-finalization";
        activity.SetTag("db.query.text", "select 1");
        activity.Stop();

        Assert.True(TelemetryExportPolicy.PrepareForExport(activity));
        Assert.Empty(Tags(activity));
        Assert.Equal("orders.claim-window-finalization", activity.DisplayName);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("link")]
    [InlineData("baggage")]
    [InlineData("database")]
    [InlineData("dispatch")]
    [InlineData("poll")]
    public void A_span_that_cannot_be_cleaned_or_is_not_exported_is_taken_out(string kind)
    {
        var (sourceName, operation, activityKind) = kind switch
        {
            "database" => ("Npgsql", "postgresql", ActivityKind.Client),
            "dispatch" => (TraceCorrelation.ActivitySourceName, TraceCorrelation.OutboxDispatchCycleOperation, ActivityKind.Internal),
            "poll" => (TraceCorrelation.ActivitySourceName, TraceCorrelation.PollCycleOperation, ActivityKind.Internal),
            _ => (TelemetryExportPolicy.OutgoingHttpActivitySourceName, "System.Net.Http.HttpRequestOut", ActivityKind.Client),
        };
        using var recorded = new RecordedSource(sourceName);
        using var activity = kind == "link"
            ? recorded.Start(operation, activityKind, [new ActivityLink(new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None))])
            : recorded.Start(operation, activityKind);
        switch (kind)
        {
            case "event":
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { ["exception.message"] = "obs003@example.invalid" }));
                break;
            case "baggage":
                activity.AddBaggage("tenant", "synthetic-obs003-org");
                break;
        }

        activity.Stop();

        Assert.False(TelemetryExportPolicy.PrepareForExport(activity));
        Assert.False(activity.Recorded);
        Assert.False(activity.IsAllDataRequested);
    }

    [Fact]
    public void Exported_metric_tags_never_name_an_identifier_an_address_the_pseudonym_or_the_role()
    {
        Assert.Contains("aspnetcore.rate_limiting.policy", TelemetryExportPolicy.MetricTagKeys);
        Assert.Contains("aspnetcore.rate_limiting.result", TelemetryExportPolicy.MetricTagKeys);
        Assert.Contains("policy", TelemetryExportPolicy.MetricTagKeys);
        foreach (var forbidden in new[]
                 {
                     TelemetryExportPolicy.OrganizationPseudonymAttribute, TelemetryExportPolicy.ActorRoleKindAttribute,
                     TelemetryOrganizationContext.PseudonymLogProperty, TelemetryOrganizationContext.RoleKindLogProperty,
                     "server.address", "server.port", "client.address", "network.peer.address", "url.full", "url.path",
                     "enduser.id", "db.client.connection.pool.name", "user.is_authenticated",
                 })
        {
            Assert.DoesNotContain(forbidden, TelemetryExportPolicy.MetricTagKeys);
        }
    }

    // --------------------------------------------------------------------------------------------- log lines

    [Fact]
    public void Lines_inside_an_organization_context_carry_the_pseudonym_and_role_kind_and_lines_outside_do_not()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new JsonConsoleLines();
        var logger = lines.Factory.CreateLogger("Obs003.Organization");
        logger.LogInformation("Before {Rows}.", 1);
        using (TraceCorrelation.StartRootActivity(TraceCorrelation.RequestOperation))
        using (TelemetryOrganizationContext.Enter("v1-92d9e53b9ba938cafe99ce9cbd949926", "VIEWER"))
        {
            logger.LogInformation("Inside {Rows}.", 2);
            logger.LogInformation("Explicit {ActorRoleKind}.", "explicit-value");
        }

        logger.LogInformation("After {Rows}.", 3);

        var before = lines.Parse(0).GetProperty("State");
        var inside = lines.Parse(1).GetProperty("State");
        var explicitRole = lines.Parse(2).GetProperty("State");
        var after = lines.Parse(3).GetProperty("State");
        Assert.False(before.TryGetProperty(TelemetryOrganizationContext.PseudonymLogProperty, out _));
        Assert.False(after.TryGetProperty(TelemetryOrganizationContext.PseudonymLogProperty, out _));
        Assert.False(after.TryGetProperty(TelemetryOrganizationContext.RoleKindLogProperty, out _));
        Assert.Equal("v1-92d9e53b9ba938cafe99ce9cbd949926", inside.GetProperty(TelemetryOrganizationContext.PseudonymLogProperty).GetString());
        Assert.Equal("VIEWER", inside.GetProperty(TelemetryOrganizationContext.RoleKindLogProperty).GetString());
        Assert.True(TraceCorrelation.IsTraceId(inside.GetProperty(TraceCorrelation.LogProperty).GetString()));
        Assert.Equal("Inside 2.", lines.Parse(1).GetProperty("Message").GetString());
        Assert.Equal("explicit-value", explicitRole.GetProperty(TelemetryOrganizationContext.RoleKindLogProperty).GetString());
        Assert.Single(explicitRole.EnumerateObject(), property => property.Name == TelemetryOrganizationContext.RoleKindLogProperty);
        Assert.Null(TelemetryOrganizationContext.Current);
        Assert.Throws<ArgumentException>(() => TelemetryOrganizationContext.Enter(Organization.ToString("D"), "VIEWER"));
    }

    // -------------------------------------------------------------------------------------------------- helpers

    private static byte[] SyntheticKey(int offset) => Enumerable.Range(offset, 32).Select(value => (byte)value).ToArray();

    private static string ConnectionString() =>
        "InstrumentationKey=00000000-0000-4000-8000-0000000000a1;IngestionEndpoint=https://mexicocentral-0.in.applicationinsights.azure.com/;" +
        "LiveEndpoint=https://mexicocentral.livediagnostics.monitor.azure.com/;ApplicationId=00000000-0000-0000-0000-0000000000aa";

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHost PseudonymHost(string environment, Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.Services.AddTelemetryOrganizationPseudonym(builder.Configuration, builder.Environment);
        return builder.Build();
    }

    private static IEnumerable<(string Key, object Value)> SensitiveTags() =>
    [
        ("url.path", "/api/v1/tracking/obs003-secret-token"),
        ("url.query", "?email=obs003@example.invalid"),
        ("url.full", "https://paquetenvia.invalid/api/v1/tracking/obs003-secret-token"),
        ("client.address", "203.0.113.7"),
        ("user_agent.original", "obs003-agent/1.0"),
        ("http.request.header.cookie", "obs003-cookie"),
        ("enduser.id", "mock-subject-obs003"),
        ("organization.id", Organization.ToString("D")),
        ("db.query.text", "select set_config('app.current_org_ids', $1, true)"),
        ("server.address", "paquetenvia.invalid"),
    ];

    private static string[] Tags(Activity activity) =>
        activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}").Order(StringComparer.Ordinal).ToArray();

    /// <summary>A source of the given name whose activities are recorded, observed only through this instance.</summary>
    private sealed class RecordedSource : IDisposable
    {
        private readonly ActivitySource _source;
        private readonly ActivityListener _listener;

        internal RecordedSource(string name)
        {
            _source = new ActivitySource(name);
            _listener = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, _source),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal Activity Start(string operation, ActivityKind kind, IEnumerable<ActivityLink>? links = null)
        {
            Activity.Current = null;
            return _source.StartActivity(operation, kind, default(ActivityContext), links: links)
                ?? throw new InvalidOperationException("the recorded source did not start an activity");
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
        }
    }

    /// <summary>The lines the JSON console would write (the framework's JSON formatter, no scopes).</summary>
    private sealed class JsonConsoleLines : IDisposable
    {
        private readonly ServiceProvider _formatters;
        private readonly ConcurrentQueue<string> _lines = new();

        internal JsonConsoleLines()
        {
            _formatters = new ServiceCollection()
                .AddLogging(logging => logging.AddJsonConsole())
                .BuildServiceProvider();
            var formatter = _formatters.GetServices<ConsoleFormatter>()
                .Single(item => item.Name == ConsoleFormatterNames.Json);
            Factory = new TraceCorrelatedLoggerFactory(
                LoggerFactory.Create(logging => logging
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddProvider(new FormattingProvider(formatter, _lines))));
        }

        internal ILoggerFactory Factory { get; }

        internal JsonElement Parse(int index) => JsonDocument.Parse(_lines.ElementAt(index)).RootElement.Clone();

        public void Dispose()
        {
            Factory.Dispose();
            _formatters.Dispose();
        }
    }

    private sealed class FormattingProvider(ConsoleFormatter formatter, ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FormattingLogger(categoryName, formatter, lines);

        public void Dispose()
        {
        }
    }

    private sealed class FormattingLogger(string category, ConsoleFormatter formatter, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> messageFormatter)
        {
            using var writer = new StringWriter();
            formatter.Write(
                new LogEntry<TState>(logLevel, category, eventId, state, exception, messageFormatter),
                scopeProvider: null,
                writer);
            lines.Enqueue(writer.ToString().TrimEnd());
        }
    }
}
