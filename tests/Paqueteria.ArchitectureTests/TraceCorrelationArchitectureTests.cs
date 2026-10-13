using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-003 (phase 4 PR 1) guards: both hosts configure W3C trace correlation before anything builds an HTTP
/// handler, the API sets the request trace id first, every Azure SDK client strips trace context, every background
/// loop runs its cycles in their own root trace, and no activity is ever given an event or baggage (request paths,
/// queries, headers and bodies included). Since phase 4 PR 6 one reviewed file, <see cref="ExportPolicy"/>, writes the
/// allowlisted attributes of an exported span and nothing else writes any.
/// </summary>
public sealed partial class TraceCorrelationArchitectureTests
{
    /// <summary>
    /// Every file that uses an Azure SDK namespace (any <c>Azure.*</c> but <c>Azure.Core</c>, or
    /// <c>Microsoft.Extensions.Azure</c>) or an extension that builds Azure clients, each reviewed to strip trace
    /// context. <see cref="AzureClientTracingGuard"/> checks every construction in them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ReviewedAzureClientFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Cloud/AzureWorkloadCredential.cs"] =
                "the managed identity credential, built with CreateOptions, which ends in WithoutDistributedTracing",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Cloud/KeyVaultSecretsConfiguration.cs"] =
                "the Key Vault secrets client, built with options through WithoutDistributedTracing",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/DataProtection/DataProtectionKeyEncryption.cs"] =
                "the data protection key resolver, built by CreateKeyVault with CreateClientOptions",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/DataProtection/DependencyInjection.cs"] =
                "ProtectKeysWithAzureKeyVault through the resolver overload with DataProtectionKeyEncryptionKeyResolver",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Security/Pii/AzureKeyVaultPiiKeyWrapClient.cs"] =
                "the key and cryptography clients, built with options through Configure, which ends in WithoutDistributedTracing",
            ["src/Modules/Custody/Custody.Infrastructure/ProofStorage/AzureBlobProofGateway.cs"] =
                "the Blob service client, built with CreateClientOptions, which ends in WithoutDistributedTracing",
            ["src/Modules/Custody/Custody.Infrastructure/ProofStorage/AzureBlobProofObjectStorage.cs"] =
                "SAS builders and Blob models only; every Blob request goes through IProofBlobGateway",
            ["src/Paqueteria.Api/Observability/AzureMonitorTelemetryExport.cs"] =
                "the Azure Monitor trace and metric exporters (OBS-003 PR 6), built with CreateExporterOptions, which ends in WithoutDistributedTracing",
        };

    private const string Scheduler = "src/BuildingBlocks/Paqueteria.Infrastructure/Scheduling/PeriodicJobScheduler.cs";

    /// <summary>OBS-003 PR 6: the only file that writes attributes, a status or a name on an activity it did not start.</summary>
    private const string ExportPolicy = "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TelemetryExportPolicy.cs";

    /// <summary>Background loops (one per <c>BackgroundService</c> class) that run each cycle in a new root trace.</summary>
    private static readonly IReadOnlyDictionary<string, string> CycleTracedLoops =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // OBS-003 PR 6 (Q-PR6-1): a poll, not an ADR-034 scheduled job, so its traces are never exported.
            ["src/Modules/Custody/Custody.Infrastructure/Proofs/ProofValidationWorker.cs"] = "TraceCorrelation.StartPollCycle(",
            ["src/Modules/Notifications/Notifications.Infrastructure/Dispatching/NotificationsOutboxDispatcher.cs"] =
                "TraceCorrelation.StartOutboxDispatchCycle(",
            ["src/Modules/Dispatch/Dispatch.Infrastructure/Lifecycle/AssignmentLifecycleProcessor.cs"] =
                "TraceCorrelation.StartOutboxDispatchCycle(",
            ["src/Modules/Realtime/Realtime.Infrastructure/Dispatching/RealtimeOutboxDispatchers.cs"] =
                "TraceCorrelation.StartOutboxDispatchCycle(",
        };

    /// <summary>Background services that are not cycle loops themselves, with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> OtherBackgroundServices =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Database/Outbox/Retention/OutboxRetentionHostedService.cs"] =
                "delegates every cycle to IJobScheduler (PeriodicJobScheduler)",
            ["src/Modules/Orders/Orders.Infrastructure/Lifecycle/ClaimWindowFinalizationHostedService.cs"] =
                "delegates every cycle to IJobScheduler (PeriodicJobScheduler)",
            ["src/Modules/Orders/Orders.Infrastructure/Lifecycle/OrderAutoCloseHostedService.cs"] =
                "delegates every cycle to IJobScheduler (PeriodicJobScheduler)",
            ["src/Modules/Custody/Custody.Infrastructure/Cleanup/OperationalCleanupJobs.cs"] =
                "delegates every cycle to IJobScheduler (PeriodicJobScheduler)",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Observability/HttpStatusTelemetry.cs"] =
                "the OBS-002 per-minute status summary spans many requests and belongs to none",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Observability/RateLimitRejectionSummary.cs"] =
                "the SEC-004 per-minute rate-limit summary spans many requests and belongs to none",
        };

    [Theory]
    [InlineData("src/Paqueteria.Api/Program.cs")]
    [InlineData("src/Paqueteria.Worker/Program.cs")]
    public void Hosts_configure_trace_correlation_before_anything_builds_a_handler(string path)
    {
        var program = File.ReadAllText(TestRepository.GetPath(path));
        var configure = program.IndexOf("TraceCorrelation.ConfigureProcess();", StringComparison.Ordinal);
        var builder = program.IndexOf("WebApplication.CreateBuilder(args);", StringComparison.Ordinal);
        var keyVault = program.IndexOf("AddPaqueteriaKeyVaultSecrets();", StringComparison.Ordinal);
        Assert.True(configure > 0 && configure < builder && builder < keyVault, path);
        Assert.Contains("builder.Services.AddTraceCorrelation();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_sets_the_request_trace_id_first_and_writes_it_into_every_problem()
    {
        var program = File.ReadAllText(TestRepository.GetPath("src/Paqueteria.Api/Program.cs"));
        var build = program.IndexOf("var app = builder.Build();", StringComparison.Ordinal);
        var correlation = program.IndexOf("app.UseTraceCorrelation();", StringComparison.Ordinal);
        var firstUse = AppUsePattern().Match(program, build).Index;
        Assert.True(build > 0 && correlation == firstUse, "app.UseTraceCorrelation() must be the first middleware");
        Assert.Contains("builder.Services.AddProblemDetailsTraceId();", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_azure_sdk_client_is_reviewed_to_strip_trace_context()
    {
        var sources = SourceFiles().Select(Guarded).ToArray();

        // Every file that uses an Azure SDK namespace or a guarded extension is reviewed, and no entry is stale.
        Assert.Equal(ReviewedAzureClientFiles.Keys.Order(StringComparer.Ordinal), AzureClientTracingGuard.InScope(sources));
        // Every construction of a client, credential or key resolver in them, and every guarded extension anywhere,
        // goes through WithoutDistributedTracing.
        Assert.Empty(AzureClientTracingGuard.Violations(sources, ReviewedAzureClientFiles.Keys.ToHashSet(StringComparer.Ordinal)));
        Assert.Empty(ProjectFiles().SelectMany(file => AzureClientTracingGuard.ProjectViolations(Relative(file), File.ReadAllText(file))));
    }

    [Fact]
    public void Only_the_azure_transport_names_trace_context_headers()
    {
        var files = SourceFiles()
            .Where(file => TraceHeaderLiteralPattern().IsMatch(CodeWithoutComments(file)))
            .Select(Relative)
            .ToArray();

        Assert.Equal(["src/BuildingBlocks/Paqueteria.Infrastructure/Cloud/AzureClientTracing.cs"], files);
    }

    [Fact]
    public void Every_background_loop_runs_its_cycles_in_their_own_root_trace()
    {
        var services = SourceFiles()
            .Where(file => BackgroundServicePattern().IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // A new BackgroundService must be reviewed: a cycle loop starts its cycle trace, anything else says why not.
        Assert.Equal(
            CycleTracedLoops.Keys.Concat(OtherBackgroundServices.Keys).Order(StringComparer.Ordinal),
            services);
        foreach (var (file, call) in CycleTracedLoops)
        {
            var code = CodeWithoutComments(TestRepository.GetPath(file));
            Assert.True(
                BackgroundServicePattern().Matches(code).Count == Regex.Count(code, Regex.Escape(call)),
                $"{file}: every BackgroundService loop must call {call} once per cycle");
        }

        // The ADR-034 scheduler that the other job hosts delegate to starts one root trace per job cycle.
        Assert.Single(
            Regex.Matches(CodeWithoutComments(TestRepository.GetPath(Scheduler)), Regex.Escape("TraceCorrelation.StartJobCycle(")));
    }

    [Fact]
    public void Activities_are_started_only_by_trace_correlation_and_never_carry_tags_events_or_baggage()
    {
        // OBS-003 PR 6 widens one rule narrowly: TelemetryExportPolicy, and only it, writes the allowlisted attributes
        // of an exported span (and clears its status description and rebuilds its name). Starting activities, events,
        // links and baggage stay forbidden everywhere.
        var correlation = "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TraceCorrelation.cs";
        var starters = SourceFiles()
            .Where(file => CodeWithoutComments(file).Contains("StartActivity(", StringComparison.Ordinal) ||
                           CodeWithoutComments(file).Contains("new ActivitySource(", StringComparison.Ordinal) ||
                           CodeWithoutComments(file).Contains("new ActivityListener", StringComparison.Ordinal) ||
                           NewActivityPattern().IsMatch(CodeWithoutComments(file)))
            .Select(Relative)
            .ToArray();
        Assert.Equal([correlation], starters);

        var enrichers = SourceFiles()
            .Where(file => ActivityEnrichmentPattern().IsMatch(CodeWithoutComments(file)))
            .Select(Relative)
            .ToArray();
        Assert.Equal([ExportPolicy], enrichers);
        var policy = CodeWithoutComments(TestRepository.GetPath(ExportPolicy));
        Assert.Empty(EventOrBaggagePattern().Matches(policy));
        // Every attribute it writes is a literal of its allowlist or one of its two organization constants.
        var written = SetTagKeyPattern().Matches(policy).Select(match => match.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            [
                "\"http.request.method\"",
                "\"http.response.status_code\"",
                "\"http.route\"",
                "ActorRoleKindAttribute",
                "OrganizationPseudonymAttribute",
                "tag.Key",
            ],
            written.Order(StringComparer.Ordinal));
        Assert.Contains("activity.SetTag(tag.Key, null);", policy, StringComparison.Ordinal);

        // The only activity names set after start are the fixed job or lane name of a cycle and the export's
        // rebuilt span name, whatever the receiver.
        var renames = SourceFiles()
            .Where(file => AzureClientTracingGuard.DisplayNameAssignments(CodeWithoutComments(file)).Count > 0)
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([ExportPolicy, correlation], renames);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string CodeWithoutComments(string file) => string.Join(
        '\n',
        File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>The file as the guard reads it: full-line comments blanked, so line numbers stay true.</summary>
    private static GuardedSource Guarded(string file) => new(
        Relative(file),
        string.Join(
            '\n',
            File.ReadLines(file).Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line)));

    /// <summary>Project and MSBuild files that could add a global using to every file of a project.</summary>
    private static IEnumerable<string> ProjectFiles() =>
        new[] { "*.csproj", "*.props", "*.targets" }
            .SelectMany(pattern => Directory.EnumerateFiles(TestRepository.GetPath("src"), pattern, SearchOption.AllDirectories))
            .Concat(new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" }
                .Select(TestRepository.GetPath)
                .Where(File.Exists))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string file) =>
        Path.GetRelativePath(TestRepository.Root, file).Replace(Path.DirectorySeparatorChar, '/');

    [GeneratedRegex(@"\bapp\.Use[A-Za-z]*\(")]
    private static partial Regex AppUsePattern();

    [GeneratedRegex(@"(?<![\w.])new\s+(?:global::)?(?:System\.Diagnostics\.)?Activity\s*\(")]
    private static partial Regex NewActivityPattern();

    [GeneratedRegex("\"(?:traceparent|tracestate|baggage|Request-Id|Correlation-Context)\"", RegexOptions.IgnoreCase)]
    private static partial Regex TraceHeaderLiteralPattern();

    [GeneratedRegex(@"\)\s*:\s*BackgroundService\b|class\s+\w+\s*:\s*BackgroundService\b")]
    private static partial Regex BackgroundServicePattern();

    [GeneratedRegex(@"\.(?:SetTag|AddTag|SetBaggage|AddBaggage|AddEvent|AddLink|SetStatus|AddException)\(")]
    private static partial Regex ActivityEnrichmentPattern();

    [GeneratedRegex(@"\.(?:AddTag|SetBaggage|AddBaggage|AddEvent|AddLink|AddException)\(")]
    private static partial Regex EventOrBaggagePattern();

    [GeneratedRegex(@"\.SetTag\(\s*(?<key>""[^""]*""|[A-Za-z_][\w.]*)\s*,")]
    private static partial Regex SetTagKeyPattern();
}
