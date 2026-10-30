using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paqueteria.Application.Security;

namespace Notifications.Endpoints;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 7; AI-24 <c>recipient_notices.webhooks.unsubscribe</c>: the token
/// is "never stored or logged"): two secrets travel in request targets - the unsubscribe token in the path of
/// <c>unsubscribeRecipientNotices</c> and <c>hub.verify_token</c> in the query of <c>verifyMetaWhatsAppWebhook</c>.
/// Hosting reads the target before any middleware, so the host's request target redacting factory applies these
/// redactors: the token segment becomes <c>redacted</c> (same segment count, same routing) and the verification query is
/// removed; each original value stays only in a request feature its endpoint reads.
/// </summary>
public static class RecipientNoticeRequestTargetRedaction
{
    public const string RedactedSegment = "redacted";

    public static IServiceCollection AddRecipientNoticeEndpoints(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestTargetRedactor, RecipientNoticeUnsubscribePathRedactor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestTargetRedactor, RecipientNoticeVerificationQueryRedactor>());
        return services;
    }

    /// <summary>
    /// The redacted form of an unsubscribe path (case-insensitive, like routing) and the original token when there is
    /// exactly one segment after the prefix.
    /// </summary>
    public static bool TryRedactUnsubscribePath(PathString path, out PathString redactedPath, out string? originalToken)
    {
        redactedPath = path;
        originalToken = null;
        if (!path.StartsWithSegments(
                RecipientNoticeEndpointDefaults.UnsubscribePathPrefix,
                StringComparison.OrdinalIgnoreCase,
                out var matchedPrefix,
                out var remaining) ||
            !remaining.HasValue)
        {
            return false;
        }

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

/// <summary>The original unsubscribe token of a redacted request.</summary>
public interface IRecipientNoticeUnsubscribeTokenFeature
{
    /// <summary>The token segment as routing would have bound it, or null when the path had more segments.</summary>
    string? Token { get; }
}

/// <summary>The original query of a redacted Meta verification request.</summary>
public interface IRecipientNoticeVerificationQueryFeature
{
    /// <summary>The raw query string, with its leading '?', or empty.</summary>
    string QueryString { get; }
}

internal sealed class RecipientNoticeUnsubscribeTokenFeature(string? token) : IRecipientNoticeUnsubscribeTokenFeature
{
    public string? Token { get; } = token;

    public override string ToString() => nameof(RecipientNoticeUnsubscribeTokenFeature);
}

internal sealed class RecipientNoticeVerificationQueryFeature(string queryString) : IRecipientNoticeVerificationQueryFeature
{
    public string QueryString { get; } = queryString;

    public override string ToString() => nameof(RecipientNoticeVerificationQueryFeature);
}

internal sealed class RecipientNoticeUnsubscribePathRedactor : IRequestTargetRedactor
{
    public RequestTargetRedaction? Redact(string path, string queryString) =>
        RecipientNoticeRequestTargetRedaction.TryRedactUnsubscribePath(new PathString(path), out var redacted, out var token)
            ? new RequestTargetRedaction(
                redacted.Value!,
                queryString,
                typeof(IRecipientNoticeUnsubscribeTokenFeature),
                new RecipientNoticeUnsubscribeTokenFeature(token))
            : null;
}

internal sealed class RecipientNoticeVerificationQueryRedactor : IRequestTargetRedactor
{
    public RequestTargetRedaction? Redact(string path, string queryString) =>
        string.Equals(
            path.TrimEnd('/'),
            RecipientNoticeEndpointDefaults.WhatsAppWebhookPath,
            StringComparison.OrdinalIgnoreCase)
            ? new RequestTargetRedaction(
                path,
                string.Empty,
                typeof(IRecipientNoticeVerificationQueryFeature),
                new RecipientNoticeVerificationQueryFeature(queryString))
            : null;
}
