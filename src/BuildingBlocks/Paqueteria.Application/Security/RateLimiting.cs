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
/// SEC-004 PR 3 (owner answer Q6, "Mensaje común con espera"): endpoint metadata of a browser navigation
/// (<c>GET /auth/login</c>). When the rejected request accepts <c>text/html</c>, its 429 is this simple page instead
/// of the problem; any other request still gets AI-05 <c>TooManyRequests</c>. The page is the message in one
/// paragraph, with the message's first sentence as its title, and nothing else: no script, style, image, link, form
/// or branding. The response also carries <c>Retry-After</c> and <c>Cache-Control: no-store</c> like every 429.
/// </summary>
public sealed class RateLimitRejectionPage
{
    public const string ContentType = "text/html; charset=utf-8";

    /// <summary>Nothing may load, run, frame or submit from the page.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public RateLimitRejectionPage(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Message = message;
        var sentence = message.IndexOf(". ", StringComparison.Ordinal) is var end and > 0 ? message[..(end + 1)] : message;
        Body = Encoding.UTF8.GetBytes(
            "<!doctype html>\n" +
            "<html lang=\"es-MX\">\n" +
            "<head>\n" +
            "<meta charset=\"utf-8\">\n" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
            "<meta name=\"robots\" content=\"noindex\">\n" +
            $"<title>{Escape(sentence)}</title>\n" +
            "</head>\n" +
            "<body>\n" +
            $"<p>{Escape(message)}</p>\n" +
            "</body>\n" +
            "</html>\n");
    }

    /// <summary>The text the page shows, verbatim.</summary>
    public string Message { get; }

    /// <summary>The whole page, UTF-8.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Escapes the markup characters only; accented letters stay as they are in a UTF-8 page.</summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
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
