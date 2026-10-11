using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Voice;

namespace Paqueteria.Infrastructure.Voice;

/// <summary>
/// VOICE-001-PROVIDER-TWILIO-2026-10-11 adapter over Twilio Programmable Voice. One REST "create call"
/// (<c>POST {BaseUri}/2010-04-01/Accounts/{AccountSid}/Calls.json</c>) calls the driver's mobile from the company
/// number; the inline TwiML greets the driver (no personal data) and dials the recipient with the company number as
/// caller id, so neither party sees the other's number. Nothing is recorded. Twilio Proxy is not used (Public Beta
/// without SLA). A create-call request has no idempotency key, so the adapter never retries: an ambiguous answer is
/// reported as such and the call request ends UNCONFIRMED.
/// </summary>
internal sealed partial class TwilioVoiceBridgeProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<VoiceBridgeOptions> options,
    TimeProvider time,
    ILogger<TwilioVoiceBridgeProvider> logger) : IVoiceBridgeProvider, IDisposable
{
    public const string HttpClientName = "Paqueteria.Voice.Twilio";
    public const string ApiVersion = "2010-04-01";

    private static readonly EventId ProviderCompleted = new(4701, "VoiceProviderCompleted");

    private readonly VoiceProviderGuard _guard = new(
        time,
        options.Value.Twilio.CircuitBreakerFailureThreshold,
        TimeSpan.FromSeconds(options.Value.Twilio.CircuitBreakerBreakSeconds),
        options.Value.Twilio.MaxConcurrentRequests);

    internal VoiceProviderGuard Guard => _guard;

    public async ValueTask<VoiceBridgeResult> PlaceCallAsync(VoiceBridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var twilio = options.Value.Twilio;
        if (request.CallRequestId == Guid.Empty || request.OrganizationId == Guid.Empty ||
            request.Driver is not { IsDialable: true } || request.Recipient is not { IsDialable: true } ||
            !VoicePhoneNumber.TryParseE164(twilio.CompanyNumber, out var company) || !company.IsDialable ||
            string.Equals(request.Driver.E164, request.Recipient.E164, StringComparison.Ordinal))
        {
            return Log(new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.PhoneInvalid), null);
        }

        using var message = BuildRequest(twilio, company, request);
        var (result, status) = await _guard.RunAsync(
            token => SendAsync(message, twilio.TimeoutSeconds, token), cancellationToken).ConfigureAwait(false);
        return Log(result, status);
    }

    public void Dispose() => _guard.Dispose();

    /// <summary>The REST request, exposed to the tests: form fields, Basic credentials and the callback URL.</summary>
    internal static HttpRequestMessage BuildRequest(
        TwilioVoiceOptions twilio,
        VoicePhoneNumber company,
        VoiceBridgeRequest request)
    {
        var uri = new Uri(
            $"{twilio.BaseUri.TrimEnd('/')}/{ApiVersion}/Accounts/{Uri.EscapeDataString(twilio.AccountSid)}/Calls.json",
            UriKind.Absolute);
        var statusCallback =
            twilio.WebhookBaseUri.TrimEnd('/') +
            VoiceWebhookPaths.CallStatusPathAndQuery(request.OrganizationId, request.CallRequestId);
        var form = new List<KeyValuePair<string, string>>
        {
            new("To", request.Driver.E164),
            new("From", company.E164),
            new("Twiml", TwilioTwiml.Bridge(twilio, company, request.Recipient)),
            new("Timeout", twilio.DriverRingSeconds.ToString(CultureInfo.InvariantCulture)),
            // Greeting and ringing on top of the bridged conversation, which <Dial timeLimit> caps on its own.
            new("TimeLimit", (twilio.MaximumCallSeconds + twilio.DriverRingSeconds + 60).ToString(CultureInfo.InvariantCulture)),
            new("Record", "false"),
            new("StatusCallback", statusCallback),
            new("StatusCallbackMethod", "POST"),
        };
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(form),
        };
        var (user, secret) = string.IsNullOrEmpty(twilio.ApiKeySid)
            ? (twilio.AccountSid, twilio.AuthToken)
            : (twilio.ApiKeySid, twilio.ApiKeySecret);
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private async ValueTask<(VoiceBridgeResult Result, int? Status)> SendAsync(
        HttpRequestMessage message,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, attempt.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout), null);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is
            HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or
            HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
        {
            // Nothing left the process: no call can exist.
            return (new(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unreachable), null);
        }
        catch (HttpRequestException)
        {
            return (new(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout), null);
        }

        using (response)
        {
            string payload;
            try
            {
                payload = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException &&
                                              !cancellationToken.IsCancellationRequested)
            {
                payload = string.Empty;
            }

            return (Classify(response.StatusCode, payload), (int)response.StatusCode);
        }
    }

    /// <summary>
    /// Twilio error codes in the body take precedence over the HTTP status. The error <c>message</c> can echo a
    /// phone number, so it is never read: only the numeric <c>code</c>.
    /// </summary>
    internal static VoiceBridgeResult Classify(HttpStatusCode status, string payload)
    {
        if ((int)status is >= 200 and < 300)
        {
            var sid = TryReadString(payload, "sid");
            return sid is not null && CallSidPattern().IsMatch(sid)
                ? new(VoiceBridgeOutcome.Placed, VoiceBridgeResultCodes.Placed, sid)
                : new(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.ResponseInvalid);
        }

        return TryReadCode(payload) switch
        {
            // 'To' (the driver's mobile) is invalid, unreachable or not a phone number.
            21211 or 21214 or 21217 or 21401 =>
                new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DriverPhoneRejected),
            // Geographic permissions (only Mexico is enabled) or a blocked destination.
            21215 or 21216 => new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.DestinationNotAllowed),
            // The company number is not a valid, verified caller id of the account.
            21210 or 21212 or 21213 => new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.CallerIdRejected),
            21219 => new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.TrialRestricted),
            20003 or 20005 or 20404 => new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed),
            20429 => new(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.RateLimited),
            _ => ClassifyStatus(status),
        };
    }

    private static VoiceBridgeResult ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.AuthenticationFailed),
        HttpStatusCode.TooManyRequests => new(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.RateLimited),
        // The provider may have created the call before timing out.
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
            new(VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.Timeout),
        _ when (int)status >= 500 => new(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.Unavailable),
        _ => new(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Rejected),
    };

    private static string? TryReadString(string payload, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? TryReadCode(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt64(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private VoiceBridgeResult Log(VoiceBridgeResult result, int? status)
    {
        logger.LogInformation(
            ProviderCompleted,
            "Voice provider completed with provider {Provider}, outcome {Outcome}, code {Code}, status {Status}.",
            "TWILIO",
            result.Outcome,
            result.Code,
            status);
        return result;
    }

    [GeneratedRegex("^CA[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    internal static partial Regex CallSidPattern();
}
