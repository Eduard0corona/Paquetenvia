using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-002 outbox lane summary. Each dispatcher reports what it claimed and how each message was
/// settled; once per <see cref="ReportInterval"/> the lane writes one structured
/// <see cref="TelemetryEvents.OutboxLaneSummary"/> line with counts only, also when it was idle, so
/// a missing line means a stalled loop.
/// </summary>
/// <remarks>
/// It never reads the outbox: the claim age is measured on the rows the approved claim functions
/// already returned (<c>now - min(available_at)</c> of each claimed batch), which is the time the
/// oldest ready message waited. A backlog that is never claimed shows up as a missing summary or as
/// loop failures, not as an age. The dispatcher loop drives <see cref="ReportIfDue"/>, so a blocked
/// loop stops its heartbeat instead of reporting zeros.
/// </remarks>
public sealed class OutboxLaneMonitor(ILogger<OutboxLaneMonitor> logger, TimeProvider timeProvider)
{
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, LaneWindow> _lanes = new(StringComparer.Ordinal);

    public void Claimed(string lane, int count, DateTimeOffset? oldestAvailableAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var window = Window(lane);
        if (count == 0)
        {
            return;
        }

        var ageMs = oldestAvailableAt is { } oldest
            ? Math.Max(0L, (long)(timeProvider.GetUtcNow() - oldest).TotalMilliseconds)
            : 0L;
        lock (window)
        {
            window.Claimed += count;
            window.MaxClaimAgeMs = Math.Max(window.MaxClaimAgeMs, ageMs);
        }
    }

    public void Settled(string lane, OutboxSettlement settlement)
    {
        var window = Window(lane);
        lock (window)
        {
            switch (settlement)
            {
                case OutboxSettlement.Processed:
                    window.Processed++;
                    break;
                case OutboxSettlement.Retry:
                    window.Retry++;
                    break;
                case OutboxSettlement.Dead:
                    window.Dead++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(settlement), settlement, "Unmapped outbox settlement.");
            }
        }
    }

    public void LoopFailed(string lane)
    {
        var window = Window(lane);
        lock (window)
        {
            window.LoopFailures++;
        }
    }

    /// <summary>Writes and resets the lane summary once its window has elapsed; otherwise does nothing.</summary>
    public void ReportIfDue(string lane)
    {
        var window = Window(lane);
        var now = timeProvider.GetUtcNow();
        long claimed, processed, retry, dead, loopFailures, maxClaimAgeMs, windowSeconds;
        lock (window)
        {
            if (now - window.StartedAt < ReportInterval)
            {
                return;
            }

            (claimed, processed, retry, dead, loopFailures, maxClaimAgeMs) =
                (window.Claimed, window.Processed, window.Retry, window.Dead, window.LoopFailures, window.MaxClaimAgeMs);
            windowSeconds = (long)(now - window.StartedAt).TotalSeconds;
            window.Reset(now);
        }

        logger.Log(
            dead > 0 || loopFailures > 0 ? LogLevel.Warning : LogLevel.Information,
            TelemetryEvents.OutboxLaneSummary,
            "Outbox lane {Lane} summary: claimed={Claimed} processed={Processed} retry={Retry} dead={Dead} loop_failures={LoopFailures} max_claim_age_ms={MaxClaimAgeMs} window_s={WindowSeconds}",
            lane,
            claimed,
            processed,
            retry,
            dead,
            loopFailures,
            maxClaimAgeMs,
            windowSeconds);
    }

    private LaneWindow Window(string lane)
    {
        if (!OutboxLanes.All.Contains(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "Outbox lane is not in the OBS-002 allowlist.");
        }

        return _lanes.GetOrAdd(lane, static (_, started) => new LaneWindow(started), timeProvider.GetUtcNow());
    }

    private sealed class LaneWindow(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; private set; } = startedAt;
        public long Claimed { get; set; }
        public long Processed { get; set; }
        public long Retry { get; set; }
        public long Dead { get; set; }
        public long LoopFailures { get; set; }
        public long MaxClaimAgeMs { get; set; }

        public void Reset(DateTimeOffset now)
        {
            StartedAt = now;
            Claimed = Processed = Retry = Dead = LoopFailures = MaxClaimAgeMs = 0;
        }
    }
}

public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>Registers the shared <see cref="OutboxLaneMonitor"/> once per host.</summary>
    public static IServiceCollection AddOutboxLaneMonitor(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxLaneMonitor>();
        return services;
    }
}
