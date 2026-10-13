using Realtime.Application.Configuration;

namespace Realtime.Application.Dispatching;

/// <summary>
/// SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13: the wait of one realtime outbox lane between two claims. Each lane loop owns
/// its own instance, so the business and location lanes wait independently.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A claim that returned work, a full batch or a partial one, starts the progression over and the lane claims
/// again at once, as it did before the adaptive wait.</item>
/// <item>A claim that returned nothing waits the current step, starting at
/// <see cref="OutboxLaneOptions.PollIntervalMilliseconds"/>; each further empty claim doubles it, up to
/// <see cref="OutboxLaneOptions.MaximumIdleWaitMilliseconds"/>.</item>
/// </list>
/// With the defaults the business lane waits 250 ms, 500 ms, then 1 s, and the location lane 500 ms, then 1 s. The
/// wait only spaces calls to the existing claim function; it reads nothing and writes nothing.
/// </remarks>
public sealed class OutboxIdleWait
{
    private TimeSpan _next;

    public OutboxIdleWait(OutboxLaneOptions lane)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentOutOfRangeException.ThrowIfLessThan(lane.PollIntervalMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            lane.MaximumIdleWaitMilliseconds,
            lane.PollIntervalMilliseconds);
        Shortest = TimeSpan.FromMilliseconds(lane.PollIntervalMilliseconds);
        Longest = TimeSpan.FromMilliseconds(lane.MaximumIdleWaitMilliseconds);
        _next = Shortest;
    }

    /// <summary>The first wait after work, the configured poll interval.</summary>
    public TimeSpan Shortest { get; }

    /// <summary>The longest wait, the configured maximum idle wait.</summary>
    public TimeSpan Longest { get; }

    /// <summary>
    /// How long the lane waits before its next claim, given how many messages the last claim returned. Zero means
    /// claim again at once.
    /// </summary>
    public TimeSpan After(int claimed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(claimed);
        if (claimed > 0)
        {
            _next = Shortest;
            return TimeSpan.Zero;
        }

        var wait = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, Longest.Ticks));
        return wait;
    }
}
