using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paqueteria.Application.Security;

namespace Paqueteria.Infrastructure.Security;

public static class RequestTargetRedactionServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the host <see cref="IHttpContextFactory"/> with the one that applies every registered
    /// <see cref="IRequestTargetRedactor"/> before hosting diagnostics read the request target.
    /// </summary>
    public static IServiceCollection AddRequestTargetRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IHttpContextFactory>();
        services.AddSingleton<IHttpContextFactory, RequestTargetRedactingHttpContextFactory>();
        return services;
    }
}

/// <summary>
/// Rewrites secrets out of the request target before ASP.NET Core hosting reads it.
/// </summary>
/// <remarks>
/// <c>HostingApplication.CreateContext</c> calls <see cref="IHttpContextFactory.Create"/> and then
/// <c>BeginRequest</c>, which captures the path and query in the RequestPath scope, the "Request starting" and
/// "Request finished" messages and the request activity. Middleware runs later, so it cannot protect those.
/// Hosting reuses a Kestrel connection's pooled HttpContext without calling Create only when the factory is
/// <see cref="DefaultHttpContextFactory"/> itself; this factory therefore runs on every request, including
/// keep-alive and HTTP/2 connections. It wraps (does not derive from) the default factory, so the context lifecycle
/// (IHttpContextAccessor, RequestServices scope, form options) stays the default one.
/// </remarks>
public sealed class RequestTargetRedactingHttpContextFactory(
    IServiceProvider services,
    IEnumerable<IRequestTargetRedactor> redactors) : IHttpContextFactory
{
    private readonly DefaultHttpContextFactory inner = new(services);
    private readonly IRequestTargetRedactor[] redactors = redactors.ToArray();

    public HttpContext Create(IFeatureCollection featureCollection)
    {
        ArgumentNullException.ThrowIfNull(featureCollection);
        Redact(featureCollection);
        return inner.Create(featureCollection);
    }

    public void Dispose(HttpContext httpContext) => inner.Dispose(httpContext);

    private void Redact(IFeatureCollection features)
    {
        var request = features.Get<IHttpRequestFeature>();
        if (request is null || redactors.Length == 0)
        {
            return;
        }

        var path = request.Path;
        var query = request.QueryString;
        var changed = false;
        foreach (var redactor in redactors)
        {
            var redaction = redactor.Redact(path, query);
            if (redaction is null)
            {
                continue;
            }

            features[redaction.FeatureType] = redaction.Feature;
            changed |= !string.Equals(path, redaction.Path, StringComparison.Ordinal)
                || !string.Equals(query, redaction.QueryString, StringComparison.Ordinal);
            path = redaction.Path;
            query = redaction.QueryString;
        }

        if (!changed)
        {
            return;
        }

        request.Path = path;
        request.QueryString = query;
        request.RawTarget = new PathString(request.PathBase).Add(new PathString(path)).ToUriComponent() + query;
    }
}
