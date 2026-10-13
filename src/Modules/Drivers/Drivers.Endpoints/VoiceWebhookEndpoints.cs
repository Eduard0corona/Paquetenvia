using System.Globalization;
using Drivers.Application.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Paqueteria.Application.Voice;
using static Drivers.Endpoints.VoiceEndpointResults;

namespace Drivers.Endpoints;

/// <summary>
/// VOICE-001-PROVIDER-TWILIO-2026-10-11 webhooks (AI-05 <c>receiveTwilioCallStatus</c>, <c>answerTwilioInboundCall</c>,
/// <c>confirmTwilioDriverKeypress</c>, <c>screenTwilioRecipient</c> and <c>confirmTwilioRecipientKeypress</c>).
/// Server-to-server: no session, cookie or CSRF; every request must carry a valid <c>X-Twilio-Signature</c> computed
/// over the configured public URL, otherwise 403. Inert (404) unless the live provider is configured. The posted
/// <c>From</c>, <c>To</c>, <c>Caller</c> and <c>Called</c> numbers are read only to verify the signature: they are
/// never logged, stored, audited or put in an event. The status callback keeps only the call id, its final status and
/// its duration; the key-press steps (VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11) keep only how far the bridge got.
/// </summary>
public static class VoiceWebhookEndpoints
{
    private const string TwimlContentType = "text/xml; charset=utf-8";

    private static readonly FormOptions WebhookFormOptions = new()
    {
        BufferBodyLengthLimit = 16 * 1024,
        MultipartBodyLengthLimit = 16 * 1024,
        ValueCountLimit = 64,
        KeyLengthLimit = 64,
        ValueLengthLimit = 4 * 1024,
    };

    public static IEndpointRouteBuilder MapVoiceWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(VoiceWebhookPaths.CallStatus, ReceiveCallStatusAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("receiveTwilioCallStatus")
            .WithTags("Voice")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(VoiceWebhookPaths.Inbound, AnswerInboundCallAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("answerTwilioInboundCall")
            .WithTags("Voice")
            .Produces<string>(StatusCodes.Status200OK, "text/xml")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost(VoiceWebhookPaths.DriverConfirm, ConfirmDriverKeypressAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("confirmTwilioDriverKeypress")
            .WithTags("Voice")
            .Produces<string>(StatusCodes.Status200OK, "text/xml")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost(VoiceWebhookPaths.RecipientScreen, ScreenRecipientAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("screenTwilioRecipient")
            .WithTags("Voice")
            .Produces<string>(StatusCodes.Status200OK, "text/xml")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost(VoiceWebhookPaths.RecipientConfirm, ConfirmRecipientKeypressAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithName("confirmTwilioRecipientKeypress")
            .WithTags("Voice")
            .Produces<string>(StatusCodes.Status200OK, "text/xml")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> ReceiveCallStatusAsync(
        HttpContext httpContext,
        IVoiceBridgeStatus voiceStatus,
        IVoiceWebhookVerifier verifier,
        IRecipientCallService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (voiceStatus.Mode != VoiceBridgeMode.Live)
        {
            return NotFound();
        }

        var (form, failure) = await ReadVerifiedFormAsync(httpContext, verifier, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (!TryCallRequest(httpContext.Request.Query, out var organizationId, out var callRequestId) ||
            !TrySingle(form!, "CallSid", out var callSid) ||
            !TrySingle(form!, "CallStatus", out var callStatus) ||
            !VoiceCallStatuses.All.Contains(callStatus))
        {
            return BadRequest();
        }

        int? duration = null;
        if (form!.TryGetValue("CallDuration", out var durations))
        {
            if (durations.Count != 1 ||
                !int.TryParse(durations[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                seconds > 86_400)
            {
                return BadRequest();
            }

            duration = seconds;
        }

        try
        {
            // An unknown or already final call request is acknowledged as well: the provider must not retry it.
            _ = await service.RecordStatusAsync(
                new VoiceCallStatusReport(
                    organizationId, callRequestId, callSid, callStatus, duration, httpContext.TraceIdentifier),
                cancellationToken);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientCallUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> AnswerInboundCallAsync(
        HttpContext httpContext,
        IVoiceBridgeStatus voiceStatus,
        IVoiceWebhookVerifier verifier,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (voiceStatus.Mode != VoiceBridgeMode.Live)
        {
            return NotFound();
        }

        var (_, failure) = await ReadVerifiedFormAsync(httpContext, verifier, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        // No session lookup by phone: a recipient who calls back hears a fixed message (or reaches the configured
        // dispatch line), never the driver.
        return Results.Content(verifier.BuildInboundCallResponse(), TwimlContentType);
    }

    /// <summary>
    /// The driver leg's one-digit gather ("Presiona 1 para llamar al destinatario"). Only a first, timely 1 on the
    /// platform's own call (<c>CallSid</c>) dials the recipient, through the screening; no digit (a voicemail or a
    /// timeout) or any other digit hangs up without dialing anyone.
    /// </summary>
    private static async Task<IResult> ConfirmDriverKeypressAsync(
        HttpContext httpContext,
        IVoiceBridgeStatus voiceStatus,
        IVoiceWebhookVerifier verifier,
        IRecipientCallService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (voiceStatus.Mode != VoiceBridgeMode.Live)
        {
            return NotFound();
        }

        var (form, failure) = await ReadVerifiedFormAsync(httpContext, verifier, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (!TryCallRequest(httpContext.Request.Query, out var organizationId, out var callRequestId) ||
            !TrySingle(form!, "CallSid", out var callSid) ||
            !TryKeypress(form!, out var confirmed))
        {
            return BadRequest();
        }

        var decision = await service.ConfirmDriverAsync(
            new VoiceKeypress(organizationId, callRequestId, callSid, confirmed, httpContext.TraceIdentifier),
            cancellationToken);
        return Results.Content(
            decision.Recipient is { } recipient
                ? verifier.BuildBridgeResponse(recipient, organizationId, callRequestId)
                : verifier.BuildHangupResponse(),
            TwimlContentType);
    }

    /// <summary>
    /// The recipient leg once answered (<c>&lt;Number url&gt;</c>): the neutral prompt "Tiene una llamada sobre su
    /// envío; presione 1 para contestar" and a one-digit gather; the legs stay apart until the answer of
    /// <see cref="ConfirmRecipientKeypressAsync"/>.
    /// </summary>
    private static async Task<IResult> ScreenRecipientAsync(
        HttpContext httpContext,
        IVoiceBridgeStatus voiceStatus,
        IVoiceWebhookVerifier verifier,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (voiceStatus.Mode != VoiceBridgeMode.Live)
        {
            return NotFound();
        }

        var (_, failure) = await ReadVerifiedFormAsync(httpContext, verifier, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        return TryCallRequest(httpContext.Request.Query, out var organizationId, out var callRequestId)
            ? Results.Content(verifier.BuildRecipientScreenResponse(organizationId, callRequestId), TwimlContentType)
            : BadRequest();
    }

    /// <summary>
    /// The recipient leg's one-digit gather. The key press binds to the platform's own call (<c>ParentCallSid</c>, the
    /// driver leg); only a 1 after the driver's own 1 connects both legs, anything else hangs up without bridging.
    /// </summary>
    private static async Task<IResult> ConfirmRecipientKeypressAsync(
        HttpContext httpContext,
        IVoiceBridgeStatus voiceStatus,
        IVoiceWebhookVerifier verifier,
        IRecipientCallService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (voiceStatus.Mode != VoiceBridgeMode.Live)
        {
            return NotFound();
        }

        var (form, failure) = await ReadVerifiedFormAsync(httpContext, verifier, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (!TryCallRequest(httpContext.Request.Query, out var organizationId, out var callRequestId) ||
            !TrySingle(form!, "ParentCallSid", out var parentCallSid) ||
            !TryKeypress(form!, out var confirmed))
        {
            return BadRequest();
        }

        var connect = await service.ConfirmRecipientAsync(
            new VoiceKeypress(organizationId, callRequestId, parentCallSid, confirmed, httpContext.TraceIdentifier),
            cancellationToken);
        return Results.Content(
            connect ? verifier.BuildConnectResponse() : verifier.BuildHangupResponse(),
            TwimlContentType);
    }

    private static async Task<(IFormCollection? Form, IResult? Failure)> ReadVerifiedFormAsync(
        HttpContext httpContext,
        IVoiceWebhookVerifier verifier,
        CancellationToken cancellationToken)
    {
        var request = httpContext.Request;
        var signatures = request.Headers["X-Twilio-Signature"];
        if (!IsFormUrlEncoded(request.ContentType))
        {
            return (null, BadRequest());
        }

        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = WebhookFormOptions.BufferBodyLengthLimit;
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(WebhookFormOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException &&
                                          !cancellationToken.IsCancellationRequested)
        {
            return (null, BadRequest());
        }

        var fields = form
            .SelectMany(field => field.Value.Select(value => new KeyValuePair<string, string>(field.Key, value ?? string.Empty)))
            .ToArray();
        var verdict = verifier.Verify(new VoiceWebhookRequest(
            request.Path.Value + request.QueryString.Value,
            signatures.Count == 1 ? signatures[0] : null,
            fields));
        return verdict switch
        {
            VoiceWebhookVerdict.Verified => (form, null),
            VoiceWebhookVerdict.NotConfigured => (null, NotFound()),
            _ => (null, Forbidden()),
        };
    }

    private static bool IsFormUrlEncoded(string? contentType) =>
        contentType is not null &&
        contentType.Split(';')[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exactly the tenant (<c>o</c>) and the call request (<c>r</c>) the platform put in the URL.</summary>
    private static bool TryCallRequest(IQueryCollection query, out Guid organizationId, out Guid callRequestId)
    {
        organizationId = Guid.Empty;
        callRequestId = Guid.Empty;
        return query.Count == 2 &&
               TryGuid(query, VoiceWebhookPaths.OrganizationParameter, out organizationId) &&
               TryGuid(query, VoiceWebhookPaths.CallRequestParameter, out callRequestId);
    }

    private static bool TryGuid(IQueryCollection query, string name, out Guid value)
    {
        value = Guid.Empty;
        return query.TryGetValue(name, out var values) && values.Count == 1 &&
            Guid.TryParseExact(values[0], "D", out value) && value != Guid.Empty;
    }

    private static bool TrySingle(IFormCollection form, string name, out string value)
    {
        value = string.Empty;
        if (!form.TryGetValue(name, out var values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    /// <summary>
    /// The gathered <c>Digits</c>: absent or empty (a timeout, a voicemail) and any digit but 1 are a refusal; only
    /// a repeated field is malformed.
    /// </summary>
    private static bool TryKeypress(IFormCollection form, out bool confirmed)
    {
        confirmed = false;
        if (!form.TryGetValue("Digits", out var digits))
        {
            return true;
        }

        if (digits.Count != 1)
        {
            return false;
        }

        confirmed = string.Equals(digits[0], "1", StringComparison.Ordinal);
        return true;
    }
}
