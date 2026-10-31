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
/// (<c>POST {BaseUri}/{ApiVersion}/{PhoneNumberId}/messages</c>). Business-initiated messages use an approved template;
/// the only free-form text is the fixed answer to an inbound message inside its 24-hour customer service window
/// (<see cref="MessagingRequest.FreeFormText"/>, AI-08 NTF-006). The Cloud API has no idempotency key: an ambiguous
/// timeout retried by the outbox may deliver twice.
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

    public MessagingChannel Channel => MessagingChannel.WhatsApp;

    public async ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
    {
        var channel = options.Value.WhatsApp;
        var meta = channel.MetaCloudApi;
        WhatsAppTemplateOptions? template = null;
        if (request.FreeFormText is null && !channel.Templates.TryGetValue(request.TemplateKey, out template))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateNotConfigured), null);
        }

        if ((template is null
                ? !MessagingHttp.IsFreeFormTextValid(request)
                : !MessagingHttp.AreParametersValid(request.Parameters, template.ParameterCount)) ||
            !MessagingHttp.AreExtrasValid(request))
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
        };
        if (template is null)
        {
            // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-24 recipient_notices.auto_reply.how): a free-form text inside
            // the customer service window the inbound message opened, without a link preview.
            body["type"] = "text";
            body["text"] = new JsonObject { ["preview_url"] = false, ["body"] = request.FreeFormText };
        }
        else
        {
            body["type"] = "template";
            body["template"] = BuildTemplate(template, request);
        }

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

        if (MetaWhatsAppErrorCodes.IsTransient(code.Value))
        {
            return new(
                MessagingOutcome.TransientFailure,
                MetaWhatsAppErrorCodes.Classify(code.Value),
                RetryAfter: MessagingHttp.ReadRetryAfter(response.Headers, time));
        }

        var classified = MetaWhatsAppErrorCodes.Classify(code.Value);
        return classified == MessagingResultCodes.Rejected && MessagingHttp.IsServerOrThrottle(response.StatusCode)
            ? MessagingHttp.ClassifyStatus(response.StatusCode, response.Headers, time)
            : new(MessagingOutcome.PermanentFailure, classified);
    }

    /// <summary>
    /// The template object of a send: the body parameters and, for a recipient notice (AI-24
    /// <c>recipient_notices.templates.whatsapp</c>, NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 9), the URL button
    /// "Ver seguimiento" as the first button (index 0, its dynamic suffix) and the quick reply "No recibir avisos" as the
    /// second (index 1, its payload). The approved template must declare the buttons in that order.
    /// </summary>
    internal static JsonObject BuildTemplate(WhatsAppTemplateOptions template, MessagingRequest request)
    {
        var result = new JsonObject
        {
            ["name"] = template.Name,
            ["language"] = new JsonObject { ["code"] = template.LanguageCode },
        };
        var components = new JsonArray();
        if (request.Parameters.Count > 0)
        {
            var values = new JsonArray();
            foreach (var parameter in request.Parameters)
            {
                values.Add(new JsonObject { ["type"] = "text", ["text"] = parameter });
            }

            components.Add(new JsonObject { ["type"] = "body", ["parameters"] = values });
        }

        if (request.UrlButtonSuffix is { } suffix)
        {
            components.Add(new JsonObject
            {
                ["type"] = "button",
                ["sub_type"] = "url",
                ["index"] = "0",
                ["parameters"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = suffix } },
            });
        }

        if (request.QuickReplyPayload is { } payload)
        {
            components.Add(new JsonObject
            {
                ["type"] = "button",
                ["sub_type"] = "quick_reply",
                ["index"] = "1",
                ["parameters"] = new JsonArray { new JsonObject { ["type"] = "payload", ["payload"] = payload } },
            });
        }

        if (components.Count > 0)
        {
            result["components"] = components;
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
