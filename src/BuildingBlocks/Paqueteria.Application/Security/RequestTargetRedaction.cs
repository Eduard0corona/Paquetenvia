namespace Paqueteria.Application.Security;

/// <summary>
/// Removes a secret carried in a request target (path or query) before ASP.NET Core hosting diagnostics read it.
/// </summary>
/// <remarks>
/// Hosting captures the path and query in the RequestPath log scope, the "Request starting/finished" messages and
/// the request activity before any middleware runs, so a secret in the URL can only be kept out of logs and
/// telemetry by rewriting the target when the HttpContext is created. The host composes every registered redactor
/// in one IHttpContextFactory; each module keeps the original value in its own request feature.
/// </remarks>
public interface IRequestTargetRedactor
{
    /// <summary>
    /// Returns the redacted target and the feature holding the original secret, or null when
    /// <paramref name="path"/> is not one this redactor owns.
    /// </summary>
    /// <param name="path">The decoded request path, as hosting would expose it.</param>
    /// <param name="queryString">The raw query string, including the leading '?', or empty.</param>
    RequestTargetRedaction? Redact(string path, string queryString);
}

/// <summary>A redacted request target and the request feature that keeps the original secret.</summary>
/// <param name="Path">The decoded path hosting, routing and logging will see.</param>
/// <param name="QueryString">The raw query string (with '?', or empty) they will see.</param>
/// <param name="FeatureType">The request feature type the original secret is stored under.</param>
/// <param name="Feature">The feature instance; it must not expose the secret through ToString.</param>
public sealed record RequestTargetRedaction(
    string Path,
    string QueryString,
    Type FeatureType,
    object Feature);
