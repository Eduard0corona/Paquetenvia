using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.Application.Security;

/// <summary>
/// SEC-004: the one 429 shape of the API, AI-05 <c>TooManyRequests</c> (<c>application/problem+json</c>, a
/// <c>Problem</c>). Every 429 is <c>{"type":"about:blank","title":"Too Many Requests","status":429}</c>; the
/// problem writer adds the request's <c>traceId</c> (OBS-003), except on the public tracking surfaces
/// (<see cref="PublicTrackingRateLimitRejection"/>), whose body stays uniform without it (Invariant 5).
/// <c>Retry-After</c> is a whole number of seconds, at least 1.
/// </summary>
public static class TooManyRequestsProblem
{
    public const int Status = 429;
    public const string Type = "about:blank";
    public const string Title = "Too Many Requests";
    public const string ContentType = "application/problem+json";

    /// <summary>The <c>Retry-After</c> value: the wait rounded up to whole seconds, never below 1.</summary>
    public static long RetryAfterSeconds(TimeSpan retryAfter) =>
        retryAfter <= TimeSpan.Zero ? 1 : Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
}

/// <summary>
/// Endpoint metadata of the public tracking surfaces (<c>publicTracking</c> and the tracking hub): their 429 is the
/// uniform body without <c>traceId</c>, like their 404 and 503 (Invariant 5, AI-05 <c>Problem.traceId</c>).
/// </summary>
public sealed class PublicTrackingRateLimitRejection
{
    private PublicTrackingRateLimitRejection()
    {
    }

    public static PublicTrackingRateLimitRejection Instance { get; } = new();
}

/// <summary>
/// Rate-limit partition of an authenticated caller: an opaque hash of its subject (the <c>sub</c> claim), so limiter
/// state never stores the subject itself. The value is never logged, traced or used as a metric tag.
/// </summary>
public static class SubjectPartition
{
    public static string Key(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"identity:{subject}")));
    }
}
