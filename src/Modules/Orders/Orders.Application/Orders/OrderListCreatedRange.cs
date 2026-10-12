using System.Globalization;

namespace Orders.Application.Orders;

/// <summary>
/// ORD-LIST-DATE-FILTER-2026-10-12: the optional creation window of listOrders (<c>created_from</c>,
/// <c>created_to</c>), mirroring the parameters of the same name of the OBS-001 operations dashboard. Both bounds are
/// inclusive (<c>created_from &lt;= orders.created_at &lt;= created_to</c>), each may be used alone, and when both are
/// given <c>created_from</c> is not after <c>created_to</c> and at most <see cref="MaximumSpan"/> lies between them.
/// The web sends the first instant of the first Mazatlán calendar day and the last microsecond of the last one.
/// </summary>
public readonly record struct OrderListCreatedRange(DateTimeOffset? From, DateTimeOffset? To)
{
    /// <summary>The dashboard's maximum window, 31 days.</summary>
    public static readonly TimeSpan MaximumSpan = TimeSpan.FromDays(31);

    public bool IsValid =>
        From is null || To is null || (From.Value <= To.Value && To.Value - From.Value <= MaximumSpan);

    /// <summary>
    /// The dashboard's <c>TryUtc</c> rule: an RFC3339 instant written in UTC, ending in <c>Z</c>. Any other text,
    /// offset or empty value is not an instant.
    /// </summary>
    public static bool TryParseInstant(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null ||
            !text.EndsWith('Z') ||
            !DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
