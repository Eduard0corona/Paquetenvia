using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>OBS-003 wiring for the API and Worker hosts (see <see cref="TraceCorrelation"/>).</summary>
public static class TraceCorrelationServiceCollectionExtensions
{
    /// <summary>
    /// Makes ASP.NET Core hosting ignore inbound trace headers (the propagator it resolves from DI) and adds the
    /// trace id to every log line written inside a trace. Call after the host registered logging.
    /// </summary>
    public static IServiceCollection AddTraceCorrelation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        TraceCorrelation.ConfigureProcess();
        services.Replace(ServiceDescriptor.Singleton<DistributedContextPropagator>(NoTraceContextPropagator.Instance));
        services.Decorate<ILoggerFactory>(
            static (sp, descriptor) => descriptor.ImplementationType == typeof(LoggerFactory)
                ? new LoggerFactory(
                    sp.GetServices<ILoggerProvider>(),
                    sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
                    sp.GetService<IOptions<LoggerFactoryOptions>>(),
                    sp.GetService<IExternalScopeProvider>())
                : null,
            static inner => inner is TraceCorrelatedLoggerFactory ? inner : new TraceCorrelatedLoggerFactory(inner));
        return services;
    }

    /// <summary>
    /// Every <c>application/problem+json</c> written through <see cref="IProblemDetailsService"/> carries
    /// <c>traceId</c>, the 32-hex W3C trace id of the request (AI-05 <c>Problem</c>), including the fallback that
    /// writes the problem when the request's <c>Accept</c> excludes JSON. The framework default (the full
    /// <c>Activity.Id</c> or the Kestrel connection id) never reaches a response.
    /// </summary>
    public static IServiceCollection AddProblemDetailsTraceId(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProblemDetails();
        services.PostConfigure<ProblemDetailsOptions>(options =>
        {
            var previous = options.CustomizeProblemDetails;
            options.CustomizeProblemDetails = context =>
            {
                previous?.Invoke(context);
                HttpTraceCorrelation.ApplyTraceId(context);
            };
        });
        services.Decorate<IProblemDetailsService>(
            static (_, _) => null,
            static inner => inner is TraceIdProblemDetailsService ? inner : new TraceIdProblemDetailsService(inner));
        return services;
    }

    /// <summary>
    /// Replaces the last registration of <typeparamref name="TService"/> with <paramref name="decorate"/> applied
    /// to the instance the original registration builds (<paramref name="create"/> may build it itself).
    /// </summary>
    private static void Decorate<TService>(
        this IServiceCollection services,
        Func<IServiceProvider, ServiceDescriptor, TService?> create,
        Func<TService, TService> decorate)
        where TService : class
    {
        var descriptor = services.LastOrDefault(item => item.ServiceType == typeof(TService) && !item.IsKeyedService)
            ?? throw new InvalidOperationException($"{typeof(TService).Name} must be registered before OBS-003 decorates it.");
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(TService),
            provider => decorate(create(provider, descriptor) ?? Build<TService>(provider, descriptor)),
            descriptor.Lifetime));
    }

    private static TService Build<TService>(IServiceProvider provider, ServiceDescriptor descriptor)
        where TService : class =>
        (TService)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(
                provider,
                descriptor.ImplementationType
                    ?? throw new InvalidOperationException($"{typeof(TService).Name} has no implementation to decorate.")));
}

/// <summary>OBS-003 request correlation for the API pipeline.</summary>
public static class HttpTraceCorrelation
{
    /// <summary>
    /// Must run first. Sets <see cref="HttpContext.TraceIdentifier"/> to the 32-hex trace id of the request
    /// activity that hosting started (a new trace, since inbound headers are ignored), so every existing reader of
    /// <c>TraceIdentifier</c> (audit <c>request_id</c>, problem writers, the tenant activation audit) uses the one
    /// identifier. If hosting started no W3C activity, the request runs in a new root activity of its own.
    /// </summary>
    public static IApplicationBuilder UseTraceCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static (context, next) =>
        {
            var activity = context.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current;
            if (TraceCorrelation.TraceIdOf(activity) is { } traceId)
            {
                context.TraceIdentifier = traceId;
                return next(context);
            }

            return RunInOwnTraceAsync(context, next);
        });
    }

    /// <summary>The trace id of the request, or null when none can be read (never another format).</summary>
    public static string? TraceIdOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TraceCorrelation.IsTraceId(context.TraceIdentifier)
            ? context.TraceIdentifier
            : TraceCorrelation.TraceIdOf(context.Features.Get<IHttpActivityFeature>()?.Activity ?? Activity.Current);
    }

    internal static void ApplyTraceId(ProblemDetailsContext context)
    {
        if (TraceIdOf(context.HttpContext) is { } traceId)
        {
            context.ProblemDetails.Extensions[TraceCorrelation.ProblemMember] = traceId;
        }
        else
        {
            context.ProblemDetails.Extensions.Remove(TraceCorrelation.ProblemMember);
        }
    }

    private static async Task RunInOwnTraceAsync(HttpContext context, RequestDelegate next)
    {
        using var activity = TraceCorrelation.StartRootActivity(TraceCorrelation.RequestOperation);
        context.TraceIdentifier = activity.TraceId.ToHexString();
        await next(context);
    }
}

/// <summary>
/// Sets the trace id before the inner service runs: the default writer then resets it to the framework value and
/// <c>CustomizeProblemDetails</c> restores it, and the result's own fallback (no writer accepted the request's
/// <c>Accept</c>) serializes this same problem with it.
/// </summary>
internal sealed class TraceIdProblemDetailsService(IProblemDetailsService inner) : IProblemDetailsService
{
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        HttpTraceCorrelation.ApplyTraceId(context);
        return inner.WriteAsync(context);
    }

    public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
    {
        HttpTraceCorrelation.ApplyTraceId(context);
        return inner.TryWriteAsync(context);
    }
}
