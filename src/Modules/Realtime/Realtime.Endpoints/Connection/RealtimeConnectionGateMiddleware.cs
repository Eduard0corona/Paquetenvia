using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Realtime.Application.Authorization;
using Realtime.Application.Configuration;
using Realtime.Endpoints.Hubs;

namespace Realtime.Endpoints.Connection;

public sealed class RealtimeConnectionGateMiddleware(RequestDelegate next)
{
    internal const string PrivateRequestItemKey = "Realtime.PrivateConnectionRequest";

    public async Task InvokeAsync(
        HttpContext context,
        IOptions<RealtimeOptions> options,
        IRealtimeConnectionAuthorizer authorizer,
        IAuthenticatedSession session)
    {
        var hubKind = GetHubKind(context.Request.Path);
        if (hubKind is null)
        {
            await next(context);
            return;
        }

        if (options.Value.Provider != RealtimeProviderKind.SignalR)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "Service unavailable.");
            return;
        }

        if (hubKind == "tracking")
        {
            await AuthorizeTrackingAsync(context, authorizer);
            return;
        }

        if (context.Items.ContainsKey(RealtimePrivateAccessTokenMiddleware.InvalidAccessTokenItemKey))
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized.");
            return;
        }

        if (!session.IsAuthenticated ||
            session.IdentityStatus != IdentityContextStatus.Active ||
            session.UserId is not { } userId ||
            userId == Guid.Empty ||
            !RealtimeOrganizationSelector.TryRead(context.Request, out var organizationId))
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized.");
            return;
        }

        context.Items[PrivateRequestItemKey] = new PrivateRealtimeConnectionRequest(
            userId,
            organizationId,
            session.MfaSatisfied,
            context.TraceIdentifier);
        await next(context);
    }

    private async Task AuthorizeTrackingAsync(
        HttpContext context,
        IRealtimeConnectionAuthorizer authorizer)
    {
        if (!TryReadTrackingToken(context.Request, out var token))
        {
            await WriteTrackingNotFoundAsync(context);
            return;
        }

        try
        {
            var result = await authorizer.AuthorizeTrackingAsync(token, context.RequestAborted);
            if (!result.IsAuthorized || result.Authorization is null)
            {
                await WriteTrackingNotFoundAsync(context);
                return;
            }

            context.Items[TrackingHub.AuthorizationItemKey] = result.Authorization;
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (RealtimeAuthorizationInfrastructureException)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "Service unavailable.");
        }
    }

    private static string? GetHubKind(PathString path)
    {
        if (path.StartsWithSegments(RealtimeEndpointDefaults.OperationsPath, StringComparison.Ordinal))
        {
            return "operations";
        }

        if (path.StartsWithSegments(RealtimeEndpointDefaults.DriverPath, StringComparison.Ordinal))
        {
            return "driver";
        }

        return path.StartsWithSegments(RealtimeEndpointDefaults.TrackingPath, StringComparison.Ordinal)
            ? "tracking"
            : null;
    }

    private static bool TryReadTrackingToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        var queryValues = request.Query["access_token"];
        var queryToken = queryValues.Count == 1 ? queryValues[0] : null;
        string? headerToken = null;
        if (request.Headers.TryGetValue("Authorization", out var headerValues) &&
            headerValues.Count == 1 &&
            AuthenticationHeaderValue.TryParse(headerValues[0], out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            headerToken = header.Parameter;
        }

        if (queryValues.Count > 1 ||
            queryToken is not null && headerToken is not null)
        {
            return false;
        }

        token = queryToken ?? headerToken ?? string.Empty;
        return token.Length > 0;
    }

    private static Task WriteTrackingNotFoundAsync(HttpContext context) =>
        WriteProblemAsync(context, StatusCodes.Status404NotFound, "Not Found");

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "about:blank",
                title,
                status = statusCode,
            }),
            context.RequestAborted);
    }
}
