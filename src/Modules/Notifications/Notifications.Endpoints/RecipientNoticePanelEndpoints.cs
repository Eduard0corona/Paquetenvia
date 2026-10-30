using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Notifications.Application.RecipientNotices;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;

namespace Notifications.Endpoints;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-05 x-pilot-contract-deltas NTF-RECIPIENT-NOTICES part
/// NTF-007; T6 choice 1): the order detail's panel "Avisos al destinatario" and its button "Detener avisos". They live in
/// the Notifications module, which owns the notices and the opt-outs, and keep the AI-05 tag Orders.
/// <list type="bullet">
/// <item>Capability is decided before any order is read (x-capability-matrix recipient_notice_operations: DISPATCHER,
/// and PLATFORM_ADMIN with a satisfied MFA challenge, 403 <c>MFA_REQUIRED</c> when only the second factor is missing);
/// the stop checks its shape (one valid Idempotency-Key, no query, no body) before that.</item>
/// <item>A malformed, missing or foreign order, and an order the selected organization only operates, is the uniform 404.</item>
/// <item>No answer, problem or log line carries a phone, an e-mail, a hash, a link or a template parameter; every answer
/// the two handlers give, problems included, is <c>Cache-Control: no-store</c>.</item>
/// </list>
/// AI-24 gives neither operation a switch, and neither is on the SEC-004 list (T6 choice 10): no rate-limit policy.
/// </summary>
public static class RecipientNoticePanelEndpoints
{
    public const string ListRoute = "/api/v1/orders/{orderId}/recipient-notices";
    public const string StopRoute = "/api/v1/orders/{orderId}/recipient-notices/stop";

    public static IEndpointRouteBuilder MapRecipientNoticePanelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(ListRoute, ListAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("listOrderRecipientNotices")
            .WithTags("Orders")
            .Produces<RecipientNoticeListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost(StopRoute, StopAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("stopOrderRecipientNotices")
            .WithTags("Orders")
            .Produces<RecipientNoticeStopResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRecipientNoticePanelService service,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext);
        if (Refusal(session, tenantContext, TenantCapabilities.ListOrderRecipientNotices, out var actorId) is { } refused)
        {
            return refused;
        }

        if (!TryParseOrderId(orderId, out var parsedOrderId))
        {
            return NotFound();
        }

        try
        {
            var result = await service.ListAsync(
                new ListOrderRecipientNoticesQuery(actorId, tenantContext.OrganizationId, parsedOrderId),
                cancellationToken);
            return Results.Json(RecipientNoticeListResponse.From(result), statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientNoticePanelNotFoundException)
        {
            return NotFound();
        }
        catch (RecipientNoticePanelUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> StopAsync(
        string orderId,
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IRecipientNoticePanelService service,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext);
        if (!TryReadShape(httpContext.Request, out var idempotencyKey))
        {
            return Conflict(RecipientNoticeStopConflictCodes.InvalidRequest);
        }

        if (Refusal(session, tenantContext, TenantCapabilities.StopOrderRecipientNotices, out var actorId) is { } refused)
        {
            return refused;
        }

        if (!TryParseOrderId(orderId, out var parsedOrderId))
        {
            return NotFound();
        }

        try
        {
            var result = await service.StopAsync(
                new StopOrderRecipientNoticesCommand(actorId, tenantContext.OrganizationId, parsedOrderId, idempotencyKey),
                cancellationToken);
            return Results.Json(RecipientNoticeStopResponse.From(result), statusCode: StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RecipientNoticeStopConflictException conflict)
        {
            return Conflict(conflict.Code);
        }
        catch (RecipientNoticePanelNotFoundException)
        {
            return NotFound();
        }
        catch (RecipientNoticePanelUnavailableException)
        {
            return Unavailable();
        }
    }

    /// <summary>The session, the selected organization and the capability, before anything is read.</summary>
    private static IResult? Refusal(
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        TenantCapability capability,
        out Guid actorId)
    {
        actorId = Guid.Empty;
        if (!session.IsActive ||
            session.UserId is not { } userId ||
            userId == Guid.Empty ||
            !tenantContext.IsSelected ||
            tenantContext.OrganizationId == Guid.Empty)
        {
            return TenantCapabilityGate.Forbidden();
        }

        actorId = userId;
        return TenantCapabilityGate.Deny(session, tenantContext, capability);
    }

    /// <summary>
    /// stopOrderRecipientNotices takes exactly one valid Idempotency-Key, no query and no body (AI-05); anything else is
    /// 409 INVALID_REQUEST before the capability or any state is read.
    /// </summary>
    internal static bool TryReadShape(HttpRequest request, out string idempotencyKey)
    {
        idempotencyKey = string.Empty;
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || !IdempotencyKeyPolicy.IsValid(values[0]) ||
            request.QueryString.HasValue ||
            request.ContentLength is > 0 ||
            request.Headers.TransferEncoding.Count > 0)
        {
            return false;
        }

        idempotencyKey = values[0]!;
        return true;
    }

    private static bool TryParseOrderId(string? value, out Guid orderId) =>
        Guid.TryParseExact(value, "D", out orderId) && orderId != Guid.Empty;

    private static void NoStore(HttpContext httpContext) =>
        httpContext.Response.Headers.CacheControl = "no-store";

    private static IResult Conflict(string code) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict.",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

/// <summary>AI-05 <c>RecipientNoticeList</c>.</summary>
public sealed record RecipientNoticeListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<RecipientNoticeResponse> Items,
    [property: JsonPropertyName("whatsapp_consent_attested")] bool WhatsAppConsentAttested,
    [property: JsonPropertyName("email_on_file")] bool EmailOnFile)
{
    public static RecipientNoticeListResponse From(RecipientNoticeListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RecipientNoticeListResponse(
            result.Items.Select(RecipientNoticeResponse.From).ToArray(),
            result.WhatsAppConsentAttested,
            result.EmailOnFile);
    }
}

/// <summary>AI-05 <c>RecipientNotice</c>: never the phone, the e-mail, the link or a template parameter.</summary>
public sealed record RecipientNoticeResponse(
    [property: JsonPropertyName("notification_id")] Guid NotificationId,
    [property: JsonPropertyName("notice")] string Notice,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("last_code")] string? LastCode,
    [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset? DeliveredAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    public static RecipientNoticeResponse From(RecipientNoticeListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new RecipientNoticeResponse(
            item.NotificationId,
            item.Notice,
            item.Channel,
            item.Status,
            item.Attempts,
            item.LastCode,
            item.SentAt,
            item.DeliveredAt,
            item.UpdatedAt);
    }
}

/// <summary>AI-05 <c>RecipientNoticeStopResult</c>: the order, the channels on record and the server time of the action.</summary>
public sealed record RecipientNoticeStopResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("channels")] IReadOnlyList<string> Channels,
    [property: JsonPropertyName("recorded_at")] DateTimeOffset RecordedAt)
{
    public static RecipientNoticeStopResponse From(RecipientNoticeStopResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new RecipientNoticeStopResponse(result.OrderId, result.Channels.ToArray(), result.RecordedAt);
    }
}
