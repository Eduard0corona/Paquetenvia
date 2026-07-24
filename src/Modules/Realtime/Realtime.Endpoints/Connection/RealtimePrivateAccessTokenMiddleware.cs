using Microsoft.AspNetCore.Http;
using Realtime.Endpoints.Hubs;

namespace Realtime.Endpoints.Connection;

public sealed class RealtimePrivateAccessTokenMiddleware(RequestDelegate next)
{
    internal const string InvalidAccessTokenItemKey =
        "Realtime.PrivateAccessToken.Invalid";

    public Task InvokeAsync(HttpContext context)
    {
        if (!IsPrivateHub(context.Request.Path))
        {
            return next(context);
        }

        var queryValues = context.Request.Query["access_token"];
        if (queryValues.Count == 0)
        {
            return next(context);
        }

        var value = queryValues.Count == 1 ? queryValues[0] : null;
        if (context.Request.Headers.ContainsKey("Authorization") ||
            string.IsNullOrEmpty(value) ||
            value.Any(char.IsWhiteSpace))
        {
            context.Items[InvalidAccessTokenItemKey] = true;
            return next(context);
        }

        context.Request.Headers.Authorization = $"Bearer {value}";
        return next(context);
    }

    private static bool IsPrivateHub(PathString path) =>
        path.StartsWithSegments(RealtimeEndpointDefaults.OperationsPath, StringComparison.Ordinal) ||
        path.StartsWithSegments(RealtimeEndpointDefaults.DriverPath, StringComparison.Ordinal);
}
