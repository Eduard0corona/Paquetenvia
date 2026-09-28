using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Messaging;

namespace Paqueteria.Infrastructure.Messaging;

/// <summary>
/// GATE-004-CHANNELS WhatsApp adapter over the Meta WhatsApp Cloud API
/// (<c>POST {BaseUri}/{ApiVersion}/{PhoneNumberId}/messages</c>, template messages only). Business-initiated
/// messages must use an approved template, so free-form text is not offered. The Cloud API has no
/// idempotency key: an ambiguous timeout retried by the outbox may deliver twice.
/// </summary>
internal sealed class MetaWhatsAppCloudApiProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<MessagingOptions> options,
    TimeProvider time,
    ILogger<MetaWhatsAppCloudApiProvider> logger) : IMessagingChannelProvider, IDisposable
{
    public const string HttpClientName = "Paqueteria.Messaging.MetaWhatsAppCloudApi";

    private readonly MessagingProviderGuard _guard = new(
        time,
        options.Value.WhatsApp.MetaCloudApi.CircuitBreakerFailureThreshold,
        TimeSpan.FromSeconds(options.Value.WhatsApp.MetaCloudApi.CircuitBreakerBreakSeconds),
        options.Value.WhatsApp.MetaCloudApi.MaxConcurrentRequests);

    internal MessagingProviderGuard Guard => _guard;

    // Meta error codes (https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes).
    private static readonly HashSet<long> TransientCodes =
    [
        1,      // API unknown
        2,      // API service
        4,      // API too many calls
        80007,  // WABA rate limit
        130429, // throughput rate limit
        131000, // something went wrong
        131016, // service unavailable
        131048, // spam rate limit
        131056, // business/consumer pair rate limit
        131057, // account in maintenance mode
        133004, // server temporarily unavailable
    ];

    private static readonly HashSet<long> ThrottleCodes = [4, 80007, 130429, 131048, 131056];

    public MessagingChannel Channel => MessagingChannel.WhatsApp;

    public async ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
    {
        var channel = options.Value.WhatsApp;
        var meta = channel.MetaCloudApi;
        if (!channel.Templates.TryGetValue(request.TemplateKey, out var template))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateNotConfigured), null);
        }

        if (!MessagingHttp.AreParametersValid(request.Parameters, template.ParameterCount))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateParametersInvalid), null);
        }

        if (!MessagingHttp.TryNormalizePhone(request.Recipient.Address, out var to))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientInvalid), null);
        }

        var body = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = to,
            ["type"] = "template",
            ["template"] = BuildTemplate(template, request.Parameters),
        };
        var uri = new Uri(
            $"{meta.BaseUri.TrimEnd('/')}/{Uri.EscapeDataString(meta.ApiVersion)}/{Uri.EscapeDataString(meta.PhoneNumberId)}/messages",
            UriKind.Absolute);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", meta.AccessToken);

        var (result, status) = await _guard.RunAsync(
            token => SendToProviderAsync(message, meta.TimeoutSeconds, token), cancellationToken).ConfigureAwait(false);
        return Log(result, status);
    }

    public void Dispose() => _guard.Dispose();

    private async ValueTask<(MessagingResult Result, int? Status)> SendToProviderAsync(
        HttpRequestMessage message,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var (response, failure) = await MessagingHttp.SendOnceAsync(
            httpClientFactory.CreateClient(HttpClientName), message, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return (failure, null);
        }

        using (response)
        {
            var payload = await response!.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (Classify(response, payload), (int)response.StatusCode);
        }
    }

    private MessagingResult Classify(HttpResponseMessage response, string payload)
    {
        if (response.IsSuccessStatusCode)
        {
            var id = TryReadMessageId(payload);
            // 2xx without a message id: the send may or may not exist; let the outbox retry decide.
            return id is null
                ? new(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.ResponseInvalid)
                : new(MessagingOutcome.Accepted, MessagingResultCodes.Accepted, id);
        }

        var code = TryReadErrorCode(payload);
        if (code is null)
        {
            return MessagingHttp.ClassifyStatus(response.StatusCode, response.Headers, time);
        }

        if (TransientCodes.Contains(code.Value))
        {
            return new(
                MessagingOutcome.TransientFailure,
                ThrottleCodes.Contains(code.Value) ? MessagingResultCodes.RateLimited : MessagingResultCodes.Unavailable,
                RetryAfter: MessagingHttp.ReadRetryAfter(response.Headers, time));
        }

        return code.Value switch
        {
            190 or 0 or 3 or 10 or (>= 200 and <= 299) => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.AuthenticationFailed),
            131026 or 131030 or 133010 => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientUndeliverable),
            131047 => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.ReengagementRequired),
            131008 or 131009 or (>= 132000 and <= 132999) => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.ProviderTemplateRejected),
            368 or 131031 or 131049 or 131050 => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.PolicyBlocked),
            _ when MessagingHttp.IsServerOrThrottle(response.StatusCode) => MessagingHttp.ClassifyStatus(response.StatusCode, response.Headers, time),
            _ => new(MessagingOutcome.PermanentFailure, MessagingResultCodes.Rejected),
        };
    }

    private static JsonObject BuildTemplate(WhatsAppTemplateOptions template, IReadOnlyList<string> parameters)
    {
        var result = new JsonObject
        {
            ["name"] = template.Name,
            ["language"] = new JsonObject { ["code"] = template.LanguageCode },
        };
        if (parameters.Count > 0)
        {
            var values = new JsonArray();
            foreach (var parameter in parameters)
            {
                values.Add(new JsonObject { ["type"] = "text", ["text"] = parameter });
            }

            result["components"] = new JsonArray
            {
                new JsonObject { ["type"] = "body", ["parameters"] = values },
            };
        }

        return result;
    }

    private static string? TryReadMessageId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() == 1 &&
                messages[0].ValueKind == JsonValueKind.Object &&
                messages[0].TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 and <= 256 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static long? TryReadErrorCode(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt64(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private MessagingResult Log(MessagingResult result, int? status)
    {
        logger.LogInformation(
            "Messaging provider completed with channel {Channel}, provider {Provider}, outcome {Outcome}, code {Code}, status {Status}.",
            "WHATSAPP",
            "META_CLOUD_API",
            result.Outcome,
            result.Code,
            status);
        return result;
    }
}
