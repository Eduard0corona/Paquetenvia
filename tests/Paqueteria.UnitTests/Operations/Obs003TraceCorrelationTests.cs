using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dispatch.Infrastructure.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Scheduling;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Observability;
using Paqueteria.Infrastructure.Scheduling;

namespace Paqueteria.UnitTests.Operations;

/// <summary>
/// OBS-003 (phase 4 PR 1): W3C trace context with System.Diagnostics only. Inbound context is never extracted,
/// nothing is injected, every Worker job cycle and outbox dispatch cycle is a new root trace, and every log line
/// written inside a trace carries its 32-hex id in the JSON console state.
/// </summary>
public sealed class Obs003TraceCorrelationTests
{
    private const string InboundTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    private const string InboundTraceId = "0af7651916cd43dd8448eb211c80319c";

    [Fact]
    public void The_propagator_extracts_no_inbound_context_and_injects_nothing()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["traceparent"] = InboundTraceParent,
            ["tracestate"] = "vendor=synthetic",
            ["baggage"] = "tenant=synthetic-org,user=synthetic-user",
            ["Request-Id"] = "|synthetic.1.",
            ["Correlation-Context"] = "tenant=synthetic-org",
        };
        var reads = 0;
        DistributedContextPropagator.PropagatorGetterCallback getter = (carrier, name, out value, out values) =>
        {
            reads++;
            values = null;
            value = ((Dictionary<string, string>)carrier!).GetValueOrDefault(name);
        };
        var propagator = NoTraceContextPropagator.Instance;

        propagator.ExtractTraceIdAndState(headers, getter, out var traceId, out var traceState);
        Assert.Null(traceId);
        Assert.Null(traceState);
        Assert.Null(propagator.ExtractBaggage(headers, getter));
        Assert.Equal(0, reads);
        Assert.Empty(propagator.Fields);

        using var source = new ActivitySource("Obs003.Propagator");
        using var listener = RecordingListener(source.Name);
        using var activity = source.StartActivity("synthetic")!;
        activity.TraceStateString = "vendor=synthetic";
        activity.AddBaggage("tenant", "synthetic-org");
        var written = new List<string>();
        propagator.Inject(activity, written, (carrier, name, value) => ((List<string>)carrier!).Add(name));
        Assert.Empty(written);
    }

    [Fact]
    public void ConfigureProcess_installs_the_propagator_and_a_propagation_only_listener()
    {
        TraceCorrelation.ConfigureProcess();
        TraceCorrelation.ConfigureProcess();

        Assert.Same(NoTraceContextPropagator.Instance, DistributedContextPropagator.Current);
        Assert.True(TraceCorrelation.Source.HasListeners());

        // The hosting source gets activities with W3C ids that record nothing (no tags, events or baggage).
        using var hosting = new ActivitySource("Microsoft.AspNetCore");
        Activity.Current = null;
        using var request = hosting.StartActivity("Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server);
        Assert.NotNull(request);
        Assert.Equal(ActivityIdFormat.W3C, request.IdFormat);
        Assert.False(request.IsAllDataRequested);
        Assert.False(request.Recorded);

        // Other sources (HTTP client, database, SignalR) stay unobserved.
        using var other = new ActivitySource("Obs003.Unobserved");
        Assert.False(other.HasListeners());
    }

    [Fact]
    public void A_root_activity_never_continues_the_ambient_trace()
    {
        TraceCorrelation.ConfigureProcess();
        using var source = new ActivitySource("Obs003.Ambient");
        using var listener = RecordingListener(source.Name);
        using var ambient = source.StartActivity("ambient")!;

        using (var cycle = TraceCorrelation.StartJobCycle("synthetic.job"))
        {
            Assert.Null(cycle.Parent);
            Assert.Equal(default, cycle.ParentSpanId);
            Assert.False(cycle.HasRemoteParent);
            Assert.NotEqual(ambient.TraceId, cycle.TraceId);
            Assert.Equal(TraceCorrelation.JobCycleOperation, cycle.OperationName);
            Assert.Equal("synthetic.job", cycle.DisplayName);
            Assert.True(TraceCorrelation.IsTraceId(TraceCorrelation.CurrentTraceId()));
            Assert.Same(cycle, Activity.Current);
        }

        Assert.Null(Activity.Current);
    }

    [Theory]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736", true)]
    [InlineData("4BF92F3577B34DA6A3CE929D0E0E4736", false)]
    [InlineData("00000000000000000000000000000000", false)]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e473", false)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", false)]
    [InlineData("0HN7SYNTHETIC:00000001", false)]
    [InlineData(null, false)]
    public void A_trace_id_is_32_lowercase_hex_digits_and_not_all_zero(string? value, bool expected) =>
        Assert.Equal(expected, TraceCorrelation.IsTraceId(value));

    [Fact]
    public void Every_json_console_line_written_inside_a_trace_carries_its_trace_id_and_lines_outside_carry_none()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new JsonConsoleLines();
        var logger = lines.Factory.CreateLogger("Obs003.Synthetic");

        Activity.Current = null;
        logger.LogInformation("Outside any trace with {Outcome}.", "idle");
        string traceId;
        using (var cycle = TraceCorrelation.StartOutboxDispatchCycle(OutboxLanes.Dispatch))
        {
            traceId = cycle.TraceId.ToHexString();
            logger.LogWarning("Inside a cycle with {Outcome} after {Rows} rows.", "LOOP_FAILURE", 3);
        }

        var outside = lines.Parse(0);
        Assert.False(outside.GetProperty("State").TryGetProperty("TraceId", out _));
        var inside = lines.Parse(1);
        var state = inside.GetProperty("State");
        Assert.Equal(traceId, state.GetProperty("TraceId").GetString());
        Assert.True(TraceCorrelation.IsTraceId(traceId));
        // The message, the template and every original property are unchanged; the trace id is the only addition.
        Assert.Equal("Inside a cycle with LOOP_FAILURE after 3 rows.", inside.GetProperty("Message").GetString());
        Assert.Equal("LOOP_FAILURE", state.GetProperty("Outcome").GetString());
        Assert.Equal(3, state.GetProperty("Rows").GetInt32());
        Assert.Equal(
            "Inside a cycle with {Outcome} after {Rows} rows.",
            state.GetProperty("{OriginalFormat}").GetString());
        Assert.Equal(
            ["Outcome", "Rows", "{OriginalFormat}", "TraceId"],
            state.EnumerateObject().Select(property => property.Name).ToArray());
        // One identifier per line: no scope (hosting RequestId, ConnectionId) is ever written.
        Assert.False(inside.TryGetProperty("Scopes", out _));
        Assert.DoesNotContain("RequestId", lines.Raw(1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_that_already_names_TraceId_is_left_untouched()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new JsonConsoleLines();
        var logger = lines.Factory.CreateLogger("Obs003.Synthetic");
        using (TraceCorrelation.StartJobCycle("synthetic.job"))
        {
            logger.LogInformation("Explicit {TraceId}.", "explicit-value");
        }

        var state = lines.Parse(0).GetProperty("State");
        Assert.Equal("explicit-value", state.GetProperty("TraceId").GetString());
        Assert.Single(state.EnumerateObject(), property => property.Name == "TraceId");
    }

    [Fact]
    public async Task Each_job_cycle_is_a_new_root_trace_whose_id_is_in_every_line_of_the_cycle()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new JsonConsoleLines();
        var jobLogger = lines.Factory.CreateLogger("Obs003.Job");
        var scheduler = new PeriodicJobScheduler(TimeProvider.System, lines.Factory.CreateLogger<PeriodicJobScheduler>());
        using var cancellation = new CancellationTokenSource();
        var job = new ScriptedJob(run =>
        {
            jobLogger.LogInformation("Job cycle body with {Rows} rows.", run);
            if (run == 2)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            return Task.CompletedTask;
        });

        // An ambient activity (as if the loop had been started inside some other trace) is never continued.
        using var source = new ActivitySource("Obs003.Scheduler");
        using var listener = RecordingListener(source.Name);
        using var ambient = source.StartActivity("ambient")!;
        await scheduler.RunAsync(job, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        var parsed = Enumerable.Range(0, lines.Count).Select(lines.Parse).ToArray();
        var bodies = parsed.Where(line => line.GetProperty("Category").GetString() == "Obs003.Job").ToArray();
        var cycles = parsed.Where(line => line.GetProperty("EventId").GetInt32() == TelemetryEvents.ScheduledJobCycle.Id).ToArray();
        Assert.Equal(2, bodies.Length);
        var first = TraceIdOf(bodies[0]);
        var second = TraceIdOf(bodies[1]);
        Assert.True(TraceCorrelation.IsTraceId(first));
        Assert.True(TraceCorrelation.IsTraceId(second));
        Assert.NotEqual(first, second);
        Assert.NotEqual(ambient.TraceId.ToHexString(), first);
        Assert.NotEqual(ambient.TraceId.ToHexString(), second);
        // The OBS-002 cycle line of the first cycle shares that cycle's id; its allowlisted properties are unchanged.
        var cycleLine = Assert.Single(cycles);
        Assert.Equal(first, TraceIdOf(cycleLine));
        Assert.Equal(
            ["DurationMs", "Job", "Outcome", "TraceId"],
            cycleLine.GetProperty("State").EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name != "{OriginalFormat}")
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Each_outbox_dispatch_cycle_is_a_new_root_trace_whose_id_is_in_its_log_lines()
    {
        TraceCorrelation.ConfigureProcess();
        using var lines = new JsonConsoleLines();
        var store = new FailingDispatchOutboxStore();
        var options = Options.Create(new AssignmentLifecycleOptions
        {
            Provider = AssignmentLifecycleProviderKind.PostgreSql,
            PollIntervalMilliseconds = 10,
        });
        var lanes = new OutboxLaneMonitor(NullLogger<OutboxLaneMonitor>.Instance, TimeProvider.System);
        using var services = new ServiceCollection().BuildServiceProvider();
        var processor = new AssignmentLifecycleProcessor(
            store,
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            new SystemClock(),
            lanes,
            lines.Factory.CreateLogger<AssignmentLifecycleProcessor>());
        using var dispatcher = new AssignmentLifecycleDispatcher(
            store,
            processor,
            options,
            lanes,
            SyntheticPseudonymizer.Create(),
            TimeProvider.System,
            lines.Factory.CreateLogger<AssignmentLifecycleDispatcher>());

        await dispatcher.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (lines.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await dispatcher.StopAsync(CancellationToken.None);

        var failures = Enumerable.Range(0, lines.Count)
            .Select(lines.Parse)
            .Where(line => line.GetProperty("State").GetProperty("Outcome").GetString() == "LOOP_FAILURE")
            .Select(line => TraceIdOf(line) ?? string.Empty)
            .ToArray();
        Assert.True(failures.Length >= 3, $"expected three failed cycles, saw {failures.Length}");
        Assert.All(failures, traceId => Assert.True(TraceCorrelation.IsTraceId(traceId)));
        Assert.Equal(failures.Length, failures.Distinct(StringComparer.Ordinal).Count());
        // The stale requeue and the claim of one cycle ran in that cycle's trace.
        Assert.Equal(failures.Length, store.TraceIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Subset(store.TraceIds.ToHashSet(StringComparer.Ordinal), failures.ToHashSet(StringComparer.Ordinal));
    }

    private static string? TraceIdOf(JsonElement line) =>
        line.GetProperty("State").TryGetProperty("TraceId", out var value) ? value.GetString() : null;

    private static ActivityListener RecordingListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// The lines the JSON console would write: the framework's own JSON formatter (production options, no scopes)
    /// behind <see cref="TraceCorrelatedLoggerFactory"/>, exactly as the hosts compose it.
    /// </summary>
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

        internal int Count => _lines.Count;

        internal string Raw(int index) => _lines.ElementAt(index);

        internal JsonElement Parse(int index) => JsonDocument.Parse(Raw(index)).RootElement.Clone();

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
                new Microsoft.Extensions.Logging.Abstractions.LogEntry<TState>(
                    logLevel, category, eventId, state, exception, messageFormatter),
                scopeProvider: null,
                writer);
            lines.Enqueue(writer.ToString().TrimEnd());
        }
    }

    private sealed class ScriptedJob(Func<int, Task> cycle) : IScheduledJob
    {
        private int _runs;

        public string Name => "synthetic.obs003";

        public TimeSpan Interval => TimeSpan.FromMilliseconds(1);

        public Task RunOnceAsync(CancellationToken cancellationToken) => cycle(Interlocked.Increment(ref _runs));
    }

    private sealed class FailingDispatchOutboxStore : IDispatchOutboxStore
    {
        public ConcurrentQueue<string> TraceIds { get; } = new();

        public Task<IReadOnlyList<ClaimedDispatchOutboxMessage>> ClaimAsync(
            string workerId,
            int batchSize,
            TimeSpan lease,
            CancellationToken cancellationToken) => Fail<IReadOnlyList<ClaimedDispatchOutboxMessage>>();

        public Task<int> RequeueStaleAsync(int batchSize, int maximumAttempts, CancellationToken cancellationToken) =>
            Fail<int>();

        public Task<bool> SettleAsync(
            Guid id,
            Guid leaseToken,
            string status,
            string? errorCode,
            DateTimeOffset? availableAt,
            CancellationToken cancellationToken) => Fail<bool>();

        public Task<string?> ResolveConsumerAsync(string topic, CancellationToken cancellationToken) =>
            Fail<string?>();

        private Task<T> Fail<T>()
        {
            TraceIds.Enqueue(TraceCorrelation.CurrentTraceId() ?? "none");
            return Task.FromException<T>(new InvalidOperationException("synthetic database outage"));
        }
    }
}
