using System.Globalization;
using System.Text;

namespace Orders.Application.Orders;

public static class OrderCursorCodec
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAt.UtcTicks}:{id:D}");
        return ToBase64Url(Encoding.UTF8.GetBytes(value));
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(cursor));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(text.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(text.AsSpan(separator + 1), "D", out id) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid Base64URL."),
        };
        return Convert.FromBase64String(base64);
    }
}
