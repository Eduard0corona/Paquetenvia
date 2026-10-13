using Microsoft.Extensions.Logging;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-002: the only structured-log events the pilot alerts and workbook query (and the SEC-004
/// rate-limit summary, which none of them reads yet), and the only dimensions they may carry. Every
/// value is a fixed lane, job, outcome or rate-limit policy name, a status class or a count; no
/// identifier, payload, token, tenant or personal data ever becomes a dimension.
/// </summary>
/// <remarks>
/// The alert rules in <c>deploy/azure/pilot/observability.bicep</c> filter on these event ids and
/// property names, and <c>ObservabilityArchitectureTests</c> pins both sides to this allowlist.
/// </remarks>
public static class TelemetryEvents
{
    /// <summary>One per outbox lane and reporting window, also when idle (a heartbeat).</summary>
    public static readonly EventId OutboxLaneSummary = new(4601, "OutboxLaneSummary");

    /// <summary>One per scheduled Worker job cycle (ADR-034 scheduler).</summary>
    public static readonly EventId ScheduledJobCycle = new(4602, "ScheduledJobCycle");

    /// <summary>One per reporting window with at least one API response.</summary>
    public static readonly EventId HttpStatusSummary = new(4603, "HttpStatusSummary");

    /// <summary>
    /// SEC-004 (owner answer Q8): one per rate-limit policy and reporting window with at least one rejection. No alert
    /// or workbook reads it until a later owner decision asks for one.
    /// </summary>
    public static readonly EventId RateLimitSummary = new(4604, "RateLimitSummary");
}

public static class TelemetryDimensions
{
    /// <summary>Structured-log property names allowed on the OBS-002 events.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "Lane",
        "Claimed",
        "Processed",
        "Retry",
        "Dead",
        "LoopFailures",
        "MaxClaimAgeMs",
        "WindowSeconds",
        "Job",
        "Outcome",
        "DurationMs",
        "Total",
        "Status2xx",
        "Status3xx",
        "Status4xx",
        "Status5xx",
        // SEC-004 (owner answer Q8): a rate-limit registry policy name (never a user, address or partition key) and
        // its rejection count.
        "Policy",
        "Rejected",
    };
}

/// <summary>Fixed outbox lane names (low cardinality). Unknown names are rejected.</summary>
public static class OutboxLanes
{
    public const string Notifications = "notifications";
    public const string Dispatch = "dispatch";
    public const string RealtimeBusiness = "realtime_business";
    public const string RealtimeLocation = "realtime_location";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Notifications,
        Dispatch,
        RealtimeBusiness,
        RealtimeLocation,
    };
}

/// <summary>How a claimed outbox message left the lane (the AI-06 settle status).</summary>
public enum OutboxSettlement
{
    Processed,
    Retry,
    Dead,
}

/// <summary>Fixed scheduled-job outcomes.</summary>
public static class ScheduledJobOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
}
