namespace Drivers.Domain.Voice;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11: when a driver may ask the platform to call the recipient through the masked
/// bridge. Only the delivery recipient, and only while the order is <c>DELIVERING</c> (the driver is at or near the
/// destination and is about to deliver); every other state, the pickup contact included, is out of scope.
/// </summary>
public static class RecipientCallPolicy
{
    public const string CallableOrderStatus = "DELIVERING";

    /// <summary>The assignment states of the driver's own current assignment (DSP-002, EXT-001).</summary>
    public static IReadOnlyList<string> CurrentAssignmentStatuses { get; } = ["ACCEPTED", "ACTIVE"];

    public static bool IsCallable(string? orderStatus) =>
        string.Equals(orderStatus, CallableOrderStatus, StringComparison.Ordinal);
}

/// <summary>
/// Rate limits that bound the cost of the bridge and the attempts against one recipient. Only requests that may have
/// placed a call count (REQUESTED, PLACED, UNCONFIRMED); a FAILED request placed nothing.
/// </summary>
public sealed record RecipientCallLimits(int MaximumPerOrder, TimeSpan OrderWindow, int MaximumPerDriverPerHour)
{
    /// <summary>
    /// The delay until a new request fits, or <see langword="null"/> when it fits now. <paramref name="orderRequests"/>
    /// are the counted requests of this driver for this order inside the order window; <paramref name="driverRequests"/>
    /// the counted requests of this driver in the last hour; both newest first.
    /// </summary>
    public TimeSpan? RetryAfter(
        IReadOnlyList<DateTimeOffset> orderRequests,
        IReadOnlyList<DateTimeOffset> driverRequests,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(orderRequests);
        ArgumentNullException.ThrowIfNull(driverRequests);
        TimeSpan? delay = null;
        if (orderRequests.Count >= MaximumPerOrder)
        {
            delay = Max(delay, orderRequests[MaximumPerOrder - 1] + OrderWindow - now);
        }

        if (driverRequests.Count >= MaximumPerDriverPerHour)
        {
            delay = Max(delay, driverRequests[MaximumPerDriverPerHour - 1] + TimeSpan.FromHours(1) - now);
        }

        return delay is { } value ? (value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value) : null;
    }

    private static TimeSpan? Max(TimeSpan? current, TimeSpan candidate) =>
        current is { } value && value >= candidate ? value : candidate;
}
