using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Paqueteria.Application.Security;
using Paqueteria.Infrastructure.Observability;

namespace Paqueteria.Infrastructure.Security.RateLimiting;

/// <summary>
/// SEC-004: the one writer of every rate-limiter rejection. It counts the rejection by policy name only, runs the
/// policy's own counter, sets <c>Retry-After</c> in whole seconds and <c>Cache-Control: no-store</c>, and writes
/// AI-05 <c>TooManyRequests</c>: the problem with the request's <c>traceId</c>, or, on a public tracking surface, the
/// uniform body without it, or, on a browser navigation that accepts HTML (<see cref="RateLimitRejectionPage"/>,
/// <c>GET /auth/login</c>), its simple page. The partition key, the subject, its hash and the client address are never
/// read here, logged or tagged.
/// </summary>
public static class RateLimitRejectionWriter
{
    /// <summary>The public tracking 429, byte for byte as before SEC-004 (#244).</summary>
    private static readonly byte[] PublicTrackingBody =
        """{"type":"about:blank","title":"Too Many Requests","status":429}"""u8.ToArray();

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = context.HttpContext;
        var services = httpContext.RequestServices;
        var endpoint = httpContext.GetEndpoint();
        var registry = services.GetRequiredService<RateLimitPolicyRegistry>();
        var policy = registry.For(endpoint)
            ?? throw new InvalidOperationException(
                "A rate-limit rejection came from a policy outside the registry; every limited endpoint must name a registered policy.");
        // SEC-004 PR 3: a shared window or an in-flight rule rejects under its own registered name.
        if (context.Lease.TryGetMetadata(RateLimitLeaseMetadata.RejectedBy, out var rejectedBy))
        {
            policy = registry.Find(rejectedBy)
                ?? throw new InvalidOperationException("A rate-limit rejection named a policy outside the registry.");
        }

        services.GetRequiredService<RateLimitTelemetry>().Rejected(policy.Name);
        policy.CountRejection?.Invoke(services);

        var response = httpContext.Response;
        if (response.HasStarted)
        {
            // Nothing can be written once the response started; the rejection is still counted.
            return;
        }

        response.StatusCode = TooManyRequestsProblem.Status;
        // The fixed window limiter reports its full window as the retry hint: a constant per policy, never too early,
        // and nothing about other callers or a token.
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? wait
            : policy.Limit(services).Window;
        response.Headers.RetryAfter =
            TooManyRequestsProblem.RetryAfterSeconds(retryAfter).ToString(CultureInfo.InvariantCulture);
        // Like the realtime gate's problems. On public tracking its own privacy headers (no-store, private) are set
        // when the response starts and win, unchanged.
        response.Headers.CacheControl = "no-store";

        if (endpoint?.Metadata.GetMetadata<PublicTrackingRateLimitRejection>() is not null)
        {
            response.ContentType = TooManyRequestsProblem.ContentType;
            await response.Body.WriteAsync(PublicTrackingBody, cancellationToken);
            return;
        }

        // SEC-004 PR 3 (owner answer Q6): a browser navigation sees a simple page; any other client, the problem.
        if (endpoint?.Metadata.GetMetadata<RateLimitRejectionPage>() is { } page &&
            AcceptsHtml(httpContext.Request))
        {
            response.ContentType = RateLimitRejectionPage.ContentType;
            response.Headers.ContentSecurityPolicy = RateLimitRejectionPage.ContentSecurityPolicy;
            response.Headers.XContentTypeOptions = "nosniff";
            await response.Body.WriteAsync(page.Body, cancellationToken);
            return;
        }

        // Through IProblemDetailsService, so OBS-003 adds traceId (also when the request's Accept excludes JSON).
        await Results.Problem(
                statusCode: TooManyRequestsProblem.Status,
                title: TooManyRequestsProblem.Title,
                type: TooManyRequestsProblem.Type)
            .ExecuteAsync(httpContext);
    }

    /// <summary>
    /// Whether the request names <c>text/html</c> in <c>Accept</c> with a quality above zero, as a browser navigation
    /// does. A wildcard alone (<c>*/*</c>, as <c>fetch</c> and command-line clients send) is not enough.
    /// </summary>
    internal static bool AcceptsHtml(HttpRequest request) =>
        MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accepted) &&
        accepted.Any(static media =>
            media.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
            media.Quality is not <= 0);
}

/// <summary>
/// Rate-limit rejections, counted by policy name only (<see cref="PolicyTag"/>, a fixed name from the registry):
/// the <c>rate_limit.rejected</c> counter, and the per-minute OBS-002 summary
/// (<see cref="RateLimitRejectionSummary"/>, owner answer Q8), which accepts registry names only. No partition key,
/// subject, address, tenant or path ever becomes a tag or a dimension. No alert or workbook reads either.
/// </summary>
public sealed class RateLimitTelemetry
{
    public const string MeterName = "Paquetenvia.RateLimiting";
    public const string RejectedInstrument = "rate_limit.rejected";
    public const string PolicyTag = "policy";

    private readonly Counter<long> _rejected;
    private readonly RateLimitRejectionSummary _summary;

    public RateLimitTelemetry(IMeterFactory meterFactory, RateLimitRejectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(summary);
        _rejected = meterFactory.Create(MeterName).CreateCounter<long>(
            RejectedInstrument,
            unit: "{request}",
            description: "Requests rejected by an API rate-limit policy.");
        _summary = summary;
    }

    public void Rejected(string policy)
    {
        _summary.Record(policy);
        _rejected.Add(1, new KeyValuePair<string, object?>(PolicyTag, policy));
    }
}
