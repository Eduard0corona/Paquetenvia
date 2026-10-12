using System.Text.RegularExpressions;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

/// <summary>
/// OBS-003 (phase 4 PR 1) guards: both hosts configure W3C trace correlation before anything builds an HTTP
/// handler, the API sets the request trace id first, every Azure SDK client strips trace context, every background
/// loop runs its cycles in their own root trace, and no activity is ever given a tag, event or baggage (request
/// paths, queries, headers and bodies included).
/// </summary>
public sealed partial class TraceCorrelationArchitectureTests
{
    /// <summary>Files that build an Azure SDK client, credential or options, each reviewed to strip trace context.</summary>
    private static readonly IReadOnlyDictionary<string, string> ReviewedAzureClientFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Cloud/AzureWorkloadCredential.cs"] =
                "managed identity credential options through WithoutDistributedTracing",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Cloud/KeyVaultSecretsConfiguration.cs"] =
                "Key Vault secrets client options through WithoutDistributedTracing",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/DataProtection/DataProtectionKeyEncryption.cs"] =
                "data protection cryptography client options through WithoutDistributedTracing",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/DataProtection/DependencyInjection.cs"] =
                "the key resolver is built with DataProtectionKeyEncryptionKeyResolver.CreateClientOptions()",
            ["src/BuildingBlocks/Paqueteria.Infrastructure/Security/Pii/AzureKeyVaultPiiKeyWrapClient.cs"] =
                "key and cryptography client options through Configure, which ends in WithoutDistributedTracing",
            ["src/Modules/Custody/Custody.Infrastructure/ProofStorage/AzureBlobProofGateway.cs"] =
                "Blob client options through CreateClientOptions, which ends in WithoutDistributedTracing",
        };

    private const string Scheduler = "src/BuildingBlocks/Paqueteria.Infrastructure/Scheduling/PeriodicJobScheduler.cs";

    /// <summary>Background loops (one per <c>BackgroundService</c> class) that run each cycle in a new root trace.</summary>
    private static readonly IReadOnlyDictionary<string, string> CycleTracedLoops =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Modules/Custody/Custody.Infrastructure/Proofs/ProofValidationWorker.cs"] = "TraceCorrelation.StartJobCycle(",
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
        var files = SourceFiles()
            .Where(file => AzureClientPattern().IsMatch(CodeWithoutComments(file)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ReviewedAzureClientFiles.Keys.Order(StringComparer.Ordinal), files);
        foreach (var file in files)
        {
            var code = CodeWithoutComments(TestRepository.GetPath(file));
            Assert.True(
                code.Contains(".WithoutDistributedTracing()", StringComparison.Ordinal) ||
                code.Contains("DataProtectionKeyEncryptionKeyResolver.CreateClientOptions()", StringComparison.Ordinal),
                $"{file}: {ReviewedAzureClientFiles[file]}");
        }
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
        var correlation = "src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TraceCorrelation.cs";
        var starters = SourceFiles()
            .Where(file => CodeWithoutComments(file).Contains("StartActivity(", StringComparison.Ordinal) ||
                           CodeWithoutComments(file).Contains("new ActivitySource(", StringComparison.Ordinal) ||
                           CodeWithoutComments(file).Contains("new ActivityListener", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();
        Assert.Equal([correlation], starters);

        var enrichers = SourceFiles()
            .Where(file => ActivityEnrichmentPattern().IsMatch(CodeWithoutComments(file)))
            .Select(Relative)
            .ToArray();
        Assert.Empty(enrichers);

        // The only activity name set after start is the fixed job or lane name of a cycle.
        var renames = SourceFiles()
            .Where(file => CodeWithoutComments(file).Contains("activity.DisplayName", StringComparison.Ordinal))
            .Select(Relative)
            .ToArray();
        Assert.Equal([correlation], renames);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(TestRepository.GetPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string CodeWithoutComments(string file) => string.Join(
        '\n',
        File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Relative(string file) =>
        Path.GetRelativePath(TestRepository.Root, file).Replace(Path.DirectorySeparatorChar, '/');

    [GeneratedRegex(@"\bapp\.Use[A-Za-z]*\(")]
    private static partial Regex AppUsePattern();

    [GeneratedRegex(
        @"new (?:SecretClient|KeyClient|CryptographyClient|BlobServiceClient|BlobContainerClient|BlobClient|" +
        @"ManagedIdentityCredential|DefaultAzureCredential|WorkloadIdentityCredential|ChainedTokenCredential|" +
        @"EnvironmentCredential|KeyResolver|[A-Za-z]+ClientOptions|[A-Za-z]+CredentialOptions)\(")]
    private static partial Regex AzureClientPattern();

    [GeneratedRegex("\"(?:traceparent|tracestate|baggage|Request-Id|Correlation-Context)\"", RegexOptions.IgnoreCase)]
    private static partial Regex TraceHeaderLiteralPattern();

    [GeneratedRegex(@"\)\s*:\s*BackgroundService\b|class\s+\w+\s*:\s*BackgroundService\b")]
    private static partial Regex BackgroundServicePattern();

    [GeneratedRegex(@"\.(?:SetTag|AddTag|SetBaggage|AddBaggage|AddEvent|AddLink|SetStatus|AddException)\(")]
    private static partial Regex ActivityEnrichmentPattern();
}
