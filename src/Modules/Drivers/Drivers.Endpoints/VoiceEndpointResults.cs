using Microsoft.AspNetCore.Http;
using Organizations.Application.Session;
using Paqueteria.Application.Tenancy;

namespace Drivers.Endpoints;

/// <summary>
/// VOICE-001: the session check and the problem answers shared by the driver phone, recipient call and webhook
/// routes. Every problem carries only a fixed title (and, for 409, a fixed code): never a phone, a provider message or
/// an exception text.
/// </summary>
internal static class VoiceEndpointResults
{
    public const string InvalidRequestCode = "INVALID_REQUEST";

    /// <summary>An active session with a selected organization; the actor is its user.</summary>
    public static bool TrySession(IOrganizationRequestSession session, ITenantContext tenantContext, out Guid actorId)
    {
        actorId = default;
        if (!session.IsActive || session.UserId is not { } id || !tenantContext.IsSelected)
        {
            return false;
        }

        actorId = id;
        return true;
    }

    public static IResult BadRequest() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request.");

    public static IResult Conflict(string code = InvalidRequestCode) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Conflict.",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden.");

    public static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found.");

    public static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service unavailable.");
}
