using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using Paqueteria.Application.Security;

namespace Realtime.Endpoints.Connection;

/// <summary>
/// Keeps hub <c>access_token</c> query values (the public tracking token on <c>/hubs/tracking</c>, private access
/// tokens on the other hubs) out of every log line, scope and activity derived from the request target.
/// </summary>
/// <remarks>
/// Browsers cannot set headers on WebSockets, so SignalR carries the token in the query. Hosting logs the query in
/// "Request starting/finished" before any middleware runs; the host's request target redacting factory applies this
/// redactor first. For every <c>/hubs</c> request (negotiate, connect, WebSocket, long polling; case-insensitive
/// like routing) each <c>access_token</c> value becomes <c>redacted</c>, the rest of the query is kept byte for
/// byte, and the original values are kept in <see cref="IRealtimeAccessTokenFeature"/>.
/// </remarks>
public static class RealtimeAccessTokenRedaction
{
    public const string HubsPathPrefix = "/hubs";
    public const string AccessTokenParameter = "access_token";
    public const string RedactedValue = "redacted";

    /// <summary>Registers the hub access token redactor applied by the host's request target redacting factory.</summary>
    public static IServiceCollection AddRealtimeAccessTokenRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRequestTargetRedactor, RealtimeAccessTokenRedactor>());
        return services;
    }

    public static bool IsHubPath(PathString path) =>
        path.StartsWithSegments(HubsPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the query with every <c>access_token</c> value replaced, and the original values exactly as
    /// <c>HttpRequest.Query["access_token"]</c> would have returned them.
    /// </summary>
    public static string RedactQuery(string queryString, out StringValues accessTokens)
    {
        ArgumentNullException.ThrowIfNull(queryString);
        accessTokens = QueryHelpers.ParseNullableQuery(queryString) is { } parsed &&
            parsed.TryGetValue(AccessTokenParameter, out var values)
                ? values
                : StringValues.Empty;
        if (accessTokens.Count == 0)
        {
            return queryString;
        }

        var body = queryString.StartsWith('?') ? queryString[1..] : queryString;
        var builder = new StringBuilder(queryString.Length);
        builder.Append('?');
        var first = true;
        foreach (var pair in body.Split('&'))
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var rawKey = separator < 0 ? pair : pair[..separator];
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            if (string.Equals(key, AccessTokenParameter, StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(rawKey).Append('=').Append(RedactedValue);
            }
            else
            {
                builder.Append(pair);
            }
        }

        return builder.ToString();
    }
}

/// <summary>The original <c>access_token</c> query values of a hub request, before redaction.</summary>
public interface IRealtimeAccessTokenFeature
{
    /// <summary>The values <c>HttpRequest.Query["access_token"]</c> held before redaction (possibly none).</summary>
    StringValues AccessTokens { get; }
}

internal sealed class RealtimeAccessTokenFeature(StringValues accessTokens) : IRealtimeAccessTokenFeature
{
    public StringValues AccessTokens { get; } = accessTokens;

    public override string ToString() => nameof(RealtimeAccessTokenFeature);
}

internal sealed class RealtimeAccessTokenRedactor : IRequestTargetRedactor
{
    public RequestTargetRedaction? Redact(string path, string queryString)
    {
        if (!RealtimeAccessTokenRedaction.IsHubPath(new PathString(path)))
        {
            return null;
        }

        // Always set the feature on hub requests: its absence means the redaction is not wired.
        var redacted = RealtimeAccessTokenRedaction.RedactQuery(queryString, out var accessTokens);
        return new RequestTargetRedaction(
            path,
            redacted,
            typeof(IRealtimeAccessTokenFeature),
            new RealtimeAccessTokenFeature(accessTokens));
    }
}

internal static class RealtimeAccessTokenFeatureExtensions
{
    /// <summary>Reads the original access_token values; fails closed when the redaction is not wired.</summary>
    internal static StringValues GetOriginalAccessTokens(this HttpContext context) =>
        context.Features.Get<IRealtimeAccessTokenFeature>()?.AccessTokens
        ?? throw new InvalidOperationException(
            "Realtime access token redaction is not registered; refusing the hub request.");
}
