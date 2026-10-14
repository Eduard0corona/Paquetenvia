using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-002: the Worker and outbox paths log and measure only allowlisted, low-cardinality dimensions
/// (fixed lane, job and outcome names, bounded error classes, counts and cutoffs), and the pilot alert
/// queries read only the OBS-002 telemetry dimensions. No payload, token, tenant, identifier or
/// personal datum can become a log property, a metric tag or an alert dimension. OBS-003 PR 6: every
/// metric the hosts export keeps only <see cref="MetricTags"/>, and the organization pseudonym and the
/// actor role kind never reach a metric tag, an OBS-002 event or an alert or workbook query.
/// </summary>
public sealed partial class ObservabilityArchitectureTests
{
    /// <summary>Worker, outbox and scheduler code whose logs reach Log Analytics in the pilot.</summary>
    private static readonly string[] WorkerAndOutboxPaths =
    [
        "src/BuildingBlocks/Paqueteria.Infrastructure/Observability",
        "src/BuildingBlocks/Paqueteria.Infrastructure/Scheduling",
        "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox",
        "src/Modules/Notifications/Notifications.Infrastructure/Dispatching",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle",
        "src/Modules/Realtime/Realtime.Infrastructure/Dispatching",
        "src/Modules/Custody/Custody.Infrastructure/Cleanup",
        "src/Modules/Orders/Orders.Infrastructure/Lifecycle",
        "src/Paqueteria.Worker",
    ];

    /// <summary>
    /// Log template properties allowed on those paths: the OBS-002 dimensions plus the existing bounded
    /// OPS-003/OPS-004/NTF-001 fields (fixed names, error classes, counts, configured values and cutoffs).
    /// </summary>
    private static readonly HashSet<string> WorkerLogProperties = new(TelemetryDimensions.Allowed, StringComparer.Ordinal)
    {
        "Owner",
        "Channel",
        "Code",
        "Provider",
        "ErrorClass",
        "DryRun",
        "PollInterval",
        "Batches",
        "Rows",
        "Finalized",
        "BatchSize",
        "MaxBatchesPerRun",
        "AffectedRows",
        "DeadEligible",
        "DeadEligibleBound",
        "Exhausted",
        "ProcessedBefore",
        "DeadBefore",
        "StartedAt",
        "CompletedAt",
        // ORD-AUTO-CLOSE-2026-10-10: per-cycle attempt counts of the order auto-close job.
        "Closed",
        "NotEligible",
        "Superseded",
        "Failed",
        "Attempted",
    };

    /// <summary>
    /// The tag keys of the application meters, as their source declares them (every file that creates an instrument,
    /// <see cref="MetricSourceFiles"/>): fixed lane, job, outcome, provider, hub and rate-limit policy names, error
    /// classes and codes.
    /// </summary>
    private static readonly HashSet<string> ApplicationMetricTags = new(StringComparer.Ordinal)
    {
        "lane",
        "mode",
        "outcome",
        "error_class",
        "job",
        "audience",
        "event_type",
        "event.type",
        // SEC-004: rate_limit.rejected counts by registry policy name only (RateLimitTelemetry.PolicyTag).
        "policy",
        "provider",
        "proof_type",
        "reason_code",
        "auth.kind",
        "hub",
        "category",
        "stage",
        "publication_decision",
        "rejection_code",
    };

    /// <summary>
    /// OBS-003 PR 6: the tag keys of the built-in ASP.NET Core meters that the export keeps, among them the policy
    /// name and the result of <c>aspnetcore.rate_limiting.requests</c>. Kestrel's <c>server.address</c> and
    /// <c>server.port</c>, <c>network.protocol.name</c> and every other key are dropped by the export's view.
    /// </summary>
    private static readonly HashSet<string> AspNetCoreMetricTags = new(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "url.scheme",
        "network.protocol.version",
        "error.type",
        "aspnetcore.request.is_unhandled",
        "aspnetcore.routing.match_status",
        "aspnetcore.routing.is_fallback",
        "aspnetcore.diagnostics.handler.type",
        "aspnetcore.diagnostics.exception.result",
        "aspnetcore.rate_limiting.policy",
        "aspnetcore.rate_limiting.result",
        "signalr.connection.status",
        "signalr.transport",
        "aspnetcore.authentication.scheme",
        "aspnetcore.authentication.result",
        "aspnetcore.authorization.policy",
        "aspnetcore.authorization.result",
        "aspnetcore.memory_pool.owner",
        "tls.protocol.version",
    };

    /// <summary>The OBS-002 metric allowlist: the only tag keys an exported metric keeps.</summary>
    private static readonly HashSet<string> MetricTags = new(ApplicationMetricTags.Concat(AspNetCoreMetricTags), StringComparer.Ordinal);

    /// <summary>
    /// Instrument files whose meters record no tag at all (each measurement is a bare count or duration).
    /// </summary>
    private static readonly HashSet<string> UntaggedMetricFiles = new(StringComparer.Ordinal)
    {
        "src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignableDriversQuery.cs",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Fleet/PostgreSqlOrganizationDriversQuery.cs",
        "src/Modules/Dispatch/Dispatch.Infrastructure/Stops/PostgreSqlDriverStopsQuery.cs",
    };

    private static readonly string[] ForbiddenFragments =
    [
        "payload", "token", "secret", "password", "email", "phone", "address", "recipient", "tenant",
        "organization", "orgid", "userid", "driverid", "orderid", "publicid", "aggregateid", "messageid",
        "name", "body", "variables", "coordinate", "latitude", "longitude",
        // OBS-003-TENANT-PSEUDONYM-2026-10-12: the organization pseudonym, the actor role kind, a subject, a client
        // address and a rate-limit partition key never become a dimension or a metric tag.
        "pseudonym", "role", "subject", "client", "partition",
    ];

    /// <summary>Whole-word fragments (split on '.' and '_'): an IP address never becomes a dimension or a tag.</summary>
    private static readonly string[] ForbiddenWords = ["ip", "peer", "user", "sub"];

    /// <summary>The names under which the pseudonym and the role kind are written (logs and spans).</summary>
    private static readonly string[] PseudonymAndRoleNames =
    [
        TelemetryOrganizationContext.PseudonymLogProperty,
        TelemetryOrganizationContext.RoleKindLogProperty,
        TelemetryExportPolicy.OrganizationPseudonymAttribute,
        TelemetryExportPolicy.ActorRoleKindAttribute,
    ];

    [Fact]
    public void Obs002_dimension_allowlist_carries_no_identifier_or_personal_field()
    {
        Assert.All(TelemetryDimensions.Allowed, AssertNotSensitive);
        Assert.All(WorkerLogProperties, AssertNotSensitive);
        Assert.All(MetricTags, AssertNotSensitive);
        Assert.Equal(
            ["dispatch", "notifications", "realtime_business", "realtime_location"],
            OutboxLanes.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Worker_and_outbox_logs_use_only_allowlisted_properties()
    {
        var calls = LogCalls().ToArray();

        Assert.NotEmpty(calls);
        foreach (var (path, call) in calls)
        {
            Assert.False(call.Contains("$\"", StringComparison.Ordinal), $"{path}: interpolated log message: {call}");
            foreach (var property in Placeholders(call))
            {
                Assert.True(
                    WorkerLogProperties.Contains(property),
                    $"{path}: log property {{{property}}} is not in the OBS-002 Worker/outbox allowlist");
            }
        }
    }

    [Fact]
    public void Obs002_events_carry_exactly_the_telemetry_dimensions()
    {
        var properties = LogCalls()
            .Where(call => call.Path.Contains("/Observability/", StringComparison.Ordinal) ||
                           call.Path.EndsWith("Scheduling/PeriodicJobScheduler.cs", StringComparison.Ordinal))
            .SelectMany(call => Placeholders(call.Call))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(TelemetryDimensions.Allowed.ToHashSet(StringComparer.Ordinal), properties);
        Assert.Equal(TelemetryDimensions.Allowed.Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));
        Assert.Equal(4601, TelemetryEvents.OutboxLaneSummary.Id);
        Assert.Equal(4602, TelemetryEvents.ScheduledJobCycle.Id);
        Assert.Equal(4603, TelemetryEvents.HttpStatusSummary.Id);
        Assert.Equal(4604, TelemetryEvents.RateLimitSummary.Id);
        Assert.Equal("RateLimitSummary", TelemetryEvents.RateLimitSummary.Name);

        // Every OBS-002 event takes its id from the reserved block, so the log enrichment never adds the organization
        // pseudonym or the role kind to one (TelemetryEvents.IsObservabilityEvent), also for an event added later.
        var events = typeof(TelemetryEvents).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Microsoft.Extensions.Logging.EventId))
            .Select(field => (Microsoft.Extensions.Logging.EventId)field.GetValue(null)!)
            .ToArray();
        Assert.Equal(4, events.Length);
        Assert.All(events, eventId => Assert.InRange(eventId.Id, TelemetryEvents.FirstReservedId, TelemetryEvents.LastReservedId));
        Assert.All(events, eventId => Assert.True(TelemetryEvents.IsObservabilityEvent(eventId)));
    }

    /// <summary>
    /// SEC-004 (owner answer Q8): <c>Policy</c> and <c>Rejected</c> are dimensions of the rate-limit summary only (4604),
    /// and no pilot alert or workbook reads them or that event until a later owner decision asks for it.
    /// </summary>
    [Fact]
    public void The_rate_limit_summary_dimensions_reach_no_alert_or_workbook()
    {
        Assert.Contains("Policy", TelemetryDimensions.Allowed);
        Assert.Contains("Rejected", TelemetryDimensions.Allowed);
        Assert.DoesNotContain("PolicyName", TelemetryDimensions.Allowed);
        // The forbidden-fragment rule is why the dimension is Policy: PolicyName contains "name".
        Assert.ThrowsAny<Exception>(() => AssertNotSensitive("PolicyName"));

        var template = File.ReadAllText(TestRepository.GetPath("deploy/azure/pilot/observability.bicep"));
        var properties = StatePropertyPattern().Matches(template).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Policy", properties);
        Assert.DoesNotContain("Rejected", properties);
        Assert.DoesNotContain("4604", template, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_and_outbox_metric_tags_are_allowlisted()
    {
        // OBS-003 PR 6: every exported meter is scanned, not only the Worker and outbox paths: every src file that
        // creates an instrument, the rate-limiting registry included. KeyValuePair constructions and KeyValuePair.Create,
        // target-typed pairs, tuple helpers, TagList initializers and TagList.Add are all read, with literal or constant
        // keys of any case; any other key comes back unresolved and fails (MetricTagKeys).
        var files = MetricSourceFiles().ToArray();
        Assert.Contains(files, file => file.Path == "src/BuildingBlocks/Paqueteria.Infrastructure/Security/RateLimiting/RateLimitRejectionWriter.cs");
        Assert.Contains(files, file => file.Path.StartsWith("src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox", StringComparison.Ordinal));

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var tags = MetricTagKeys(file.Path, file.Text).ToArray();
            Assert.True(
                tags.Length > 0 || UntaggedMetricFiles.Contains(file.Path),
                $"{file.Path}: creates instruments but no tag key was read; list it as untagged only if it records none");
            Assert.False(
                tags.Length > 0 && UntaggedMetricFiles.Contains(file.Path),
                $"{file.Path}: records tags but is listed as untagged");
            foreach (var tag in tags)
            {
                Assert.True(ApplicationMetricTags.Contains(tag), $"{file.Path}: metric tag {tag} is not allowlisted");
                found.Add(tag);
            }
        }

        // Every allowlisted application tag is still in use, so the list cannot hide a stale or renamed key.
        Assert.Equal(ApplicationMetricTags.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(@"c.Add(1, new KeyValuePair<string, object?>(""organization_id"", id));", "organization_id")]
    [InlineData(@"c.Add(1, new KeyValuePair<string, object>(""OrganizationId"", id));", "OrganizationId")]
    [InlineData(@"private const string Key = ""subject_hash""; void M() => c.Add(1, new KeyValuePair<string, object?>(Key, v));", "subject_hash")]
    [InlineData(@"static KeyValuePair<string, object?> Ip(string v) => new(""client.ip"", v);", "client.ip")]
    [InlineData(@"static KeyValuePair<string, object?> Org(Guid id) => new(""OrganizationId"", id);", "OrganizationId")]
    [InlineData(@"const string Key = ""Subject""; static KeyValuePair<string, object?> S(string v) => new(Key, v);", "Subject")]
    [InlineData(@"static KeyValuePair<string, object?> S(string v) => new(SomeType.NotAConstant, v);", "<unresolved:SomeType.NotAConstant>")]
    [InlineData(@"static KeyValuePair<string, object?> S(string v) => new($""user_{v}"", v);", @"<unresolved:$""user_{v}"">")]
    [InlineData(@"KeyValuePair<string, object?>[] tags = [new(""lane"", l), new(""tenant_id"", t)];", "tenant_id")]
    [InlineData(@"c.Add(1, new(""user.id"", u));", "user.id")]
    [InlineData(@"h.Record(ms, new(""lane"", l), new(""peer.ip"", p));", "peer.ip")]
    [InlineData(@"var tags = new TagList([new(""partition"", p)]);", "partition")]
    [InlineData(@"c.Add(1, KeyValuePair.Create(""tenant"", (object?)t));", "tenant")]
    [InlineData(@"const string K = ""address""; void M() => c.Add(1, KeyValuePair.Create<string, object?>(K, a));", "address")]
    [InlineData(@"c.Add(1, Tags((""partition"", p), (""lane"", l)));", "partition")]
    [InlineData(@"const string K = ""role""; void M() => c.Add(1, Tags((K, r)));", "role")]
    [InlineData(@"void M() => c.Add(1, Tags((name, r)));", "<unresolved:name>")]
    [InlineData(@"var tags = new TagList { { ""user.id"", u }, { ""lane"", l } };", "user.id")]
    [InlineData(@"const string K = ""email""; void M() { var tags = new TagList { { K, e } }; }", "email")]
    [InlineData(@"var tags = new TagList(); tags.Add(""OrganizationId"", id);", "OrganizationId")]
    [InlineData(@"const string K = ""client_ip""; void M(ref TagList tags) => tags.Add(K, ip);", "client_ip")]
    [InlineData(@"void M() { TagList tags = default; tags.Add(dynamicName, v); }", "<unresolved:dynamicName>")]
    [InlineData(@"c.Add(1, new KeyValuePair<string, object?>(SomeType.Unknown, v));", "<unresolved:SomeType.Unknown>")]
    [InlineData(@"sealed class Names { public const string Lane = ""lane_name""; } void M() => c.Add(1, new KeyValuePair<string, object?>(Names.Lane, l));", "lane_name")]
    public void The_metric_tag_scan_reads_every_way_a_key_can_be_written(string code, string expected)
    {
        Assert.Contains(expected, MetricTagKeys("fixture.cs", code));
    }

    /// <summary>
    /// Target-typed <c>new(...)</c> that builds something other than a tag (a meter, a database parameter, a command, a
    /// record), an instrument's own <c>Add(value, tags)</c> and a tuple helper that forwards its scanned call sites'
    /// keys are not tags, so the strict scan does not report them.
    /// </summary>
    [Theory]
    [InlineData(@"private static readonly Meter Meter = new(""Paqueteria.Drivers.Voice"", ""1.0.0"");")]
    [InlineData(@"private static NpgsqlParameter P(string name, NpgsqlDbType type) => new(name, type);")]
    [InlineData(@"private static NpgsqlCommand C(NpgsqlConnection connection, string sql) => new(sql, connection);")]
    [InlineData(@"public static RouteKey Unrouted { get; } = new(null, null, null, null);")]
    [InlineData(@"Result M() { return new(false, false, null); }")]
    [InlineData(@"void M(long value) { counter.Add(value); histogram.Record(value); parameters.Add(new(name, value)); }")]
    [InlineData(@"private static TagList Tags(params (string Name, object? Value)[] values) { var tags = new TagList(); foreach (var (name, value) in values) { tags.Add(name, value); } return tags; }")]
    public void The_metric_tag_scan_ignores_what_is_not_a_tag(string code)
    {
        Assert.Empty(MetricTagKeys("fixture.cs", code));
    }

    /// <summary>
    /// OBS-003 PR 6: the export keeps exactly the OBS-002 metric allowlist (the view on every exported instrument), and
    /// no key of it names an identifier, an address, a partition key, the pseudonym or the role kind.
    /// </summary>
    [Fact]
    public void The_export_keeps_exactly_the_metric_allowlist()
    {
        Assert.Equal(MetricTags.Order(StringComparer.Ordinal), TelemetryExportPolicy.MetricTagKeys.Order(StringComparer.Ordinal));
        Assert.Contains("aspnetcore.rate_limiting.policy", MetricTags);
        Assert.Contains("aspnetcore.rate_limiting.result", MetricTags);
        Assert.All(MetricTags, AssertNotSensitive);
        Assert.Equal(["Microsoft.AspNetCore.*", "Paquetenvia.*", "Paqueteria.*"], TelemetryExportPolicy.ExportedMeters.Order(StringComparer.Ordinal));
        foreach (var path in new[] { "src/Paqueteria.Api/Program.cs", "src/Paqueteria.Worker/Program.cs" })
        {
            Assert.DoesNotContain("AddMeter(", File.ReadAllText(TestRepository.GetPath(path)), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// OBS-003-TENANT-PSEUDONYM-2026-10-12: the organization pseudonym and the actor role kind never reach a metric tag,
    /// an OBS-002 event (its properties are exactly <see cref="TelemetryDimensions.Allowed"/>), a log template, or an
    /// alert or workbook query, and only the reviewed files compute or carry them.
    /// </summary>
    [Fact]
    public void The_pseudonym_and_role_kind_never_reach_metrics_obs002_events_or_alert_queries()
    {
        foreach (var name in PseudonymAndRoleNames)
        {
            Assert.DoesNotContain(name, TelemetryDimensions.Allowed);
            Assert.DoesNotContain(name, MetricTags);
            Assert.DoesNotContain(name, TelemetryExportPolicy.MetricTagKeys);
            Assert.DoesNotContain(name, WorkerLogProperties);
            // The forbidden-fragment rule refuses them, so neither can ever be added to an allowlist.
            Assert.ThrowsAny<Exception>(() => AssertNotSensitive(name));
        }

        // No metric file and no log template names them.
        foreach (var file in MetricSourceFiles())
        {
            Assert.DoesNotContain("Pseudonym", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("RoleKind", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("TelemetryOrganizationContext", file.Text, StringComparison.Ordinal);
        }

        foreach (var file in AllSourceFiles())
        {
            Assert.DoesNotContain("{" + TelemetryOrganizationContext.PseudonymLogProperty + "}", file.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("{" + TelemetryOrganizationContext.RoleKindLogProperty + "}", file.Text, StringComparison.Ordinal);
        }

        // The alert and workbook queries read neither, nor any Application Insights table.
        var template = File.ReadAllText(TestRepository.GetPath("deploy/azure/pilot/observability.bicep"));
        foreach (var forbidden in PseudonymAndRoleNames.Concat(["pseudonym", "Pseudonym", "role_kind", "RoleKind"]).Concat(ApplicationInsightsTables))
        {
            Assert.DoesNotContain(forbidden, template, StringComparison.Ordinal);
        }

        // Only these files compute the pseudonym, carry it, or register its key.
        var users = AllSourceFiles()
            .Where(file => PseudonymUsePattern().IsMatch(file.Text))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/OrganizationPseudonym.cs",
                "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TelemetryExportPolicy.cs",
                "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TraceCorrelatedLogging.cs",
                .. WorkerOrganizationContextSites.Keys,
                "src/Paqueteria.Api/Program.cs",
                "src/Paqueteria.Api/Tenancy/TelemetryOrganizationContextMiddleware.cs",
                "src/Paqueteria.Worker/Program.cs",
            ],
            users);
    }

    /// <summary>
    /// OBS-003-WORKER-PSEUDONYM-2026-10-14: the only Worker sites that enter an organization context, each with the
    /// exact call it makes. The two outbox lanes enter it per claimed message, right after the message's activity, from
    /// the owner organization the claim function returned; proof validation enters it per photo, after the signed
    /// metadata was read and found valid, from the owner organization the metadata names (QC).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WorkerOrganizationContextSites =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Modules/Custody/Custody.Infrastructure/Proofs/ProofValidationProcessor.cs"] =
                "TelemetryOrganizationContext.EnterSystem(pseudonymizer, metadata.OwnerOrganizationId)",
            ["src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs"] =
                "TelemetryOrganizationContext.EnterSystem(pseudonymizer, message.OwnerOrganizationId)",
            ["src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxDispatcher.cs"] =
                "TelemetryOrganizationContext.EnterSystem(pseudonymizer, message.OwnerOrganizationId)",
        };

    /// <summary>
    /// OBS-003-WORKER-PSEUDONYM-2026-10-14 (Q2, QB "SYSTEM (Recommended)", QC): the Worker carries the pseudonym and the
    /// fixed role kind SYSTEM only on the lines of one message or one photo, entered once per unit of work at the
    /// reviewed sites; it never enters a context with any other role kind, and the API realtime lanes stay unchanged.
    /// </summary>
    [Fact]
    public void The_worker_enters_the_organization_context_only_per_message_or_photo_with_role_kind_system()
    {
        Assert.Equal("SYSTEM", TelemetryOrganizationContext.SystemRoleKind);
        var enterers = AllSourceFiles()
            .Where(file => file.Text.Contains("TelemetryOrganizationContext.Enter", StringComparison.Ordinal))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            WorkerOrganizationContextSites.Keys.Append("src/Paqueteria.Api/Tenancy/TelemetryOrganizationContextMiddleware.cs")
                .Order(StringComparer.Ordinal),
            enterers);

        foreach (var (path, call) in WorkerOrganizationContextSites)
        {
            var code = CodeWithoutComments(File.ReadAllText(TestRepository.GetPath(path)));
            Assert.Equal(1, Regex.Count(code, Regex.Escape("TelemetryOrganizationContext.Enter")));
            Assert.Equal(1, Regex.Count(code, Regex.Escape(call)));
            Assert.DoesNotContain("TelemetryOrganizationContext.Enter(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SetOrganization", code, StringComparison.Ordinal);
        }

        // The lanes: right after the message's activity, inside the per-message body, disposed with it.
        foreach (var path in WorkerOrganizationContextSites.Keys.Where(path => !path.Contains("/Custody/", StringComparison.Ordinal)))
        {
            var code = CodeWithoutComments(File.ReadAllText(TestRepository.GetPath(path)));
            Assert.Matches(
                @"using var activity = TraceCorrelation\.StartOutboxMessage\([^;]+\);\s*using var organization = " +
                Regex.Escape(WorkerOrganizationContextSites[path]) + ";",
                code);
        }

        // Proof validation: only after the signed metadata was read and found valid.
        var proofs = CodeWithoutComments(File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Custody/Custody.Infrastructure/Proofs/ProofValidationProcessor.cs")));
        Assert.True(
            proofs.IndexOf("TryReadExpectedMetadata(descriptor, out var metadata)", StringComparison.Ordinal) is var read and >= 0 &&
            read < proofs.IndexOf("TelemetryOrganizationContext.EnterSystem(", StringComparison.Ordinal),
            "proof validation must enter the organization context only after reading valid signed metadata");

        // The one Worker entry point always uses the fixed role kind, and the API realtime lanes enter nothing.
        var pseudonym = CodeWithoutComments(File.ReadAllText(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/OrganizationPseudonym.cs")));
        Assert.Contains("Enter(pseudonymizer.Pseudonymize(ownerOrganizationId), SystemRoleKind)", pseudonym, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TelemetryOrganizationContext",
            File.ReadAllText(TestRepository.GetPath("src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxProcessor.cs")),
            StringComparison.Ordinal);
    }

    private static string CodeWithoutComments(string text) => string.Join(
        '\n',
        text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void Observability_code_never_reads_the_outbox_or_the_database()
    {
        foreach (var file in SourceFiles().Where(file => file.Path.Contains("/Observability/", StringComparison.Ordinal)))
        {
            foreach (var forbidden in new[] { "Npgsql", "outbox_events", "SELECT", "DbContext", "HttpContext.User", "Request.Path.Value", "Request.Query" })
            {
                Assert.DoesNotContain(forbidden, file.Text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Pilot_alerts_query_only_obs002_events_and_dimensions()
    {
        var template = File.ReadAllText(TestRepository.GetPath("deploy/azure/pilot/observability.bicep"));
        var retention = File.ReadAllText(TestRepository.GetPath(
            "src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox/Retention/OutboxRetentionService.cs"));

        var properties = StatePropertyPattern().Matches(template).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(properties);
        Assert.Subset(TelemetryDimensions.Allowed.ToHashSet(StringComparer.Ordinal), properties);

        var eventIds = EventIdPattern().Matches(template).Select(match => int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        Assert.Equal(
            new[] { 4004, TelemetryEvents.OutboxLaneSummary.Id, TelemetryEvents.ScheduledJobCycle.Id, TelemetryEvents.HttpStatusSummary.Id }.Order(),
            eventIds.Order());
        Assert.Contains("new(4004, \"OutboxRetentionLaneCompleted\")", retention, StringComparison.Ordinal);
        // Every event an alert or the workbook reads is one the log enrichment keeps free of the pseudonym and role kind.
        Assert.All(eventIds, id => Assert.True(TelemetryEvents.IsObservabilityEvent(new Microsoft.Extensions.Logging.EventId(id)), $"event {id}"));
        Assert.Equal(4004, TelemetryEvents.RetentionLaneResultId);

        foreach (var lane in OutboxLanes.All)
        {
            Assert.Contains($"\"{lane}\"", template, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("param alertEmailAddress string =", template, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosts_wire_the_obs002_events()
    {
        var api = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        var telemetry = api.IndexOf("app.UseHttpStatusTelemetry();", StringComparison.Ordinal);
        var exceptionHandler = api.IndexOf("app.UseExceptionHandler();", StringComparison.Ordinal);
        Assert.True(telemetry > 0 && telemetry < exceptionHandler, "the status counter must wrap the exception handler");
        Assert.Contains("builder.Services.AddHttpStatusTelemetry();", api, StringComparison.Ordinal);

        foreach (var (path, lane) in new[]
                 {
                     ("src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxDispatcher.cs", "OutboxLanes.Notifications"),
                     ("src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs", "OutboxLanes.Dispatch"),
                 })
        {
            var source = File.ReadAllText(TestRepository.GetPath(path));
            Assert.Contains($"lanes.ReportIfDue({lane});", source, StringComparison.Ordinal);
            Assert.Contains($"lanes.LoopFailed({lane});", source, StringComparison.Ordinal);
        }

        var realtime = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxDispatchers.cs"));
        Assert.Equal(2, Regex.Count(realtime, @"lanes\.ReportIfDue\(RealtimeOutboxLanes\.Monitored\(""(business|location)""\)\);"));
    }

    private static void AssertNotSensitive(string dimension)
    {
        var normalized = dimension.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        Assert.All(ForbiddenFragments, fragment => Assert.False(
            normalized.Contains(fragment, StringComparison.Ordinal),
            $"dimension {dimension} looks like an identifier, payload or personal field ({fragment})"));
        var words = dimension.ToLowerInvariant().Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries);
        Assert.All(ForbiddenWords, word => Assert.False(
            words.Contains(word, StringComparer.Ordinal),
            $"dimension {dimension} looks like an address, subject or user field ({word})"));
    }

    /// <summary>The Application Insights tables (workspace and classic names) no OBS-002 query may read.</summary>
    private static readonly string[] ApplicationInsightsTables =
    [
        "AppRequests", "AppDependencies", "AppTraces", "AppMetrics", "AppExceptions", "AppEvents", "AppPageViews",
        "customMetrics", "customDimensions", "requests |", "dependencies |",
    ];

    /// <summary>
    /// The tag keys one metric file writes, through every way a key can be written:
    /// <list type="bullet">
    /// <item><c>new KeyValuePair&lt;string, object?&gt;(key, …)</c> and <c>KeyValuePair.Create(key, …)</c>;</item>
    /// <item>a target-typed <c>new(key, …)</c> in a tag context: a statement typed <c>KeyValuePair&lt;string, …&gt;</c> or
    /// <c>TagList</c>, or a measurement argument of an instrument's <c>Add</c> or <c>Record</c>;</item>
    /// <item>the tuples of a <c>Tags((key, value), …)</c> helper call and the entries of a <c>TagList</c> initializer;</item>
    /// <item><c>.Add("key", …)</c> on any receiver, and <c>.Add(key, …)</c> on a <c>TagList</c> variable.</item>
    /// </list>
    /// A key is a string literal, a constant of the file or a <c>Type.Constant</c> of the repository; anything else
    /// (a variable, an interpolated string, an expression) comes back as <c>&lt;unresolved:…&gt;</c>, which no allowlist
    /// contains, so the scan fails on it. The only names it skips are the deconstructed tuple elements a
    /// <c>Tags(params (string, object?)[])</c> helper forwards, whose call sites it reads.
    /// </summary>
    private static IEnumerable<string> MetricTagKeys(string path, string text)
    {
        _ = path;
        string Resolve(string key) => ResolveTagKey(key, text);

        foreach (Match match in KeyValuePairTagPattern().Matches(text))
        {
            yield return Resolve(FirstArgument(text, match.Index + match.Length - 1).Argument);
        }

        foreach (Match match in KeyValuePairCreatePattern().Matches(text))
        {
            yield return Resolve(FirstArgument(text, match.Index + match.Length - 1).Argument);
        }

        foreach (Match match in TargetTypedNewPattern().Matches(text))
        {
            var (argument, more) = FirstArgument(text, match.Index + match.Length - 1);
            if (more && IsTagContext(text, match.Index))
            {
                yield return Resolve(argument);
            }
        }

        var helperDeclarations = TupleHelperDeclarationPattern().Matches(text).Select(match => match.Index).ToHashSet();
        foreach (Match call in TagsHelperCallPattern().Matches(text))
        {
            if (helperDeclarations.Contains(call.Index))
            {
                continue;
            }

            var content = Enclosed(text, call.Index + call.Length - 1);
            foreach (var tuple in TopLevel(content, '('))
            {
                var (argument, more) = FirstArgument(content, tuple);
                if (more)
                {
                    yield return Resolve(argument);
                }
            }
        }

        foreach (Match initializer in TagListInitializerPattern().Matches(text))
        {
            var content = Enclosed(text, initializer.Index + initializer.Length - 1);
            foreach (var entry in TopLevel(content, '{'))
            {
                var (argument, more) = FirstArgument(content, entry);
                if (more)
                {
                    yield return Resolve(argument);
                }
            }
        }

        var tagLists = TagListVariablePattern().Matches(text).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        var forwarded = ForwardedTupleNames(text);
        foreach (Match add in AddCallPattern().Matches(text))
        {
            var (argument, more) = FirstArgument(text, add.Index + add.Length - 1);
            if (!more)
            {
                continue;
            }

            if (argument.StartsWith('"') || argument.StartsWith("@\"", StringComparison.Ordinal) || argument.StartsWith('$'))
            {
                yield return Resolve(argument);
            }
            else if (tagLists.Contains(add.Groups["receiver"].Value) && !forwarded.Contains(argument))
            {
                yield return Resolve(argument);
            }
        }
    }

    /// <summary>
    /// A target-typed <c>new(</c> builds a tag when its statement (since the last <c>;</c> or <c>}</c>) is typed
    /// <c>KeyValuePair&lt;string, …&gt;</c> or <c>TagList</c>, or when it is a measurement argument after the value of an
    /// instrument's <c>Add</c> or <c>Record</c>.
    /// </summary>
    private static bool IsTagContext(string text, int index)
    {
        var start = Math.Max(text.LastIndexOf(';', index - 1), text.LastIndexOf('}', index - 1)) + 1;
        var prefix = text[start..index];
        return prefix.Contains("KeyValuePair<string,", StringComparison.Ordinal) ||
               prefix.Contains("TagList", StringComparison.Ordinal) ||
               MeasurementArgumentPattern().IsMatch(prefix);
    }

    /// <summary>The names a <c>Tags(params (string Name, object? Value)[] values)</c> helper deconstructs and forwards.</summary>
    private static HashSet<string> ForwardedTupleNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match helper in TupleHelperDeclarationPattern().Matches(text))
        {
            var parameter = helper.Groups["parameter"].Value;
            foreach (Match loop in TupleDeconstructionPattern().Matches(text))
            {
                if (loop.Groups["source"].Value == parameter)
                {
                    names.Add(loop.Groups["name"].Value);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// A string literal, a constant of the file, or a <c>Type.Constant</c> declared in the file or in exactly one
    /// repository file that declares the type, with exactly one value; otherwise <c>&lt;unresolved:…&gt;</c>.
    /// </summary>
    private static string ResolveTagKey(string key, string text)
    {
        if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
        {
            return key[1..^1];
        }

        if (key.Length >= 3 && key.StartsWith("@\"", StringComparison.Ordinal) && key[^1] == '"')
        {
            return key[2..^1];
        }

        if (!IdentifierPattern().IsMatch(key))
        {
            return $"<unresolved:{key}>";
        }

        var separator = key.LastIndexOf('.');
        if (separator < 0)
        {
            return Constants(text).TryGetValue(key, out var values) && values.Length == 1 ? values[0] : $"<unresolved:{key}>";
        }

        var type = key[..separator];
        var name = key[(separator + 1)..];
        type = type[(type.LastIndexOf('.') + 1)..];
        var declaring = DeclaresType(text, type)
            ? [text]
            : AllSourceFiles().Select(file => file.Text).Where(source => DeclaresType(source, type)).ToArray();
        var resolved = declaring
            .SelectMany(source => Constants(source).TryGetValue(name, out var values) ? values : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return declaring.Length == 1 && resolved.Length == 1 ? resolved[0] : $"<unresolved:{key}>";
    }

    private static bool DeclaresType(string text, string type) =>
        Regex.IsMatch(text, $@"\b(?:class|struct|record|interface)\s+{Regex.Escape(type)}\b");

    /// <summary>A constant declared twice in a file (in two types) with different values is ambiguous: unresolved.</summary>
    private static Dictionary<string, string[]> Constants(string text) =>
        ConstantPattern().Matches(text)
            .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(match => match.Groups["value"].Value).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

    /// <summary>
    /// The first argument of the call whose <c>(</c> is at <paramref name="open"/>, trimmed, and whether another
    /// argument follows. Strings (regular, verbatim, interpolated), characters and nested brackets are skipped whole.
    /// </summary>
    private static (string Argument, bool More) FirstArgument(string text, int open)
    {
        var depth = 0;
        for (var index = open + 1; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '"' || (current is '@' or '$' && index + 1 < text.Length && text[index + 1] is '"' or '@' or '$'))
            {
                index = EndOfString(text, index);
            }
            else if (current == '\'' && index + 2 < text.Length)
            {
                index = text.IndexOf('\'', index + (text[index + 1] == '\\' ? 3 : 2));
                if (index < 0)
                {
                    break;
                }
            }
            else if (current is '(' or '[' or '{')
            {
                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                if (depth-- == 0)
                {
                    return (text[(open + 1)..index].Trim(), false);
                }
            }
            else if (current == ',' && depth == 0)
            {
                return (text[(open + 1)..index].Trim(), true);
            }
        }

        return (text[(open + 1)..].Trim(), false);
    }

    /// <summary>The indexes of the <paramref name="open"/> brackets at the top level of <paramref name="content"/>.</summary>
    private static IEnumerable<int> TopLevel(string content, char open)
    {
        var depth = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];
            if (current == '"' || (current is '@' or '$' && index + 1 < content.Length && content[index + 1] is '"' or '@' or '$'))
            {
                index = EndOfString(content, index);
            }
            else if (current is '(' or '[' or '{')
            {
                if (depth == 0 && current == open)
                {
                    yield return index;
                }

                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                depth--;
            }
        }
    }

    /// <summary>The index of the closing quote of the string literal that starts at <paramref name="start"/>.</summary>
    private static int EndOfString(string text, int start)
    {
        var index = start;
        var verbatim = false;
        while (index < text.Length && text[index] != '"')
        {
            verbatim |= text[index] == '@';
            index++;
        }

        for (index++; index < text.Length; index++)
        {
            if (text[index] == '\\' && !verbatim)
            {
                index++;
            }
            else if (text[index] == '"')
            {
                if (verbatim && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                return index;
            }
        }

        return text.Length - 1;
    }

    /// <summary>The text between the bracket at <paramref name="open"/> and its match.</summary>
    private static string Enclosed(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] is '(' or '{')
            {
                depth++;
            }
            else if (text[index] is ')' or '}' && --depth == 0)
            {
                return text[(open + 1)..index];
            }
        }

        return text[(open + 1)..];
    }

    /// <summary>Every src file that creates an instrument: the meters the hosts export.</summary>
    private static IEnumerable<(string Path, string Text)> MetricSourceFiles() =>
        AllSourceFiles().Where(file => InstrumentCreationPattern().IsMatch(file.Text));

    private static IEnumerable<(string Path, string Text)> AllSourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/'), File.ReadAllText(path)));

    private static IEnumerable<string> Placeholders(string call) =>
        StringLiteralPattern().Matches(call)
            .SelectMany(literal => PlaceholderPattern().Matches(literal.Value))
            .Select(match => match.Groups["name"].Value);

    private static IEnumerable<(string Path, string Call)> LogCalls() =>
        SourceFiles().SelectMany(file => file.Text.Split("logger.Log", StringSplitOptions.None).Skip(1)
            .Select(call => (file.Path, call[..call.IndexOf(");", StringComparison.Ordinal)])));

    private static IEnumerable<(string Path, string Text)> SourceFiles() =>
        WorkerAndOutboxPaths
            .SelectMany(root => Directory.EnumerateFiles(TestRepository.GetPath(root), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(TestRepository.Root, path).Replace('\\', '/'), File.ReadAllText(path)));

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\.Create(?:Counter|Histogram|UpDownCounter|Gauge|ObservableCounter|ObservableGauge|ObservableUpDownCounter)\s*<")]
    private static partial Regex InstrumentCreationPattern();

    [GeneratedRegex(@"KeyValuePair<string,\s*object\??>\(")]
    private static partial Regex KeyValuePairTagPattern();

    [GeneratedRegex(@"\bKeyValuePair\.Create(?:<[^>()]*>)?\(")]
    private static partial Regex KeyValuePairCreatePattern();

    [GeneratedRegex(@"(?<![\w.])new\(")]
    private static partial Regex TargetTypedNewPattern();

    [GeneratedRegex(@"\.(?:Add|Record)\([^;]*,\s*$")]
    private static partial Regex MeasurementArgumentPattern();

    [GeneratedRegex(@"\bTags\s*\(")]
    private static partial Regex TagsHelperCallPattern();

    [GeneratedRegex(@"\bTags\s*\(\s*params\s*\(\s*string\s+\w+\s*,\s*object\??\s+\w+\s*\)\[\]\s*(?<parameter>\w+)\s*\)")]
    private static partial Regex TupleHelperDeclarationPattern();

    [GeneratedRegex(@"\bforeach\s*\(\s*var\s*\(\s*(?<name>\w+)\s*,\s*\w+\s*\)\s*in\s+(?<source>\w+)\s*\)")]
    private static partial Regex TupleDeconstructionPattern();

    [GeneratedRegex(@"\bnew\s+TagList\s*(?:\(\s*\))?\s*\{")]
    private static partial Regex TagListInitializerPattern();

    [GeneratedRegex(@"\bTagList\s+(?<name>[A-Za-z_]\w*)\b(?!\s*\()")]
    private static partial Regex TagListVariablePattern();

    [GeneratedRegex(@"\b(?<receiver>[A-Za-z_]\w*)\s*\.\s*Add\(")]
    private static partial Regex AddCallPattern();

    [GeneratedRegex(@"^@?[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"\bconst\s+string\s+(?<name>[A-Za-z_]\w*)\s*=\s*""(?<value>[^""]*)""\s*;")]
    private static partial Regex ConstantPattern();

    [GeneratedRegex(@"\b(?:IOrganizationPseudonymizer|TelemetryOrganizationContext|AddTelemetryOrganizationPseudonym|OrganizationPseudonymAttribute|ActorRoleKindAttribute|SetOrganization)\b")]
    private static partial Regex PseudonymUsePattern();

    [GeneratedRegex(@"\be\.State\.(?<name>[A-Za-z0-9_]+)")]
    private static partial Regex StatePropertyPattern();

    [GeneratedRegex(@"toint\(e\.EventId\) == (?<id>\d{4})|EventId (?:==|in \()\s*(?<id>\d{4})|Log_s has ""(?<id>\d{4})""")]
    private static partial Regex EventIdPattern();
}
