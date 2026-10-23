using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Paqueteria.Application.Messaging;

namespace Paqueteria.Infrastructure.Messaging;

/// <summary>
/// Shared transport rules of the messaging adapters: one bounded attempt, no internal retry, and a
/// classification that never reads or logs a provider message body.
/// </summary>
internal static partial class MessagingHttp
{
    /// <summary>Upper bound applied to a provider <c>Retry-After</c> hint.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(1);

    public static bool IsServerOrThrottle(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||
        status == HttpStatusCode.TooManyRequests ||
        (int)status >= 500;

    /// <summary>Classification by HTTP status alone, used when the provider body gives no code.</summary>
    public static MessagingResult ClassifyStatus(HttpStatusCode status, HttpResponseHeaders headers, TimeProvider time) =>
        status switch
        {
            HttpStatusCode.TooManyRequests =>
                new(MessagingOutcome.TransientFailure, MessagingResultCodes.RateLimited, RetryAfter: ReadRetryAfter(headers, time)),
            HttpStatusCode.RequestTimeout =>
                new(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new(MessagingOutcome.PermanentFailure, MessagingResultCodes.AuthenticationFailed),
            // A gateway timeout may hide an accepted send; 500 from Meta/ACS is "unknown error".
            HttpStatusCode.GatewayTimeout =>
                new(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout),
            _ when (int)status >= 500 =>
                new(MessagingOutcome.TransientFailure, MessagingResultCodes.Unavailable, RetryAfter: ReadRetryAfter(headers, time)),
            _ => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.Rejected),
        };

    /// <summary>
    /// Sends once with a per-attempt timeout. A timeout or a broken exchange after the request left is
    /// ambiguous (the provider may have accepted it); a failure to connect is transient (nothing was sent).
    /// Caller cancellation propagates.
    /// </summary>
    public static async Task<(HttpResponseMessage? Response, MessagingResult? Failure)> SendOnceAsync(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, attempt.Token)
                .ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout));
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is
            HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            return (null, new MessagingResult(MessagingOutcome.TransientFailure, MessagingResultCodes.Unreachable));
        }
        catch (HttpRequestException)
        {
            return (null, new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.Timeout));
        }
    }

    public static TimeSpan? ReadRetryAfter(HttpResponseHeaders headers, TimeProvider time)
    {
        var retryAfter = headers.RetryAfter;
        TimeSpan? delay = retryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - time.GetUtcNow(),
            _ => null,
        };

        // ACS also sends retry-after-ms / x-ms-retry-after-ms.
        if (delay is null)
        {
            foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms" })
            {
                if (headers.TryGetValues(name, out var values) &&
                    long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
                {
                    delay = TimeSpan.FromMilliseconds(milliseconds);
                    break;
                }
            }
        }

        if (delay is null || delay <= TimeSpan.Zero)
        {
            return null;
        }

        return delay > MaximumRetryAfter ? MaximumRetryAfter : delay;
    }

    /// <summary>
    /// A parameter is a single line of printable text: no control characters (WhatsApp rejects new
    /// lines and tabs in template parameters) and no more than four consecutive spaces.
    /// </summary>
    public static bool AreParametersValid(IReadOnlyList<string>? parameters, int expectedCount)
    {
        if (parameters is null || parameters.Count != expectedCount)
        {
            return false;
        }

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter) || parameter.Length > 1024 ||
                parameter.Any(char.IsControl) || parameter.Contains("     ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-005): the per-channel extras of a request. A WhatsApp request may carry
    /// a URL button suffix (Base64URL characters only, as the tracking token) and a quick reply payload (a fixed
    /// upper-case code), never an unsubscribe URL; an e-mail request may carry an absolute https one-click unsubscribe
    /// URL without spaces, angle brackets or a fragment, never a button.
    /// </summary>
    public static bool AreExtrasValid(MessagingRequest request) => request.Channel switch
    {
        MessagingChannel.WhatsApp =>
            request.OneClickUnsubscribeUrl is null &&
            (request.UrlButtonSuffix is null || UrlButtonSuffixPattern().IsMatch(request.UrlButtonSuffix)) &&
            (request.QuickReplyPayload is null || QuickReplyPayloadPattern().IsMatch(request.QuickReplyPayload)),
        MessagingChannel.Email =>
            request.UrlButtonSuffix is null &&
            request.QuickReplyPayload is null &&
            (request.OneClickUnsubscribeUrl is null || IsOneClickUnsubscribeUrl(request.OneClickUnsubscribeUrl)),
        _ => false,
    };

    private static bool IsOneClickUnsubscribeUrl(string value) =>
        value.Length <= 2048 &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '<' or '>' or '"') &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    /// <summary>E.164 without separators: optional '+', 8 to 15 digits, no leading zero.</summary>
    public static bool TryNormalizePhone(string address, out string digits)
    {
        var candidate = address.StartsWith('+') ? address[1..] : address;
        if (PhonePattern().IsMatch(candidate))
        {
            digits = candidate;
            return true;
        }

        digits = string.Empty;
        return false;
    }

    public static string Render(string template, IReadOnlyList<string> parameters) =>
        MessagingOptionsValidator.PlaceholderPattern().Replace(
            template,
            match => parameters[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1]);

    [GeneratedRegex(@"^[1-9]\d{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,512}$", RegexOptions.CultureInvariant)]
    private static partial Regex UrlButtonSuffixPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex QuickReplyPayloadPattern();
}
