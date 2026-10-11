using System.Globalization;
using Drivers.Application.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Paqueteria.Application.Voice;

namespace Drivers.Endpoints;

/// <summary>
/// VOICE-001-PROVIDER-TWILIO-2026-10-11 webhooks (AI-05 <c>receiveTwilioCallStatus</c> and
/// <c>answerTwilioInboundCall</c>). Server-to-server: no session, cookie or CSRF; every request must carry a valid
/// <c>X-Twilio-Signature</c> computed over the configured public URL, otherwise 403. Inert (404) unless the live
/// provider is configured. The posted <c>From</c>, <c>To</c>, <c>Caller</c> and <c>Called</c> numbers are read only
/// to verify the signature: they are never logged, stored, audited or put in an event. The status callback keeps
/// only the call id, its final status and its duration.
/// </summary>
public static class VoiceWebhookEndpoints
{
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

        var query = httpContext.Request.Query;
        if (query.Count != 2 ||
            !TryGuid(query, VoiceWebhookPaths.OrganizationParameter, out var organizationId) ||
            !TryGuid(query, VoiceWebhookPaths.CallRequestParameter, out var callRequestId) ||
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
                new VoiceCallStatusReport(organizationId, callRequestId, callSid, callStatus, duration),
                cancellationToken);
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientCallUnavailableException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
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
        return Results.Content(verifier.BuildInboundCallResponse(), "text/xml; charset=utf-8");
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
            _ => (null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.")),
        };
    }

    private static bool IsFormUrlEncoded(string? contentType) =>
        contentType is not null &&
        contentType.Split(';')[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

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

    private static IResult BadRequest() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request.");

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");
}
