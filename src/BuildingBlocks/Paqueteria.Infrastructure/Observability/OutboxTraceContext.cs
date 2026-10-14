using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003 phase 4 PR 7 (OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13, project owner: "Columna nueva (Recommended)"): the
/// W3C context of the trace that wrote an outbox row, kept in the <c>trace_parent</c> column of both platform outbox
/// tables. This class only defines, writes and parses the value; the column's parameter and reader live with the
/// outbox model (<c>Paqueteria.Infrastructure.Database.Outbox.OutboxTraceContextColumn</c>), because observability
/// code never touches the database.
/// </summary>
/// <remarks>
/// <para>
/// The value has the shape of a W3C <c>traceparent</c> of version 00 and nothing more:
/// <c>00-{32 hex trace id}-{16 hex span id of the producing activity}-{00|01}</c>, where the last field is the sampled
/// flag of that activity. It never carries <c>tracestate</c>, baggage, a tenant, a user or any request data. The
/// column is nullable: rows written before the column existed, and rows written outside any W3C activity, have none.
/// A CHECK constraint holds every stored value to this exact shape (<see cref="Pattern"/>).
/// </para>
/// <para>
/// The value is read from <see cref="Activity.Current"/> of the producer, inside the producer's own transaction, and
/// written by the producer itself with the rest of the row (<c>ValueGeneratedNever</c>, no <c>RETURNING</c>). It never
/// enters the immutable <c>payload</c>, a SignalR message or a notification.
/// </para>
/// <para>
/// A consumer starts each claimed message's activity as a child of this context
/// (<see cref="TraceCorrelation.StartOutboxMessage"/>), so the log lines of that message carry the trace id of the
/// request or job that produced it. The context is parsed as a local parent, never a remote one: it was written by
/// this platform's own producers (inbound trace context is ignored, OBS-003 PR 1), and the export sampler keeps a
/// local parent's sampled decision.
/// </para>
/// </remarks>
public static class OutboxTraceContext
{
    /// <summary>The exact shape of a stored value, as the CHECK constraint of both tables declares it.</summary>
    public const string Pattern = "^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$";

    /// <summary>The value for a row written now: the context of <see cref="Activity.Current"/>, or null outside a trace.</summary>
    public static string? Current() => Of(Activity.Current);

    /// <summary>
    /// The value for <paramref name="activity"/>: version 00, its trace id, its own span id and its sampled flag. Null
    /// when the activity is absent, not W3C, or has an all-zero trace or span id.
    /// </summary>
    public static string? Of(Activity? activity)
    {
        if (activity is not { IdFormat: ActivityIdFormat.W3C })
        {
            return null;
        }

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        if (!TraceCorrelation.IsTraceId(traceId) || !IsSpanId(spanId))
        {
            return null;
        }

        var sampled = (activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0;
        return $"00-{traceId}-{spanId}-{(sampled ? "01" : "00")}";
    }

    /// <summary>
    /// True for a value of the stored shape: version 00, a 32-hex trace id and a 16-hex span id, lowercase and not all
    /// zero, and the flags 00 or 01.
    /// </summary>
    public static bool IsTraceParent([NotNullWhen(true)] string? value) =>
        value is { Length: 55 } &&
        value.StartsWith("00-", StringComparison.Ordinal) &&
        value[35] == '-' &&
        value[52] == '-' &&
        TraceCorrelation.IsTraceId(value.Substring(3, 32)) &&
        IsSpanId(value.Substring(36, 16)) &&
        value[53] == '0' &&
        value[54] is '0' or '1';

    /// <summary>
    /// OBS-003-WORKER-AUDIT-TRACE-2026-10-14 (project owner: "Sí, en un PR aparte (Recommended)"): the trace id of a
    /// stored value, in the exact form the API writes to <c>platform.audit_logs.request_id</c> (32 lowercase hex, as
    /// <c>HttpTraceCorrelation</c> sets <c>HttpContext.TraceIdentifier</c>), or null when the value is absent or not of
    /// the stored shape. A Worker audit row written for a claimed message stores this, so it carries the trace id of the
    /// request or job that wrote the message; without a valid stored context it keeps NULL, never the trace of the
    /// Worker's own dispatch cycle, which belongs to no request.
    /// </summary>
    public static string? TraceIdOf(string? value) =>
        TryParse(value, out var context) ? context.TraceId.ToHexString() : null;

    /// <summary>
    /// Parses a stored value into a local (never remote) parent context with its sampled flag. Anything that is not of
    /// the stored shape yields false, and the consumer then runs the message under its dispatch cycle.
    /// </summary>
    public static bool TryParse(string? value, out ActivityContext context)
    {
        context = default;
        if (!IsTraceParent(value))
        {
            return false;
        }

        var traceId = ActivityTraceId.CreateFromString(value.AsSpan(3, 32));
        var spanId = ActivitySpanId.CreateFromString(value.AsSpan(36, 16));
        var flags = value[54] == '1' ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None;
        context = new ActivityContext(traceId, spanId, flags, traceState: null, isRemote: false);
        return true;
    }

    private static bool IsSpanId(string value)
    {
        if (value.Length != 16)
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
