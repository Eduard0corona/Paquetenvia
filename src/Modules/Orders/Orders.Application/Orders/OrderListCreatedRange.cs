using System.Globalization;

namespace Orders.Application.Orders;

/// <summary>
/// ORD-LIST-DATE-FILTER-2026-10-12: the optional creation window of listOrders (<c>created_from</c>,
/// <c>created_to</c>), with the bounds of the parameters of the same name of the OBS-001 operations dashboard. Both
/// bounds are inclusive (<c>created_from &lt;= orders.created_at &lt;= created_to</c>), each may be used alone, and when
/// both are given <c>created_from</c> is not after <c>created_to</c> and at most <see cref="MaximumSpan"/> lies between
/// them. The web sends the first instant of the first Mazatlán calendar day and the last microsecond of the last one.
/// </summary>
public readonly record struct OrderListCreatedRange(DateTimeOffset? From, DateTimeOffset? To)
{
    /// <summary>The dashboard's maximum window, 31 days.</summary>
    public static readonly TimeSpan MaximumSpan = TimeSpan.FromDays(31);

    /// <summary>
    /// The only spellings accepted: RFC3339 date-time in UTC, <c>T</c> and <c>Z</c> in upper case, whole seconds and
    /// 0 to 7 fractional digits (the 100 ns resolution of <see cref="DateTimeOffset"/>, so nothing is rounded).
    /// </summary>
    private static readonly string[] InstantFormats =
    [
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'f'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'ff'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'ffff'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffff'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'ffffff'Z'",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'",
    ];

    public bool IsValid =>
        From is null || To is null || (From.Value <= To.Value && To.Value - From.Value <= MaximumSpan);

    /// <summary>
    /// A strict RFC3339 UTC instant (<c>2026-10-01T07:00:00Z</c>, <c>2026-10-08T06:59:59.999999Z</c>). Any other text
    /// - a date alone, an offset, lower-case designators, surrounding spaces, other cultures' formats or more than 7
    /// fractional digits - is not an instant. Unlike the dashboard's lenient <c>TryUtc</c>, nothing is guessed.
    /// </summary>
    public static bool TryParseInstant(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null ||
            !DateTimeOffset.TryParseExact(
                text,
                InstantFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
