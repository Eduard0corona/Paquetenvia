using System.Text.Json;
using System.Text.Json.Serialization;
using Drivers.Application.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Drivers.Endpoints;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11: the driver's own mobile number for the masked call bridge (AI-05
/// <c>getMyDriverPhone</c>, <c>registerMyDriverPhone</c>, <c>removeMyDriverPhone</c>). DRIVER only. The number is
/// accepted once, with explicit consent, and never returned: the answer only says whether one is stored.
/// </summary>
public static class DriverPhoneEndpoints
{
    public const string Path = "/api/v1/driver/me/phone";
    private const long MaximumBodyBytes = 1024;

    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapDriverPhoneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getMyDriverPhone")
            .WithTags("Driver")
            .Produces<DriverPhoneStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPut(Path, RegisterAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("registerMyDriverPhone")
            .WithTags("Driver")
            .Accepts<RegisterDriverPhoneRequest>("application/json")
            .Produces<DriverPhoneStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapDelete(Path, RemoveAsync)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("removeMyDriverPhone")
            .WithTags("Driver")
            .Produces<DriverPhoneStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverPhoneService service,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetMyDriverPhone) is { } denied)
        {
            return denied;
        }

        return await ExecuteAsync(
            httpContext,
            () => service.GetAsync(actorId, tenantContext.OrganizationId, cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverPhoneService service,
        CancellationToken cancellationToken)
    {
        // Shape first, without any persisted state: JSON, bounded, exactly the three fields, consent given for the
        // current text and a dialable Mexican mobile number.
        if (!httpContext.Request.HasJsonContentType() ||
            httpContext.Request.ContentLength is not (> 0 and <= MaximumBodyBytes))
        {
            return Conflict();
        }

        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = MaximumBodyBytes;
        }

        RegisterDriverPhoneRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<RegisterDriverPhoneRequest>(
                RequestJsonOptions,
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or BadHttpRequestException)
        {
            return Conflict();
        }

        if (request is null || request.ExtensionData is { Count: > 0 } ||
            request.ConsentAccepted is not true ||
            !string.Equals(request.ConsentVersion, DriverPhoneConsent.CurrentVersion, StringComparison.Ordinal) ||
            !DriverPhonePolicy.TryNormalize(request.Phone, out var digits))
        {
            return Conflict();
        }

        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.RegisterMyDriverPhone) is { } denied)
        {
            return denied;
        }

        return await ExecuteAsync(
            httpContext,
            () => service.RegisterAsync(
                new RegisterDriverPhoneCommand(
                    actorId,
                    tenantContext.OrganizationId,
                    digits,
                    DriverPhoneConsent.CurrentVersion,
                    httpContext.TraceIdentifier),
                cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> RemoveAsync(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverPhoneService service,
        CancellationToken cancellationToken)
    {
        if (!TrySession(session, tenantContext, out var actorId))
        {
            return Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.RemoveMyDriverPhone) is { } denied)
        {
            return denied;
        }

        return await ExecuteAsync(
            httpContext,
            () => service.RemoveAsync(actorId, tenantContext.OrganizationId, httpContext.TraceIdentifier, cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext httpContext,
        Func<Task<DriverPhoneStatusResult>> operation,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var status = await operation();
            return Results.Ok(new DriverPhoneStatusResponse(
                status.VoiceCallsEnabled,
                status.Registered,
                status.ConsentVersion,
                status.ConsentedAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DriverPhoneForbiddenException)
        {
            return Forbidden();
        }
        catch (DriverPhoneUnavailableException)
        {
            return Unavailable();
        }
    }

    private static bool TrySession(IOrganizationRequestSession session, ITenantContext tenantContext, out Guid actorId)
    {
        actorId = default;
        if (!session.IsActive || session.UserId is not { } id || !tenantContext.IsSelected)
        {
            return false;
        }

        actorId = id;
        return true;
    }

    private static IResult Conflict() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = "INVALID_REQUEST" });

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}

public sealed record RegisterDriverPhoneRequest(
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("consent_accepted")] bool? ConsentAccepted,
    [property: JsonPropertyName("consent_version")] string? ConsentVersion)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>The phone is personal data and never appears here.</summary>
    public override string ToString() => "RegisterDriverPhoneRequest { [redacted] }";
}

public sealed record DriverPhoneStatusResponse(
    [property: JsonPropertyName("voice_calls_enabled")] bool VoiceCallsEnabled,
    [property: JsonPropertyName("registered")] bool Registered,
    [property: JsonPropertyName("consent_version")] string? ConsentVersion,
    [property: JsonPropertyName("consented_at")] DateTimeOffset? ConsentedAt);
