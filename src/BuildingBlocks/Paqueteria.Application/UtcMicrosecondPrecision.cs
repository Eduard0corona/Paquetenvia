namespace Paqueteria.Application;

public static class UtcMicrosecondPrecision
{
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must use the UTC offset.", nameof(value));
        }

        var canonicalTicks = value.UtcTicks - value.UtcTicks % 10;
        return new DateTimeOffset(canonicalTicks, TimeSpan.Zero);
    }

    public static bool AreEqual(DateTimeOffset left, DateTimeOffset right) =>
        Normalize(left) == Normalize(right);
}
