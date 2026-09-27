using Identity.Endpoints.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Identity.Endpoints.Security;

internal static class IdentityProblemDetails
{
    /// <summary>
    /// Problem-details <c>code</c> of a 403 whose only unmet requirement is MFA
    /// (AUTH-001-MFA-STEP-UP). The web answers it with "Verificar identidad", which starts
    /// <c>/auth/login?mfa=required</c>. Every other 403 stays generic.
    /// </summary>
    public const string MfaRequiredCode = "MFA_REQUIRED";

    internal static Task WriteAsync(HttpContext context, int statusCode, string title, string? code = null)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = context.TraceIdentifier,
        };
        if (code is not null)
        {
            extensions["code"] = code;
        }

        context.Response.StatusCode = statusCode;
        return Results.Problem(statusCode: statusCode, title: title, extensions: extensions).ExecuteAsync(context);
    }

    /// <summary>
    /// True when the actor satisfied every requirement of the policy except MFA, so revealing that
    /// a second factor is missing discloses nothing the actor could not already infer.
    /// </summary>
    internal static bool IsOnlyMissingMfa(AuthorizationFailure? failure) =>
        failure is { FailCalled: false } &&
        failure.FailedRequirements.Any() &&
        failure.FailedRequirements.All(requirement => requirement is RequireMfaRequirement);
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
            return IdentityProblemDetails.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                IdentityProblemDetails.IsOnlyMissingMfa(authorizeResult.AuthorizationFailure)
                    ? IdentityProblemDetails.MfaRequiredCode
                    : null);
        }

        return _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
