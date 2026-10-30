using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application.Security;

namespace Notifications.Endpoints;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006): the paths, body bounds and SEC-004 policies of the four
/// anonymous recipient notice operations. The host registers the policies with every other one
/// (<c>Paqueteria.Api.Http.ApiRateLimitPolicies</c>); a policy of a switched-off webhook spends nothing, so a webhook
/// that is off answers only the uniform 404 (O4).
/// </summary>
public static class RecipientNoticeEndpointDefaults
{
    public const string WhatsAppWebhookPath = "/api/v1/messaging/whatsapp/webhook";
    public const string EmailEventsPath = "/api/v1/messaging/email/events";
    public const string UnsubscribePath = "/api/v1/recipient-notices/unsubscribe/{unsubscribeToken}";
    public const string UnsubscribePathPrefix = "/api/v1/recipient-notices/unsubscribe";

    /// <summary>O3: <c>verifyMetaWhatsAppWebhook</c>, 10 per 60 s per client network.</summary>
    public const string WhatsAppVerifyRateLimitPolicy = "Messaging.WhatsAppWebhookVerify";

    /// <summary>O3: <c>receiveMetaWhatsAppWebhook</c>, 300 per 60 s per client network.</summary>
    public const string WhatsAppWebhookRateLimitPolicy = "Messaging.WhatsAppWebhook";

    /// <summary>O3: <c>receiveAcsEmailEvents</c>, 120 per 60 s per client network.</summary>
    public const string EmailEventsRateLimitPolicy = "Messaging.EmailEvents";

    /// <summary>O3: <c>unsubscribeRecipientNotices</c>, 20 per 60 s per client network, the uniform public 429.</summary>
    public const string UnsubscribeRateLimitPolicy = "RecipientNotices.Unsubscribe";

    /// <summary>O11 choice 2: the Meta webhook body bound (Meta may batch).</summary>
    public const int WhatsAppBodyLimitBytes = 256 * 1024;

    /// <summary>O11 choice 2: the Event Grid body bound (batching off, one event per request).</summary>
    public const int EmailEventsBodyLimitBytes = 64 * 1024;

    /// <summary>O11 choice 2: the unsubscribe body bound.</summary>
    public const int UnsubscribeBodyLimitBytes = 1024;

    /// <summary>O4: while <c>Messaging:WhatsApp:Webhook:Enabled</c> is false the two Meta policies are not applied.</summary>
    public static bool IsWhatsAppWebhookActive(IServiceProvider services) =>
        services.GetRequiredService<IRecipientNoticeWebhookGuard>().WhatsAppEnabled;

    /// <summary>O4: while <c>Messaging:Email:Events:Enabled</c> is false the Event Grid policy is not applied.</summary>
    public static bool IsEmailEventsActive(IServiceProvider services) =>
        services.GetRequiredService<IRecipientNoticeWebhookGuard>().EmailEventsEnabled;
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-05 <c>verifyMetaWhatsAppWebhook</c>, <c>receiveMetaWhatsAppWebhook</c>,
/// <c>receiveAcsEmailEvents</c> and <c>unsubscribeRecipientNotices</c>; AI-24 <c>recipient_notices.webhooks</c>).
/// Anonymous: no session, cookie or CSRF. A webhook whose switch is off answers the uniform 404 before anything else.
/// The Meta signature is checked in constant time over the raw body, bounded before it is read; the Event Grid secret
/// header in constant time before the body is read (O2). No answer, problem or log line carries a phone, an e-mail, a
/// token, a secret, a reference or a message text; every answer is <c>no-store</c>.
/// </summary>
public static partial class RecipientNoticeWebhookEndpoints
{
    private const string EventGridEventTypeHeader = "aeg-event-type";
    private const string MetaSignatureHeader = "X-Hub-Signature-256";
    private const string EventGridSecretHeader = "X-Paquetenvia-Event-Secret";

    private static readonly byte[] UniformNotFoundBody =
        """{"type":"about:blank","title":"Not Found","status":404}"""u8.ToArray();

    private static readonly byte[] UniformUnavailableBody =
        """{"type":"about:blank","title":"Service Unavailable","status":503}"""u8.ToArray();

    public static IEndpointRouteBuilder MapRecipientNoticeWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RecipientNoticeEndpointDefaults.WhatsAppWebhookPath, VerifyMetaAsync)
            .AllowAnonymous()
            .WithMetadata(RecipientNoticeBodyLimit.WhatsApp)
            .RequireRateLimiting(RecipientNoticeEndpointDefaults.WhatsAppVerifyRateLimitPolicy)
            .WithName("verifyMetaWhatsAppWebhook")
            .WithTags("Messaging")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost(RecipientNoticeEndpointDefaults.WhatsAppWebhookPath, ReceiveMetaAsync)
            .AllowAnonymous()
            .WithMetadata(RecipientNoticeBodyLimit.WhatsApp)
            .DisableAntiforgery()
            .RequireRateLimiting(RecipientNoticeEndpointDefaults.WhatsAppWebhookRateLimitPolicy)
            .WithName("receiveMetaWhatsAppWebhook")
            .WithTags("Messaging")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(RecipientNoticeEndpointDefaults.EmailEventsPath, ReceiveEmailEventsAsync)
            .AllowAnonymous()
            .WithMetadata(RecipientNoticeBodyLimit.EmailEvents)
            .DisableAntiforgery()
            .RequireRateLimiting(RecipientNoticeEndpointDefaults.EmailEventsRateLimitPolicy)
            .WithName("receiveAcsEmailEvents")
            .WithTags("Messaging")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(RecipientNoticeEndpointDefaults.UnsubscribePath, UnsubscribeAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RecipientNoticeEndpointDefaults.UnsubscribeRateLimitPolicy)
            // O3: the uniform public 429 body, without traceId, like publicTracking.
            .WithMetadata(PublicTrackingRateLimitRejection.Instance)
            .WithName("unsubscribeRecipientNotices")
            .WithTags("Messaging")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    /// <summary>
    /// The verification Meta sends when the callback URL is saved: <c>hub.challenge</c> as text only for
    /// <c>hub.mode=subscribe</c> and a verify token equal, in constant time, to one of the configured slots.
    /// </summary>
    private static IResult VerifyMetaAsync(HttpContext httpContext, IRecipientNoticeWebhookGuard guard)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!guard.WhatsAppEnabled)
        {
            return NotFound();
        }

        // O11 choice 7: the query (it carries hub.verify_token) was removed from the request target before hosting
        // diagnostics ran; only this feature holds it.
        var feature = httpContext.Features.Get<IRecipientNoticeVerificationQueryFeature>()
            ?? throw new InvalidOperationException(
                "Recipient notice request target redaction is not registered; refusing to verify the webhook.");
        var query = QueryHelpers.ParseQuery(feature.QueryString);
        if (!TrySingle(query, "hub.mode", out var mode) ||
            !TrySingle(query, "hub.challenge", out var challenge) ||
            !TrySingle(query, "hub.verify_token", out var token) ||
            !string.Equals(mode, "subscribe", StringComparison.Ordinal) ||
            !ChallengePattern().IsMatch(challenge) ||
            !guard.IsValidMetaVerifyToken(token))
        {
            return Forbidden();
        }

        return Results.Text(challenge, "text/plain", Encoding.UTF8);
    }

    private static async Task<IResult> ReceiveMetaAsync(
        HttpContext httpContext,
        IRecipientNoticeWebhookGuard guard,
        IRecipientNoticeWebhookService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!guard.WhatsAppEnabled)
        {
            return NotFound();
        }

        var (body, tooLarge) = await ReadBoundedAsync(
            httpContext.Request, RecipientNoticeEndpointDefaults.WhatsAppBodyLimitBytes, cancellationToken);
        if (tooLarge)
        {
            return PayloadTooLarge();
        }

        // AI-24 recipient_notices.webhooks.whatsapp: the signature, in constant time, before the body is parsed.
        if (!httpContext.Request.Headers.TryGetValue(MetaSignatureHeader, out var signatures) || signatures.Count != 1 ||
            !guard.IsValidMetaSignature(signatures[0], body.Span))
        {
            return Forbidden();
        }

        if (!MetaWhatsAppWebhookParser.TryParse(body, guard.WhatsAppPhoneNumberId, out var batch))
        {
            return BadRequest();
        }

        try
        {
            await service.ReceiveWhatsAppAsync(batch, cancellationToken);
            return Results.Ok();
        }
        catch (RecipientNoticeStoreUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> ReceiveEmailEventsAsync(
        HttpContext httpContext,
        IRecipientNoticeWebhookGuard guard,
        IRecipientNoticeWebhookService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!guard.EmailEventsEnabled)
        {
            return NotFound();
        }

        // AI-05: the subscription validation handshake is answered with its validationResponse; every other request is
        // a delivery and must carry the Key Vault secret header, compared in constant time before the body is read (O2).
        var handshake = httpContext.Request.Headers.TryGetValue(EventGridEventTypeHeader, out var eventTypes) &&
            eventTypes.Count == 1 && string.Equals(eventTypes[0], "SubscriptionValidation", StringComparison.Ordinal);
        if (!handshake)
        {
            if (!httpContext.Request.Headers.TryGetValue(EventGridSecretHeader, out var secrets) || secrets.Count == 0)
            {
                return Unauthorized();
            }

            if (secrets.Count != 1 || !guard.IsValidEventGridSecret(secrets[0]))
            {
                return Forbidden();
            }
        }

        var (body, tooLarge) = await ReadBoundedAsync(
            httpContext.Request, RecipientNoticeEndpointDefaults.EmailEventsBodyLimitBytes, cancellationToken);
        if (tooLarge)
        {
            return PayloadTooLarge();
        }

        if (!AcsEmailEventsParser.TryParse(body, guard.EmailEventsSourceResourceId, out var batch))
        {
            return BadRequest();
        }

        if (handshake)
        {
            return batch.ValidationCode is { } code
                ? Results.Json(new EventGridValidationResponse(code), statusCode: StatusCodes.Status200OK)
                : BadRequest();
        }

        try
        {
            await service.ReceiveEmailEventsAsync(batch.Receipts, cancellationToken);
            return Results.Ok();
        }
        catch (RecipientNoticeStoreUnavailableException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// The RFC 8058 one-click POST of a mailbox and the button of the page <c>/avisos/baja/:token</c>. The body is empty,
    /// <c>List-Unsubscribe=One-Click</c> (form or multipart) or <c>{}</c>; there is no GET and no redirect (O11 choice 7).
    /// The answers 404 and 503 keep the uniform public body, without traceId.
    /// </summary>
    private static async Task<IResult> UnsubscribeAsync(
        HttpContext httpContext,
        IRecipientNoticeUnsubscribeService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
        // The route value is the redacted placeholder: the token is read only from the redaction feature.
        var feature = httpContext.Features.Get<IRecipientNoticeUnsubscribeTokenFeature>()
            ?? throw new InvalidOperationException(
                "Recipient notice request target redaction is not registered; refusing to serve the unsubscribe.");
        var (body, tooLarge) = await ReadBoundedAsync(
            httpContext.Request, RecipientNoticeEndpointDefaults.UnsubscribeBodyLimitBytes, cancellationToken);
        if (tooLarge)
        {
            return PayloadTooLarge();
        }

        if (!await IsUnsubscribeBodyAsync(httpContext.Request.ContentType, body, cancellationToken))
        {
            return BadRequest();
        }

        return await service.UnsubscribeAsync(feature.Token, cancellationToken) switch
        {
            RecipientNoticeUnsubscribeResult.Recorded => Results.NoContent(),
            RecipientNoticeUnsubscribeResult.Unavailable => Uniform(UniformUnavailableBody, StatusCodes.Status503ServiceUnavailable),
            _ => Uniform(UniformNotFoundBody, StatusCodes.Status404NotFound),
        };
    }

    /// <summary>
    /// AI-05 <c>unsubscribeRecipientNotices</c> (O11 choice 7): an empty body; <c>application/x-www-form-urlencoded</c> or
    /// <c>multipart/form-data</c> with exactly the field <c>List-Unsubscribe=One-Click</c> (RFC 8058); or the JSON object
    /// <c>{}</c> of the public page.
    /// </summary>
    internal static async Task<bool> IsUnsubscribeBodyAsync(
        string? contentType,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (body.IsEmpty)
        {
            return true;
        }

        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) || mediaType.MediaType.Value is not { } type)
        {
            return false;
        }

        if (string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    !document.RootElement.EnumerateObject().Any();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (string.Equals(type, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(body.Span);
            var fields = QueryHelpers.ParseQuery(text.StartsWith('?') ? text : "?" + text);
            return IsOneClick(fields);
        }

        if (string.Equals(type, "multipart/form-data", StringComparison.OrdinalIgnoreCase) &&
            HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is { Length: > 0 and <= 70 } boundary)
        {
            try
            {
                using var stream = new MemoryStream(body.ToArray(), writable: false);
                var reader = new MultipartReader(boundary, stream) { HeadersCountLimit = 4, HeadersLengthLimit = 512 };
                var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
                while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                        !disposition.IsFormDisposition() || disposition.IsFileDisposition() ||
                        HeaderUtilities.RemoveQuotes(disposition.Name).Value is not { } name ||
                        fields.ContainsKey(name))
                    {
                        return false;
                    }

                    using var value = new StreamReader(section.Body, Encoding.UTF8);
                    fields[name] = await value.ReadToEndAsync(cancellationToken);
                }

                return IsOneClick(fields);
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsOneClick(IDictionary<string, StringValues> fields) =>
        fields.Count == 1 &&
        fields.TryGetValue("List-Unsubscribe", out var values) &&
        values.Count == 1 &&
        string.Equals(values[0], "One-Click", StringComparison.Ordinal);

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes. A declared or actual larger body is too large, without reading the
    /// rest.
    /// </summary>
    private static async Task<(ReadOnlyMemory<byte> Body, bool TooLarge)> ReadBoundedAsync(
        HttpRequest request,
        int limit,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return (ReadOnlyMemory<byte>.Empty, true);
        }

        var buffer = new ArrayBufferWriter<byte>(Math.Min(limit, 8192) + 1);
        while (true)
        {
            // Never more than limit + 1 bytes in all: one byte past the bound is enough to know it is too large, and the
            // server bound the endpoint declares (RecipientNoticeBodyLimit) is never reached first.
            var memory = buffer.GetMemory(4096);
            var room = limit + 1 - buffer.WrittenCount;
            if (memory.Length > room)
            {
                memory = memory[..room];
            }

            var read = await request.Body.ReadAsync(memory, cancellationToken);
            if (read == 0)
            {
                return (buffer.WrittenMemory, false);
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                return (ReadOnlyMemory<byte>.Empty, true);
            }
        }
    }

    private static bool TrySingle(Dictionary<string, StringValues> query, string name, out string value)
    {
        value = string.Empty;
        if (!query.TryGetValue(name, out var values) || values.Count != 1 || values[0] is not { } single)
        {
            return false;
        }

        value = single;
        return true;
    }

    private static IResult Uniform(byte[] body, int status) => new UniformProblemResult(body, status);

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized.");

    private static IResult BadRequest() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request.");

    private static IResult PayloadTooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload Too Large.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengePattern();

    /// <summary>The Event Grid handshake answer (AI-05 <c>EventGridValidationResponse</c>).</summary>
    private sealed record EventGridValidationResponse(
        [property: JsonPropertyName("validationResponse")] string ValidationResponse);

    /// <summary>A uniform public problem body with its status, without traceId (Invariant 5).</summary>
    private sealed class UniformProblemResult(byte[] body, int status) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "application/problem+json";
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted);
        }
    }
}

/// <summary>
/// The body bound a recipient notice webhook declares (<see cref="IRequestSizeLimitMetadata"/>): its own streaming cap
/// plus the one byte that proves a body too large. The host's 1 MiB check then leaves the webhook alone, so a switched-off
/// webhook answers its uniform 404 before any size check (AI-05 "before anything else", O4), and routing sets the server
/// bound to this value.
/// </summary>
internal sealed class RecipientNoticeBodyLimit(long maxRequestBodySize) : IRequestSizeLimitMetadata
{
    public static RecipientNoticeBodyLimit WhatsApp { get; } = new(RecipientNoticeEndpointDefaults.WhatsAppBodyLimitBytes + 1L);

    public static RecipientNoticeBodyLimit EmailEvents { get; } = new(RecipientNoticeEndpointDefaults.EmailEventsBodyLimitBytes + 1L);

    public long? MaxRequestBodySize { get; } = maxRequestBodySize;
}
