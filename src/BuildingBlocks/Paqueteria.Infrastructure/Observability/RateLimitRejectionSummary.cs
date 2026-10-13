using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-002 rate-limit rejections per policy (SEC-004, owner answer Q8, "Resumen en los logs"): the API exports no
/// metric before phase 4 PR 6, so the rejections each limit made are read from one
/// <see cref="TelemetryEvents.RateLimitSummary"/> line per policy and window instead. It accepts only the names it
/// was built with, the rate-limit registry's fixed policy names, and counts nothing else: no partition key, subject,
/// address, tenant or path ever reaches it.
/// </summary>
public sealed class RateLimitRejectionSummary
{
    private readonly FrozenDictionary<string, Window> _windows;

    public RateLimitRejectionSummary(IEnumerable<string> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _windows = policies.ToFrozenDictionary(static policy => policy, static _ => new Window(), StringComparer.Ordinal);
        if (_windows.Count == 0)
        {
            throw new ArgumentException("At least one rate-limit policy is required.", nameof(policies));
        }
    }

    /// <summary>The policy names this summary reports: the registry's, fixed at start.</summary>
    public IReadOnlyCollection<string> Policies => _windows.Keys;

    /// <summary>Counts one rejection; a name outside the registry is refused.</summary>
    public void Record(string policy)
    {
        if (policy is null || !_windows.TryGetValue(policy, out var window))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "The policy is not a registered rate-limit policy.");
        }

        Interlocked.Increment(ref window.Rejected);
    }

    /// <summary>The rejections per policy since the last snapshot, in name order, with zeros; resets them.</summary>
    public IReadOnlyList<(string Policy, long Rejected)> TakeSnapshot() =>
        _windows
            .OrderBy(static window => window.Key, StringComparer.Ordinal)
            .Select(static window => (window.Key, Interlocked.Exchange(ref window.Value.Rejected, 0)))
            .ToArray();

    private sealed class Window
    {
        public long Rejected;
    }
}

/// <summary>
/// Writes, once per <see cref="ReportInterval"/>, one summary line for each policy that rejected at least one request
/// in the window. A window without rejections writes nothing, like <see cref="HttpStatusReporter"/>.
/// </summary>
public sealed class RateLimitSummaryReporter(
    RateLimitRejectionSummary summary,
    TimeProvider timeProvider,
    ILogger<RateLimitSummaryReporter> logger) : BackgroundService
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

    /// <summary>Logs and resets the current counts; a policy without rejections writes nothing.</summary>
    public void Report(long windowSeconds)
    {
        foreach (var (policy, rejected) in summary.TakeSnapshot())
        {
            if (rejected == 0)
            {
                continue;
            }

            logger.Log(
                LogLevel.Information,
                TelemetryEvents.RateLimitSummary,
                "Rate limit {Policy} summary: rejected={Rejected} window_s={WindowSeconds}",
                policy,
                rejected,
                windowSeconds);
        }
    }
}

public static class RateLimitRejectionSummaryExtensions
{
    /// <summary>
    /// Registers the summary for exactly <paramref name="policies"/>, the rate-limit registry's names, and its
    /// per-minute reporter. Only the registry calls it (<c>AddPaqueteriaRateLimiting</c>).
    /// </summary>
    public static IServiceCollection AddRateLimitRejectionSummary(
        this IServiceCollection services,
        IEnumerable<string> policies)
    {
        ArgumentNullException.ThrowIfNull(services);
        var summary = new RateLimitRejectionSummary(policies);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(summary);
        services.AddHostedService<RateLimitSummaryReporter>();
        return services;
    }
}
