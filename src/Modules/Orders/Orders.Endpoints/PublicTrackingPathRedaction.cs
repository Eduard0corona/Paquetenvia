using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paqueteria.Application.Security;

namespace Orders.Endpoints;

/// <summary>
/// Keeps the public tracking token (a bearer-like secret carried in the URL path) out of every log line,
/// log scope and telemetry item derived from the request path.
/// </summary>
/// <remarks>
/// ASP.NET Core hosting reads <see cref="HttpRequest.Path"/> in <c>HostingApplication.CreateContext</c>, before
/// any middleware runs: it captures it eagerly in the <c>RequestPath</c> log scope, the "Request starting" and
/// "Request finished" messages and the <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c> activity (tags and
/// sampling input). Middleware is therefore too late. The host's request target redacting
/// HttpContext factory (Paqueteria.Infrastructure.Security) runs before hosting diagnostics and applies this module's
/// <see cref="IRequestTargetRedactor"/>: it rewrites the target of a lookup to a redacted path with the same segment
/// count (routing still matches exactly what it matched before) and keeps the original token in a request feature
/// that only the lookup endpoint reads. Whatever provider, formatter, exporter,
/// HTTP logging or routing diagnostics is configured, the only path they can observe is the redacted one.
/// </remarks>
public static class PublicTrackingPathRedaction
{
    /// <summary>The value every path segment after <c>/api/v1/tracking</c> is replaced with.</summary>
    public const string RedactedSegment = "redacted";

    /// <summary>
    /// Registers the tracking path redactor applied by the host's request target redacting factory.
    /// </summary>
    public static IServiceCollection AddPublicTrackingPathRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRequestTargetRedactor, PublicTrackingPathRedactor>());
        return services;
    }

    /// <summary>
    /// Returns the redacted form of <paramref name="path"/> when it carries anything after the tracking prefix
    /// (case-insensitive, like routing), together with the original token when there is exactly one segment.
    /// </summary>
    public static bool TryRedact(
        PathString path,
        out PathString redactedPath,
        out string? originalToken)
    {
        redactedPath = path;
        originalToken = null;
        if (!path.StartsWithSegments(
                PublicTrackingEndpointDefaults.PathPrefix,
                StringComparison.OrdinalIgnoreCase,
                out var matchedPrefix,
                out var remaining) ||
            !remaining.HasValue)
        {
            return false;
        }

        // remaining always starts with '/'. Every non-empty segment is replaced; empty segments are kept so the
        // redacted path has the same shape (and the same routing outcome) as the original one.
        var segments = remaining.Value!.Substring(1).Split('/');
        var redactedAny = false;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0)
            {
                continue;
            }

            segments[index] = RedactedSegment;
            redactedAny = true;
        }

        if (!redactedAny)
        {
            return false;
        }

        var inner = remaining.Value.Substring(1);
        if (inner.EndsWith('/'))
        {
            inner = inner[..^1];
        }

        originalToken = inner.Length > 0 && !inner.Contains('/') ? inner : null;
        redactedPath = matchedPrefix.Add(new PathString("/" + string.Join('/', segments)));
        return true;
    }
}

/// <summary>The original public tracking token of a redacted lookup request.</summary>
public interface IPublicTrackingTokenFeature
{
    /// <summary>The token segment as routing would have bound it, or null when the path had more segments.</summary>
    string? Token { get; }
}

internal sealed class PublicTrackingTokenFeature(string? token) : IPublicTrackingTokenFeature
{
    public string? Token { get; } = token;

    public override string ToString() => nameof(PublicTrackingTokenFeature);
}

internal sealed class PublicTrackingPathRedactor : IRequestTargetRedactor
{
    public RequestTargetRedaction? Redact(string path, string queryString) =>
        PublicTrackingPathRedaction.TryRedact(new PathString(path), out var redactedPath, out var token)
            ? new RequestTargetRedaction(
                redactedPath.Value!,
                queryString,
                typeof(IPublicTrackingTokenFeature),
                new PublicTrackingTokenFeature(token))
            : null;
}
