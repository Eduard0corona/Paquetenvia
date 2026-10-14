using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Custody.Application.ProofUploads;
using Custody.Domain;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.Proofs;
using Custody.Infrastructure.ProofStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Auditing;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OBS-003 Worker follow-ups (project owner, 2026-10-14):
/// <list type="bullet">
/// <item>OBS-003-WORKER-AUDIT-TRACE-2026-10-14 (Q1 "Sí, en un PR aparte (Recommended)", QA "Se quedan vacías
/// (Recommended)"): the assignment lifecycle audit row stores the trace id of the claimed message in the API's
/// <c>request_id</c> format, and NULL without a valid stored context, never the dispatch cycle's trace;</item>
/// <item>OBS-003-WORKER-PSEUDONYM-2026-10-14 (Q2 "Sí, en un PR aparte (Recommended)", QB "SYSTEM (Recommended)", QC
/// "Sí, si los metadatos son válidos (Recommended)"): the Worker's per-message and per-photo lines carry the owner's
/// pseudonym and the role kind SYSTEM, an unknown owner or invalid metadata carries none, and neither ever reaches a
/// metric tag or the OBS-002 dimension allowlist.</item>
/// </list>
/// </summary>
public sealed class Obs003WorkerCorrelationTests
{
    private const string SampledTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string Sampled = "00-" + SampledTraceId + "-00f067aa0ba902b7-01";
    private const string UnsampledTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string Unsampled = "00-" + UnsampledTraceId + "-b7ad6b7169203331-00";

    private static readonly Guid Owner = Guid.Parse("7d1f0c2e-5a4b-4c3d-9e8f-0a1b2c3d4e5f");

    // ------------------------------------------------------------------------------------- audit request id (Q1)

    [Theory]
    [InlineData(Sampled, SampledTraceId)]
    [InlineData(Unsampled, UnsampledTraceId)]
    public void A_stored_context_gives_the_audit_request_id_in_the_exact_api_format(string stored, string expected)
    {
        var requestId = OutboxTraceContext.TraceIdOf(stored);

        Assert.Equal(expected, requestId);
        Assert.True(TraceCorrelation.IsTraceId(requestId));
        // The AI-05 Problem.traceId pattern, which is also the request_id the API writes for the same trace.
        Assert.Matches("^[0-9a-f]{32}$", requestId!);

        // The API writes request_id from HttpContext.TraceIdentifier, which HttpTraceCorrelation sets to
        // TraceCorrelation.TraceIdOf(request activity): the same value for an activity of the same trace.
        Assert.True(OutboxTraceContext.TryParse(stored, out var producer));
        using (var request = new Activity("Obs003.SyntheticRequest")
                   .SetIdFormat(ActivityIdFormat.W3C)
                   .SetParentId(producer.TraceId, producer.SpanId, producer.TraceFlags)
                   .Start())
        {
            Assert.Equal(TraceCorrelation.TraceIdOf(request), requestId);
        }

        // And it is the TraceId of every line the Worker writes while processing the message.
        TraceCorrelation.ConfigureProcess();
        using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Dispatch);
        using (var message = TraceCorrelation.StartOutboxMessage(OutboxLanes.Dispatch, stored))
        {
            Assert.Equal(TraceCorrelation.CurrentTraceId(), requestId);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00-4BF92F3577B34DA6A3CE929D0E0E4736-00F067AA0BA902B7-01")]
    [InlineData("00-00000000000000000000000000000000-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-0000000000000000-01")]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01,congo=t61rcWkgMzE")]
    [InlineData(SampledTraceId)]
    public void Without_a_valid_stored_context_the_audit_request_id_is_null_never_the_cycle_trace(string? stored)
    {
        TraceCorrelation.ConfigureProcess();
        using var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Dispatch);
        using var message = TraceCorrelation.StartOutboxMessage(OutboxLanes.Dispatch, stored);

        // The message runs under its dispatch cycle (PR 7), whose trace belongs to no request: the row keeps NULL.
        Assert.True(TraceCorrelation.IsTraceId(TraceCorrelation.CurrentTraceId()));
        Assert.Null(OutboxTraceContext.TraceIdOf(stored));
    }

    // -------------------------------------------------------------------------------- organization context (Q2)

    [Fact]
    public void The_worker_enters_the_owner_pseudonym_with_role_kind_system_and_nothing_for_an_unknown_owner()
    {
        var pseudonymizer = SyntheticPseudonymizer.Create();
        Assert.Equal("SYSTEM", TelemetryOrganizationContext.SystemRoleKind);
        Assert.Null(TelemetryOrganizationContext.Current);

        using (TelemetryOrganizationContext.EnterSystem(pseudonymizer, Owner))
        {
            var (pseudonym, roleKind) = TelemetryOrganizationContext.Current!.Value;
            Assert.Equal(pseudonymizer.Pseudonymize(Owner), pseudonym);
            Assert.StartsWith("v0-", pseudonym, StringComparison.Ordinal);
            Assert.DoesNotContain(Owner.ToString("D"), pseudonym, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Owner.ToString("N"), pseudonym, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TelemetryOrganizationContext.SystemRoleKind, roleKind);

            // An unknown owner enters nothing: the current context stays as it was.
            Assert.Null(TelemetryOrganizationContext.EnterSystem(pseudonymizer, Guid.Empty));
            Assert.Equal(pseudonym, TelemetryOrganizationContext.Current!.Value.Pseudonym);
        }

        Assert.Null(TelemetryOrganizationContext.Current);
        using (TelemetryOrganizationContext.EnterSystem(pseudonymizer, Guid.Empty))
        {
            Assert.Null(TelemetryOrganizationContext.Current);
        }

        Assert.Throws<ArgumentNullException>(() => TelemetryOrganizationContext.EnterSystem(null!, Owner));
    }

    [Fact]
    public void Neither_the_pseudonym_nor_the_role_kind_is_an_obs002_dimension_or_an_exported_metric_tag()
    {
        foreach (var name in new[]
                 {
                     TelemetryOrganizationContext.PseudonymLogProperty,
                     TelemetryOrganizationContext.RoleKindLogProperty,
                     TelemetryExportPolicy.OrganizationPseudonymAttribute,
                     TelemetryExportPolicy.ActorRoleKindAttribute,
                 })
        {
            Assert.DoesNotContain(name, TelemetryDimensions.Allowed);
            Assert.DoesNotContain(name, TelemetryExportPolicy.MetricTagKeys);
        }
    }

    /// <summary>
    /// QC: proof validation writes one line per photo only when it fails. With valid signed metadata that line carries
    /// the pseudonym of the owner the metadata names and the role kind SYSTEM; with invalid metadata it carries no
    /// organization. The poll keeps its own trace, the context ends with each photo, and the proof metrics recorded in
    /// the context never carry either value.
    /// </summary>
    [Fact]
    public async Task Proof_validation_lines_carry_the_owner_pseudonym_only_with_valid_signed_metadata()
    {
        TraceCorrelation.ConfigureProcess();
        var pseudonymizer = SyntheticPseudonymizer.Create();
        var expected = pseudonymizer.Pseudonymize(Owner);
        using var lines = new JsonConsoleLines();
        using var measurements = new CustodyMeasurements();
        var options = Options.Create(new ProofStorageOptions { MaximumConcurrency = 1 });
        using var telemetry = new ProofTelemetry(options);
        var state = new TenantDatabaseExecutionState();
        // Never opened: the scanner fails before any claim, so no database is reached.
        await using var dbContext = new CustodyDbContext(
            new DbContextOptionsBuilder<CustodyDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=obs003;Username=unused;Password=unused")
                .Options,
            state);
        var processor = new ProofValidationProcessor(
            new WorkerTenantTransactionContext<CustodyDbContext>(dbContext, state),
            new QuarantineListing([ValidObject(Owner), InvalidObject()]),
            new FailingScanner(),
            new UnusedAuditWriter(),
            new AuditPayloadRedactor(),
            options,
            new SystemClock(),
            telemetry,
            pseudonymizer,
            lines.Factory.CreateLogger<ProofValidationProcessor>());

        string pollTraceId;
        using (var poll = TraceCorrelation.StartPollCycle(ProofValidationWorker.JobName))
        {
            pollTraceId = poll.TraceId.ToHexString();
            await Assert.ThrowsAsync<ProofStorageUnavailableException>(() => processor.ProcessAvailableAsync(default));
            Assert.Null(TelemetryOrganizationContext.Current);
        }

        var failed = Assert.Single(lines.All(), line => line.GetProperty("Message").GetString() == "Secure proof validation failed.")
            .GetProperty("State");
        Assert.Equal(expected, failed.GetProperty(TelemetryOrganizationContext.PseudonymLogProperty).GetString());
        Assert.Equal("SYSTEM", failed.GetProperty(TelemetryOrganizationContext.RoleKindLogProperty).GetString());
        Assert.Equal(pollTraceId, failed.GetProperty(TraceCorrelation.LogProperty).GetString());

        var invalid = Assert.Single(lines.All(), line =>
                line.GetProperty("Message").GetString() == "Ignoring a quarantine object with invalid signed metadata.")
            .GetProperty("State");
        Assert.False(invalid.TryGetProperty(TelemetryOrganizationContext.PseudonymLogProperty, out _));
        Assert.False(invalid.TryGetProperty(TelemetryOrganizationContext.RoleKindLogProperty, out _));
        Assert.Equal(pollTraceId, invalid.GetProperty(TraceCorrelation.LogProperty).GetString());

        // No line names an organization id; the metrics recorded inside the context carry neither value.
        Assert.All(lines.Raw(), raw => Assert.DoesNotContain(Owner.ToString("D"), raw, StringComparison.OrdinalIgnoreCase));
        var tags = measurements.Tags();
        Assert.Contains(tags, tag => tag.Key == "reason_code" && Equals(tag.Value, "processing_exception"));
        Assert.All(tags, tag =>
        {
            Assert.NotEqual(TelemetryOrganizationContext.PseudonymLogProperty, tag.Key);
            Assert.NotEqual(TelemetryOrganizationContext.RoleKindLogProperty, tag.Key);
            Assert.DoesNotContain("pseudonym", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", tag.Key, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(expected, tag.Value as string);
            Assert.NotEqual(TelemetryOrganizationContext.SystemRoleKind, tag.Value as string);
        });
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private static ProofObjectDescriptor ValidObject(Guid owner)
    {
        var order = Guid.NewGuid();
        var session = Guid.NewGuid();
        return new ProofObjectDescriptor(
            ProofObjectKeys.Quarantine(owner, order, session),
            68,
            "image/png",
            "\"etag-obs003\"",
            null,
            new Dictionary<string, string>
            {
                [S3CompatibleProofObjectStorage.SessionIdMetadata] = session.ToString("D"),
                [S3CompatibleProofObjectStorage.OrderIdMetadata] = order.ToString("D"),
                [S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata] = owner.ToString("D"),
                [S3CompatibleProofObjectStorage.RequestedByMetadata] = Guid.NewGuid().ToString("D"),
                [S3CompatibleProofObjectStorage.ProofTypeMetadata] = ProofType.DeliveryPhoto.ToContractValue(),
                [S3CompatibleProofObjectStorage.SizeBytesMetadata] = "68",
            });
    }

    /// <summary>Signed metadata that names no session: the object is ignored and names no organization.</summary>
    private static ProofObjectDescriptor InvalidObject() =>
        new(
            $"quarantine/{Owner:D}/{Guid.NewGuid():D}/{Guid.NewGuid():D}",
            68,
            "image/png",
            "\"etag-obs003-invalid\"",
            null,
            new Dictionary<string, string>
            {
                [S3CompatibleProofObjectStorage.OwnerOrganizationIdMetadata] = Owner.ToString("D"),
            });

    private sealed class QuarantineListing(IReadOnlyList<ProofObjectDescriptor> objects) : IProofObjectStorage
    {
        public bool IsEnabled => true;

        public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ProofUploadGrant> CreateUploadGrantAsync(
            Guid ownerOrganizationId, Guid orderId, Guid sessionId, Guid requestedBy, ProofType proofType,
            string contentType, long sizeBytes, byte[]? sha256, DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => throw Unexpected();

        public async IAsyncEnumerable<ProofObjectDescriptor> ListQuarantineAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in objects)
            {
                await Task.Yield();
                yield return item;
            }
        }

        public Task<Stream> OpenReadAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task PromoteAsync(ValidatedProofObject proof, CancellationToken cancellationToken) => throw Unexpected();

        public Task<ProofObjectDescriptor?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task DeleteQuarantineAsync(string objectKey, string? versionId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<string> CreateInternalDownloadUrlAsync(string objectKey, CancellationToken cancellationToken) =>
            throw Unexpected();

        private static InvalidOperationException Unexpected() => new("Unexpected proof storage call.");
    }

    /// <summary>A scanner that is unavailable: the photo fails before any claim and writes its failure line.</summary>
    private sealed class FailingScanner : IProofThreatScanner
    {
        public bool IsEnabled => true;

        public ValueTask<ProofThreatScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected scan.");

        public ValueTask<bool> IsVerdictPendingAsync(ProofObjectDescriptor descriptor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic scanner outage.");
    }

    private sealed class UnusedAuditWriter : IAppendOnlyAuditWriter
    {
        public Task WriteAsync(DbConnection connection, DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No audit row is expected.");
    }

    /// <summary>Every tag recorded on the proof meter (<c>Paquetenvia.Custody</c>) while the test runs.</summary>
    private sealed class CustodyMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<KeyValuePair<string, object?>> _tags = new();

        internal CustodyMeasurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Paquetenvia.Custody")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(tags));
            _listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Record(tags));
            _listener.Start();
        }

        internal IReadOnlyList<KeyValuePair<string, object?>> Tags() => _tags.ToArray();

        public void Dispose() => _listener.Dispose();

        private void Record(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                _tags.Enqueue(tag);
            }
        }
    }

    /// <summary>The lines the JSON console would write: the framework's JSON formatter behind the hosts' factory.</summary>
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

        internal IReadOnlyList<string> Raw() => _lines.ToArray();

        internal IReadOnlyList<JsonElement> All() =>
            _lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

        public void Dispose()
        {
            Factory.Dispose();
            _formatters.Dispose();
        }

        private sealed class FormattingProvider(ConsoleFormatter formatter, ConcurrentQueue<string> lines) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new FormattingLogger(categoryName, formatter, lines);

            public void Dispose()
            {
            }
        }

        private sealed class FormattingLogger(string category, ConsoleFormatter formatter, ConcurrentQueue<string> lines)
            : ILogger
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
                    new Microsoft.Extensions.Logging.Abstractions.LogEntry<TState>(
                        logLevel, category, eventId, state, exception, messageFormatter),
                    scopeProvider: null,
                    writer);
                lines.Enqueue(writer.ToString().TrimEnd());
            }
        }
    }
}
