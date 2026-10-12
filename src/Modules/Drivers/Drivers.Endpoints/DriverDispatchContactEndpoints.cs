using System.Text.Json.Serialization;
using Drivers.Application.DispatchContacts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Organizations.Application.Session;
using Organizations.Endpoints.Authorization;
using Organizations.Endpoints.Tenancy;
using Paqueteria.Application.Tenancy;

namespace Drivers.Endpoints;

/// <summary>
/// DRV-DISPATCH-CONTACT-2026-10-12 (project owner, "Sí, configurable (Recommended)"): AI-05
/// <c>getMyDispatchContact</c>, the dispatch number configured for the organization selected with
/// X-Organization-Id, or null when it has none. DRIVER members only, like the other <c>/driver/me</c> reads: the
/// session's ACTIVE membership and the capability are checked before anything is read. It reads the server
/// configuration only, never the database, and never returns another organization's number. Every answer carries
/// Cache-Control no-store.
/// </summary>
public static class DriverDispatchContactEndpoints
{
    public const string Path = "/api/v1/driver/me/dispatch-contact";

    public static IEndpointRouteBuilder MapDriverDispatchContactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, GetMyDispatchContact)
            .RequireAuthorization(OrganizationPolicies.ActiveOrganizationMember)
            .RequireTenantContext(StatusCodes.Status403Forbidden)
            .WithName("getMyDispatchContact")
            .WithTags("Driver")
            .Produces<DriverDispatchContactResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static IResult GetMyDispatchContact(
        HttpContext httpContext,
        IOrganizationRequestSession session,
        ITenantContext tenantContext,
        IDriverDispatchContactDirectory directory)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        if (!session.IsActive || session.UserId is null || !tenantContext.IsSelected)
        {
            return TenantCapabilityGate.Forbidden();
        }

        if (TenantCapabilityGate.Deny(session, tenantContext, TenantCapabilities.GetMyDispatchContact) is { } denied)
        {
            return denied;
        }

        return Results.Ok(new DriverDispatchContactResponse(directory.FindPhoneE164(tenantContext.OrganizationId)));
    }
}

/// <summary>AI-05 <c>DriverDispatchContact</c>: exactly <c>phone_e164</c>, null while none is configured.</summary>
public sealed record DriverDispatchContactResponse(
    [property: JsonPropertyName("phone_e164")] string? PhoneE164)
{
    /// <summary>The number is never printed.</summary>
    public override string ToString() =>
        $"DriverDispatchContactResponse {{ PhoneE164 = {(PhoneE164 is null ? "null" : "[redacted]")} }}";
}
