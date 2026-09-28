using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Messaging;

namespace Paqueteria.Infrastructure.Messaging;

/// <summary>
/// GATE-004-CHANNELS email adapter over the Azure Communication Services Email REST API
/// (<c>POST {Endpoint}/emails:send?api-version=2023-03-31</c>), authenticated with the workload
/// managed identity (Microsoft Entra token for <c>https://communication.azure.com/.default</c>); no
/// connection string or access key is read. The message id is sent as <c>Operation-Id</c> so a retry
/// of the same Notification reuses the same operation. 202 means ACS accepted the message for
/// delivery, not that the mailbox received it.
/// </summary>
internal sealed class AzureCommunicationEmailProvider(
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    IOptions<MessagingOptions> options,
    TimeProvider time,
    ILogger<AzureCommunicationEmailProvider> logger) : IMessagingChannelProvider, IDisposable
{
    public const string HttpClientName = "Paqueteria.Messaging.AzureCommunicationEmail";
    public const string TokenScope = "https://communication.azure.com/.default";
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private volatile CachedToken? _token;

    public MessagingChannel Channel => MessagingChannel.Email;

    public async ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
    {
        var channel = options.Value.Email;
        var acs = channel.AzureCommunicationServices;
        if (!channel.Templates.TryGetValue(request.TemplateKey, out var template))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateNotConfigured), null);
        }

        if (!MessagingHttp.AreParametersValid(request.Parameters, template.ParameterCount))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.TemplateParametersInvalid), null);
        }

        if (!MessagingOptionsValidator.IsEmailAddress(request.Recipient.Address))
        {
            return Log(new(MessagingOutcome.PermanentFailure, MessagingResultCodes.RecipientInvalid), null);
        }

        string token;
        try
        {
            token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The managed identity endpoint failed before anything was sent.
            return Log(new(MessagingOutcome.TransientFailure, MessagingResultCodes.AuthenticationFailed), null);
        }

        var body = new JsonObject
        {
            ["senderAddress"] = acs.SenderAddress,
            ["content"] = new JsonObject
            {
                ["subject"] = MessagingHttp.Render(template.Subject, request.Parameters),
                ["plainText"] = MessagingHttp.Render(template.PlainText, request.Parameters),
            },
            ["recipients"] = new JsonObject
            {
                ["to"] = new JsonArray { new JsonObject { ["address"] = request.Recipient.Address } },
            },
            ["userEngagementTrackingDisabled"] = true,
        };
        // Absolute, never relative: "emails:send" would otherwise parse as a URI scheme.
        var uri = new Uri(
            $"{acs.Endpoint.TrimEnd('/')}/emails:send?api-version={Uri.EscapeDataString(acs.ApiVersion)}",
            UriKind.Absolute);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation("Operation-Id", request.MessageId.ToString("D"));

        var (response, failure) = await MessagingHttp.SendOnceAsync(
            httpClientFactory.CreateClient(HttpClientName), message, TimeSpan.FromSeconds(acs.TimeoutSeconds), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return Log(failure, null);
        }

        using (response)
        {
            if (response!.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                InvalidateToken();
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Log(Classify(response, payload), (int)response.StatusCode);
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    private MessagingResult Classify(HttpResponseMessage response, string payload)
    {
        if (response.IsSuccessStatusCode)
        {
            // Like the WhatsApp adapter, a success status without a confirmed operation id is not
            // trusted as SENT; the caller's Operation-Id makes the outbox retry idempotent in ACS.
            return TryReadOperationId(payload) is { } operationId
                ? new(MessagingOutcome.Accepted, MessagingResultCodes.Accepted, operationId)
                : new(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.ResponseInvalid);
        }

        return MessagingHttp.ClassifyStatus(response.StatusCode, response.Headers, time);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && cached.ExpiresOn - TokenRefreshMargin > time.GetUtcNow())
        {
            return cached.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } current && current.ExpiresOn - TokenRefreshMargin > time.GetUtcNow())
            {
                return current.Token;
            }

            var fresh = await credential.GetTokenAsync(new TokenRequestContext([TokenScope]), cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(fresh.Token))
            {
                throw new InvalidOperationException("The workload credential returned an empty token.");
            }

            _token = new CachedToken(fresh.Token, fresh.ExpiresOn);
            return fresh.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateToken() => _token = null;

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresOn)
    {
        public override string ToString() => "CachedToken";
    }

    private static string? TryReadOperationId(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("id", out var id) &&
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

    private MessagingResult Log(MessagingResult result, int? status)
    {
        logger.LogInformation(
            "Messaging provider completed with channel {Channel}, provider {Provider}, outcome {Outcome}, code {Code}, status {Status}.",
            "EMAIL",
            "AZURE_COMMUNICATION_SERVICES",
            result.Outcome,
            result.Code,
            status);
        return result;
    }
}
