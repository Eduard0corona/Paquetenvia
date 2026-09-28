using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-002 API response counts by status class. The API logs no per-request line
/// (<c>Microsoft.AspNetCore</c> stays at Warning), so the 5xx alert and the status-class workbook
/// read one <see cref="TelemetryEvents.HttpStatusSummary"/> line per window instead. Only the
/// status class is counted: no path, query, route value, header, user or tenant is ever recorded.
/// Health probes (<c>/health/*</c>) are platform traffic and are not counted; 1xx (WebSocket
/// upgrades that ended) is counted with 2xx.
/// </summary>
public sealed class HttpStatusCounter
{
    private readonly object _gate = new();
    private long _status2xx;
    private long _status3xx;
    private long _status4xx;
    private long _status5xx;

    public void Record(int statusCode)
    {
        lock (_gate)
        {
            switch (statusCode)
            {
                case >= 500:
                    _status5xx++;
                    break;
                case >= 400:
                    _status4xx++;
                    break;
                case >= 300:
                    _status3xx++;
                    break;
                default:
                    _status2xx++;
                    break;
            }
        }
    }

    public HttpStatusSnapshot TakeSnapshot()
    {
        lock (_gate)
        {
            var snapshot = new HttpStatusSnapshot(_status2xx, _status3xx, _status4xx, _status5xx);
            _status2xx = _status3xx = _status4xx = _status5xx = 0;
            return snapshot;
        }
    }
}

public readonly record struct HttpStatusSnapshot(long Status2xx, long Status3xx, long Status4xx, long Status5xx)
{
    public long Total => Status2xx + Status3xx + Status4xx + Status5xx;
}

/// <summary>Writes one summary per <see cref="ReportInterval"/> in which the API answered anything.</summary>
public sealed class HttpStatusReporter(
    HttpStatusCounter counter,
    TimeProvider timeProvider,
    ILogger<HttpStatusReporter> logger) : BackgroundService
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReportInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Report((long)ReportInterval.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Report(windowSeconds: 0);
        }
    }

    /// <summary>Logs and resets the current counts; an empty window writes nothing.</summary>
    public void Report(long windowSeconds)
    {
        var snapshot = counter.TakeSnapshot();
        if (snapshot.Total == 0)
        {
            return;
        }

        logger.Log(
            snapshot.Status5xx > 0 ? LogLevel.Warning : LogLevel.Information,
            TelemetryEvents.HttpStatusSummary,
            "HTTP responses summary: total={Total} 2xx={Status2xx} 3xx={Status3xx} 4xx={Status4xx} 5xx={Status5xx} window_s={WindowSeconds}",
            snapshot.Total,
            snapshot.Status2xx,
            snapshot.Status3xx,
            snapshot.Status4xx,
            snapshot.Status5xx,
            windowSeconds);
    }
}

public static class HttpStatusTelemetryExtensions
{
    private static readonly PathString HealthPrefix = new("/health");

    public static IServiceCollection AddHttpStatusTelemetry(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<HttpStatusCounter>();
        services.AddHostedService<HttpStatusReporter>();
        return services;
    }

    /// <summary>
    /// Place first in the pipeline so the final status (including the 500 written by the exception
    /// handler) is counted; an exception that still escapes counts as 5xx.
    /// </summary>
    public static IApplicationBuilder UseHttpStatusTelemetry(this IApplicationBuilder app)
    {
        var counter = app.ApplicationServices.GetRequiredService<HttpStatusCounter>();
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(HealthPrefix, StringComparison.OrdinalIgnoreCase))
            {
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
                // A request the client abandoned has no response to classify.
                if (!escaped || !context.RequestAborted.IsCancellationRequested)
                {
                    counter.Record(escaped ? StatusCodes.Status500InternalServerError : context.Response.StatusCode);
                }
            }
        });
    }
}
