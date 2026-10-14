namespace Paqueteria.Application.Scaling;

/// <summary>
/// SCL-007 adaptive outbox wait: the wait of one outbox lane between two claims. Each lane loop owns its own instance,
/// so lanes wait independently. It serves the API's realtime business and location lanes
/// (SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13) and the Worker's Notifications and Dispatch assignment lifecycle lanes
/// (SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A claim that returned work, a full batch or a partial one, starts the progression over and the lane claims
/// again at once, as it did before the adaptive wait.</item>
/// <item>A claim that returned nothing waits the current step, starting at the lane's poll interval; each further
/// empty claim doubles it, up to the lane's maximum idle wait.</item>
/// </list>
/// With the defaults the realtime business lane and both Worker lanes wait 250 ms, 500 ms, then 1 s, and the realtime
/// location lane 500 ms, then 1 s. The wait only spaces calls to the existing claim functions; it reads nothing and
/// writes nothing.
/// </remarks>
public sealed class OutboxIdleWait
{
    /// <summary>
    /// The longest maximum idle wait the Worker lanes accept, 1 s: the owner's value for them
    /// (SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14, "1 segundo en ambas"), the same as the realtime lanes' ceiling. A longer
    /// wait needs a new owner decision, not a configuration change.
    /// </summary>
    public const int MaximumIdleWaitCeilingMilliseconds = 1_000;

    private TimeSpan _next;

    public OutboxIdleWait(int pollIntervalMilliseconds, int maximumIdleWaitMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pollIntervalMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumIdleWaitMilliseconds, pollIntervalMilliseconds);
        Shortest = TimeSpan.FromMilliseconds(pollIntervalMilliseconds);
        Longest = TimeSpan.FromMilliseconds(maximumIdleWaitMilliseconds);
        _next = Shortest;
    }

    /// <summary>The first wait after work, the configured poll interval.</summary>
    public TimeSpan Shortest { get; }

    /// <summary>The longest wait, the configured maximum idle wait.</summary>
    public TimeSpan Longest { get; }

    /// <summary>
    /// The bounds of a lane's maximum idle wait: from the lane's poll interval (the progression starts there, and a
    /// lower maximum would make the wait shrink) up to <see cref="MaximumIdleWaitCeilingMilliseconds"/>. A lane's
    /// options validator refuses the start of its host outside them.
    /// </summary>
    public static bool IsValidMaximum(int pollIntervalMilliseconds, int maximumIdleWaitMilliseconds) =>
        maximumIdleWaitMilliseconds >= pollIntervalMilliseconds &&
        maximumIdleWaitMilliseconds <= MaximumIdleWaitCeilingMilliseconds;

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
