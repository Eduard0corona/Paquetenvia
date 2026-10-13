using System.Diagnostics;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003 (phase 4 PR 1, OBS-003-W3C-CORRELATION-2026-10-12): W3C Trace Context built on
/// <see cref="Activity"/> alone, with no telemetry package. One identifier, the 32-hex W3C trace id,
/// ties together the log lines, the audit row (<c>platform.audit_logs.request_id</c>) and the problem
/// <c>traceId</c> of an API request, and every Worker job cycle and outbox dispatch cycle runs in its own
/// new root trace.
/// </summary>
/// <remarks>
/// <para>
/// Trace context never crosses the process boundary. A browser can send <c>traceparent</c> with the sampled
/// flag set, so inbound context is untrusted: <see cref="NoTraceContextPropagator"/> extracts nothing and every
/// request starts a new trace. It injects nothing either, so no outgoing HTTP request (Google Maps, messaging,
/// voice, AuthCenter, Azure, S3) carries <c>traceparent</c>, <c>tracestate</c>, <c>baggage</c> or
/// <c>Request-Id</c>. Azure SDK clients add <c>traceparent</c> through their own pipeline policy; every Azure
/// client options instance goes through <c>AzureClientTracing.WithoutDistributedTracing</c> for that.
/// </para>
/// <para>
/// The listener is the smallest one that guarantees ids: it samples <see cref="ActivitySamplingResult.PropagationData"/>
/// for the hosting source and for <see cref="ActivitySourceName"/> only, so activities exist and carry W3C ids but
/// record no tags, events or baggage. The OBS-003 export (phase 4 PR 6), when configuration turns it on, adds the
/// OpenTelemetry listener and sampler next to this one; this listener stays, so the traces that sampler drops still
/// have W3C ids. Nothing here records or exports anything.
/// </para>
/// </remarks>
public static class TraceCorrelation
{
    /// <summary>The application's own activity source (Worker job and outbox dispatch cycles).</summary>
    public const string ActivitySourceName = "Paqueteria";

    /// <summary>Structured log property that carries the trace id on every line written inside a trace.</summary>
    public const string LogProperty = "TraceId";

    /// <summary>Optional AI-05 <c>Problem</c> member that carries the trace id of the request.</summary>
    public const string ProblemMember = "traceId";

    /// <summary>Operation name of a Worker job cycle activity; the display name is the fixed job name.</summary>
    public const string JobCycleOperation = "Paqueteria.JobCycle";

    /// <summary>Operation name of an outbox dispatch cycle activity; the display name is the fixed lane name.</summary>
    public const string OutboxDispatchCycleOperation = "Paqueteria.OutboxDispatchCycle";

    /// <summary>
    /// Operation name of one iteration of a polling loop that is not an ADR-034 scheduled job (proof validation); the
    /// display name is the fixed loop name. OBS-003 PR 6 (Q-PR6-1): polls are never exported, scheduled job cycles are.
    /// </summary>
    public const string PollCycleOperation = "Paqueteria.PollCycle";

    /// <summary>Operation name of the fallback request activity, used only if hosting started none.</summary>
    public const string RequestOperation = "Paqueteria.HttpRequest";

    /// <summary>The ASP.NET Core hosting source that starts the activity of every inbound request.</summary>
    internal const string HostingActivitySourceName = "Microsoft.AspNetCore";

    private static readonly object Gate = new();
    private static ActivityListener? _listener;

    public static ActivitySource Source { get; } = new(ActivitySourceName);

    /// <summary>
    /// Process-wide and idempotent. Call it first in <c>Program.cs</c>, before anything creates an HTTP handler:
    /// <c>SocketsHttpHandler</c> captures <see cref="DistributedContextPropagator.Current"/> when it is built.
    /// </summary>
    public static void ConfigureProcess()
    {
        DistributedContextPropagator.Current = NoTraceContextPropagator.Instance;
        lock (Gate)
        {
            if (_listener is not null)
            {
                return;
            }

            _listener = new ActivityListener
            {
                ShouldListenTo = static source =>
                    string.Equals(source.Name, ActivitySourceName, StringComparison.Ordinal) ||
                    string.Equals(source.Name, HostingActivitySourceName, StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.PropagationData,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.PropagationData,
            };
            ActivitySource.AddActivityListener(_listener);
        }
    }

    /// <summary>Starts the new root trace of one ADR-034 scheduled job cycle; dispose it when the cycle ends.</summary>
    public static Activity StartJobCycle(string job) =>
        StartRootActivity(JobCycleOperation, job);

    /// <summary>Starts the new root trace of one polling loop iteration; dispose it when the iteration ends.</summary>
    public static Activity StartPollCycle(string loop) =>
        StartRootActivity(PollCycleOperation, loop);

    /// <summary>Starts the new root trace of one outbox dispatch cycle; dispose it when the cycle ends.</summary>
    public static Activity StartOutboxDispatchCycle(string lane) =>
        StartRootActivity(OutboxDispatchCycleOperation, lane);

    /// <summary>
    /// Starts a new root W3C activity in the calling flow, never a child of whatever activity flowed in. Meant for
    /// background loops: the ambient activity of the caller is cleared, and stopping the returned activity leaves
    /// none. Without a listener on <see cref="ActivitySourceName"/> it still returns a started W3C activity.
    /// </summary>
    public static Activity StartRootActivity(string operationName, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        Activity.Current = null;
        var activity = Source.StartActivity(operationName, ActivityKind.Internal)
            ?? new Activity(operationName).SetIdFormat(ActivityIdFormat.W3C).Start();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            activity.DisplayName = displayName;
        }

        return activity;
    }

    /// <summary>The 32-hex trace id of the current activity, or null outside any W3C activity.</summary>
    public static string? CurrentTraceId() => TraceIdOf(Activity.Current);

    /// <summary>The 32-hex trace id of <paramref name="activity"/>, or null when it is absent or not W3C.</summary>
    public static string? TraceIdOf(Activity? activity) =>
        activity is { IdFormat: ActivityIdFormat.W3C } ? activity.TraceId.ToHexString() : null;

    /// <summary>True for a W3C trace id as this code writes it: 32 lowercase hex digits, not all zero.</summary>
    public static bool IsTraceId(string? value)
    {
        if (value is not { Length: 32 })
        {
            return false;
        }

        var nonZero = false;
        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }

            nonZero |= character != '0';
        }

        return nonZero;
    }
}

/// <summary>
/// A propagator that neither extracts nor injects anything: inbound <c>traceparent</c>, <c>tracestate</c>,
/// <c>baggage</c>, <c>Request-Id</c> and <c>Correlation-Context</c> are ignored, and outgoing requests get none.
/// Registered as <see cref="DistributedContextPropagator.Current"/> (HTTP client handlers) and in DI (ASP.NET Core
/// hosting).
/// </summary>
internal sealed class NoTraceContextPropagator : DistributedContextPropagator
{
    internal static readonly NoTraceContextPropagator Instance = new();

    private NoTraceContextPropagator()
    {
    }

    public override IReadOnlyCollection<string> Fields { get; } = [];

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
    }

    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter) => null;
}
