using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Identity.Endpoints.Security;

internal static class IdentityProblemDetails
{
    internal static Task WriteAsync(HttpContext context, int statusCode, string title)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = statusCode;
        return Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["traceId"] = context.TraceIdentifier,
            }).ExecuteAsync(context);
    }
}

public sealed class IdentityAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            return IdentityProblemDetails.WriteAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized");
        }

        if (authorizeResult.Forbidden)
        {
            return IdentityProblemDetails.WriteAsync(context, StatusCodes.Status403Forbidden, "Forbidden");
        }

        return _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
