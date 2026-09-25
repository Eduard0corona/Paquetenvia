namespace Finance.Domain.Settlements;

/// <summary>
/// A settlement period of whole operational days. The dates are local calendar dates of the operational
/// time zone; the instants are the UTC boundaries timestamps are compared against: the start of
/// <see cref="From"/> inclusive and the start of the day after <see cref="To"/> exclusive.
/// </summary>
public sealed record SettlementPeriod(
    DateOnly From,
    DateOnly To,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsBeforeUtc)
{
    /// <summary>The longest period one settlement may cover, inclusive of both ends.</summary>
    public const int MaximumDays = 366;

    public static bool TryResolve(DateOnly from, DateOnly to, TimeZoneInfo zone, out SettlementPeriod period)
    {
        ArgumentNullException.ThrowIfNull(zone);
        period = null!;
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaximumDays || to == DateOnly.MaxValue)
        {
            return false;
        }

        if (!TryStartOfDay(from, zone, out var starts) || !TryStartOfDay(to.AddDays(1), zone, out var ends))
        {
            return false;
        }

        period = new(from, to, starts, ends);
        return true;
    }

    /// <summary>
    /// The first UTC instant whose local date in <paramref name="zone"/> is <paramref name="date"/>. A daylight
    /// saving gap at midnight moves the start to the first local time that exists, and an ambiguous midnight
    /// resolves to its earlier occurrence, so consecutive days never overlap and never leave a gap.
    /// </summary>
    public static bool TryStartOfDay(DateOnly date, TimeZoneInfo zone, out DateTimeOffset startsAtUtc)
    {
        ArgumentNullException.ThrowIfNull(zone);
        startsAtUtc = default;
        try
        {
            var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var limit = local.AddDays(1);
            while (zone.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
                if (local >= limit)
                {
                    return false;
                }
            }

            var offset = zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).Max()
                : zone.GetUtcOffset(local);
            startsAtUtc = new DateTimeOffset(local, offset).ToUniversalTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
