using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// What one host exports when <see cref="TelemetryExportSettings.Enabled"/> is on: its Application Insights role name,
/// the activity sources its tracer listens to and the meters its meter provider reads.
/// </summary>
public sealed record TelemetryExportProfile(string ServiceName, IReadOnlyList<string> TraceSources, IReadOnlyList<string> Meters)
{
    /// <summary>
    /// The API: its requests (hosting), their outgoing HTTP calls, and the application source (the API runs no ADR-034
    /// job; its two realtime outbox lanes are dispatch cycles, never exported).
    /// </summary>
    public static TelemetryExportProfile Api { get; } = new(
        "paqueteria-api",
        [TelemetryExportPolicy.HostingActivitySourceName, TelemetryExportPolicy.OutgoingHttpActivitySourceName, TraceCorrelation.ActivitySourceName],
        TelemetryExportPolicy.ExportedMeters);

    /// <summary>
    /// The Worker: its ADR-034 scheduled job cycles and their outgoing HTTP calls. Its only inbound requests are the
    /// platform probes on <c>/health/*</c>, so it does not listen to the hosting source.
    /// </summary>
    public static TelemetryExportProfile Worker { get; } = new(
        "paqueteria-worker",
        [TelemetryExportPolicy.OutgoingHttpActivitySourceName, TraceCorrelation.ActivitySourceName],
        TelemetryExportPolicy.ExportedMeters);
}

/// <summary>
/// OBS-003 phase 4 PR 6: which traces and metric tags may reach Application Insights, and the only code that writes
/// attributes on an activity. Everything here works on <see cref="Activity"/> alone; the hosts wire it into the
/// OpenTelemetry SDK.
/// </summary>
/// <remarks>
/// <para>
/// Traces (owner decision Q-PR6-1, "Solo tareas programadas (Recommended)"): a trace is exported only when its root is
/// an API request that is not a <c>/health/*</c> probe, or an ADR-034 scheduled job cycle
/// (<see cref="TraceCorrelation.JobCycleOperation"/>), and only when the root is sampled (10 %). Outbox dispatch
/// cycles, polling loops (proof validation), activities without a parent and contexts that came from outside are never
/// exported. Children follow their parent's decision.
/// </para>
/// <para>
/// Span attributes are an allowlist per source, checked again when the span ends (<see cref="PrepareForExport"/>):
/// everything else is removed, and values that do not have the expected shape are removed too. A span that carries an
/// event, a link or baggage is not exported at all, because an <see cref="Activity"/> cannot drop them: an exception
/// event can carry a message with personal data. ASP.NET Core hosting writes no attribute on its request activity
/// (.NET 10), so <see cref="EnrichRequest"/> writes the method, the route template and the status code. The URL path
/// and query, headers, cookies, bodies, the client address and the user agent are never written.
/// </para>
/// <para>
/// Metrics: every exported instrument keeps only the tag keys in <see cref="MetricTagKeys"/> (a view). Nothing there
/// is an identifier, an address, a partition key, the organization pseudonym or the actor role kind.
/// </para>
/// </remarks>
public static partial class TelemetryExportPolicy
{
    /// <summary>OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12: "muestreo de 10 %". Fixed in code, not configurable.</summary>
    public const double TraceSamplingRatio = 0.10;

    /// <summary>The ASP.NET Core hosting source (one activity per inbound request).</summary>
    public const string HostingActivitySourceName = TraceCorrelation.HostingActivitySourceName;

    /// <summary>The operation name of the hosting request activity.</summary>
    public const string HostingRequestOperation = "Microsoft.AspNetCore.Hosting.HttpRequestIn";

    /// <summary>The built-in outgoing HTTP source (System.Net.Http) (one activity per outgoing request).</summary>
    public const string OutgoingHttpActivitySourceName = "System.Net.Http";

    /// <summary>OBS-003-TENANT-PSEUDONYM-2026-10-12: the organization pseudonym, on request spans with an organization context.</summary>
    public const string OrganizationPseudonymAttribute = "paqueteria.organization.pseudonym";

    /// <summary>OBS-003-TENANT-PSEUDONYM-2026-10-12: the actor's role kind, on request spans with an organization context.</summary>
    public const string ActorRoleKindAttribute = "paqueteria.actor.role_kind";

    /// <summary>Request paths that are platform probe traffic (OBS-002 excludes them too); their traces are never exported.</summary>
    public const string ProbePathPrefix = "/health";

    /// <summary>The application meters (both prefixes in use) and every built-in ASP.NET Core meter.</summary>
    public static IReadOnlyList<string> ExportedMeters { get; } = ["Paquetenvia.*", "Paqueteria.*", "Microsoft.AspNetCore.*"];

    /// <summary>
    /// The metric tag keys an exported metric keeps (the OBS-002 metric allowlist, <c>ObservabilityArchitectureTests</c>):
    /// the tags of the application meters, then the ones of the ASP.NET Core meters. Any other tag is dropped, for
    /// example Kestrel's <c>server.address</c> and <c>server.port</c>.
    /// </summary>
    public static IReadOnlySet<string> MetricTagKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Application meters (fixed lane, job, outcome, provider, hub and policy names, error classes and codes).
        "lane",
        "mode",
        "outcome",
        "error_class",
        "job",
        "audience",
        "event_type",
        "event.type",
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
        // ASP.NET Core meters: hosting, routing, diagnostics, rate limiting, SignalR connections, authentication,
        // authorization, Kestrel and the memory pool.
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

    /// <summary>Attributes a request span may carry when exported.</summary>
    public static IReadOnlySet<string> RequestSpanAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.route",
        "http.response.status_code",
        OrganizationPseudonymAttribute,
        ActorRoleKindAttribute,
    };

    /// <summary>
    /// Attributes an outgoing HTTP span may carry when exported: never <c>url.full</c> (its path can hold an account, a
    /// phone number id or an address).
    /// </summary>
    public static IReadOnlySet<string> OutgoingHttpSpanAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.response.status_code",
        "server.address",
        "server.port",
        "error.type",
    };

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "CONNECT", "TRACE",
    };

    private static readonly IReadOnlySet<string> NoAttributes = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The roots that may be sampled: an inbound request or an ADR-034 scheduled job cycle.</summary>
    public static bool IsExportedRoot(string operationName, ActivityKind kind) =>
        (kind == ActivityKind.Server && string.Equals(operationName, HostingRequestOperation, StringComparison.Ordinal)) ||
        (kind == ActivityKind.Internal && string.Equals(operationName, TraceCorrelation.JobCycleOperation, StringComparison.Ordinal));

    /// <summary>
    /// Takes <paramref name="activity"/> and the children it starts afterwards out of the export (a probe request).
    /// </summary>
    public static void ExcludeFromExport(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        activity.IsAllDataRequested = false;
    }

    /// <summary>
    /// Writes the request attributes that ASP.NET Core hosting does not write itself. Only called for a sampled request
    /// activity; the values are checked again in <see cref="PrepareForExport"/>.
    /// </summary>
    public static void EnrichRequest(Activity activity, string method, string? routeTemplate, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(activity);
        activity.SetTag("http.request.method", NormalizeMethod(method));
        if (IsRouteTemplate(routeTemplate))
        {
            activity.SetTag("http.route", routeTemplate);
        }

        activity.SetTag("http.response.status_code", statusCode);
    }

    /// <summary>
    /// OBS-003-TENANT-PSEUDONYM-2026-10-12: the organization pseudonym and the actor role kind on a sampled request
    /// span that has an organization context. Never the organization id.
    /// </summary>
    public static void SetOrganization(Activity activity, string pseudonym, string roleKind)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (OrganizationPseudonym.IsPseudonym(pseudonym) && IsRoleKind(roleKind))
        {
            activity.SetTag(OrganizationPseudonymAttribute, pseudonym);
            activity.SetTag(ActorRoleKindAttribute, roleKind);
        }
    }

    /// <summary>
    /// Runs when a sampled span ends, before any exporter sees it. Removes every attribute outside the allowlist of its
    /// source (and any allowlisted attribute whose value has an unexpected shape), clears the status description and
    /// rebuilds the span name from allowed values. Returns false, and takes the span out of the export, for a span of a
    /// source that is not exported or one that carries events, links or baggage.
    /// </summary>
    public static bool PrepareForExport(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (!activity.Recorded)
        {
            return false;
        }

        var allowed = AllowedAttributes(activity);
        if (allowed is null || activity.Events.Any() || activity.Links.Any() || activity.Baggage.Any())
        {
            ExcludeFromExport(activity);
            return false;
        }

        foreach (var tag in activity.TagObjects.ToArray())
        {
            if (!allowed.Contains(tag.Key) || !IsAllowedValue(tag.Key, tag.Value))
            {
                activity.SetTag(tag.Key, null);
            }
        }

        activity.SetStatus(activity.Status);
        activity.DisplayName = SpanName(activity);
        return true;
    }

    private static IReadOnlySet<string>? AllowedAttributes(Activity activity) => activity.Source.Name switch
    {
        HostingActivitySourceName when activity.Kind == ActivityKind.Server &&
                                       string.Equals(activity.OperationName, HostingRequestOperation, StringComparison.Ordinal) =>
            RequestSpanAttributes,
        OutgoingHttpActivitySourceName when activity.Kind == ActivityKind.Client => OutgoingHttpSpanAttributes,
        TraceCorrelation.ActivitySourceName when string.Equals(activity.OperationName, TraceCorrelation.JobCycleOperation, StringComparison.Ordinal) =>
            NoAttributes,
        _ => null,
    };

    private static string SpanName(Activity activity)
    {
        var method = activity.GetTagItem("http.request.method") as string;
        return activity.Source.Name switch
        {
            HostingActivitySourceName => (method ?? "HTTP") + (activity.GetTagItem("http.route") is string route ? " " + route : string.Empty),
            OutgoingHttpActivitySourceName => method ?? "HTTP",
            _ => JobNamePattern().IsMatch(activity.DisplayName) ? activity.DisplayName : activity.OperationName,
        };
    }

    private static bool IsAllowedValue(string key, object? value) => key switch
    {
        "http.request.method" => value is string method && (Methods.Contains(method) || method == "_OTHER"),
        "http.route" => value is string route && IsRouteTemplate(route),
        "http.response.status_code" => value is int status && status is >= 100 and <= 599,
        "server.address" => value is string address && HostPattern().IsMatch(address),
        "server.port" => value is int port && port is >= 1 and <= 65535,
        "error.type" => value is string error && ErrorTypePattern().IsMatch(error),
        OrganizationPseudonymAttribute => value is string pseudonym && OrganizationPseudonym.IsPseudonym(pseudonym),
        ActorRoleKindAttribute => value is string role && IsRoleKind(role),
        _ => false,
    };

    private static string NormalizeMethod(string method) => Methods.Contains(method) ? method : "_OTHER";

    private static bool IsRouteTemplate(string? route) => route is not null && RoutePattern().IsMatch(route);

    private static bool IsRoleKind(string? value) => value is not null && RoleKindPattern().IsMatch(value);

    [GeneratedRegex(@"^/[A-Za-z0-9/{}\-_.:*?=]{0,255}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoutePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.:\-\[\]]{1,253}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorTypePattern();

    [GeneratedRegex(@"^[A-Z_]{1,32}(?:,[A-Z_]{1,32}){0,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleKindPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex JobNamePattern();
}
