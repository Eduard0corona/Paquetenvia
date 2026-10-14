using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003 phase 4 PR 6: the request span of a sampled API request. ASP.NET Core hosting writes no attribute on its
/// activity, so this middleware writes the allowlisted ones (method, route template, final status code) when the request
/// ends. A <c>/health/*</c> probe request is taken out of the export with its children (Q-PR6-1; OBS-002 excludes the
/// probes too). With the export off no request is sampled, so nothing is written.
/// </summary>
public static class RequestSpanEnrichment
{
    /// <summary>Call right after <c>UseTraceCorrelation</c>, before the status counter and the exception handler.</summary>
    public static IApplicationBuilder UseRequestSpanEnrichment(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (context, next) =>
        {
            var activity = context.Features.Get<IHttpActivityFeature>()?.Activity;
            if (activity is null || !activity.IsAllDataRequested)
            {
                await next(context);
                return;
            }

            if (context.Request.Path.StartsWithSegments(TelemetryExportPolicy.ProbePathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                TelemetryExportPolicy.ExcludeFromExport(activity);
                await next(context);
                return;
            }

            var escaped = true;
            try
            {
                await next(context);
                escaped = false;
            }
            finally
            {
                // An exception that escapes the pipeline becomes a 500 when hosting answers it, as OBS-002 counts it.
                var status = escaped && !context.Response.HasStarted
                    ? StatusCodes.Status500InternalServerError
                    : context.Response.StatusCode;
                TelemetryExportPolicy.EnrichRequest(activity, context.Request.Method, RouteTemplate(context), status);
            }
        });
    }

    private static string? RouteTemplate(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText is { Length: > 0 } template
            ? template.StartsWith('/') ? template : "/" + template
            : null;
}
