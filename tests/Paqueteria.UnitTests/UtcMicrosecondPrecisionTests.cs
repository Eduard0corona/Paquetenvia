using Paqueteria.Application;

namespace Paqueteria.UnitTests;

public sealed class UtcMicrosecondPrecisionTests
{
    private static readonly DateTimeOffset Base =
        DateTimeOffset.Parse(
            "2026-07-25T18:00:00.1234560Z",
            System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Aligned_value_remains_identical()
    {
        var canonical = UtcMicrosecondPrecision.Normalize(Base);

        Assert.Equal(Base, canonical);
        Assert.Equal(TimeSpan.Zero, canonical.Offset);
        Assert.Equal(0, canonical.UtcTicks % 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Seventh_fractional_digit_is_truncated_without_rounding(int ticks)
    {
        var canonical = UtcMicrosecondPrecision.Normalize(Base.AddTicks(ticks));

        Assert.Equal(Base, canonical);
    }

    [Fact]
    public void Last_tick_of_second_does_not_round_into_the_next_second()
    {
        var original = DateTimeOffset.Parse(
            "2026-07-25T18:00:00.9999999Z",
            System.Globalization.CultureInfo.InvariantCulture);

        var canonical = UtcMicrosecondPrecision.Normalize(original);

        Assert.Equal(
            DateTimeOffset.Parse(
                "2026-07-25T18:00:00.9999990Z",
                System.Globalization.CultureInfo.InvariantCulture),
            canonical);
    }

    [Fact]
    public void Normalization_is_idempotent_monotonic_and_less_than_one_microsecond_behind()
    {
        var values = new[]
        {
            Base.AddTicks(1),
            Base.AddTicks(9),
            Base.AddTicks(10),
            Base.AddSeconds(1).AddTicks(9),
        };

        var normalized = values.Select(UtcMicrosecondPrecision.Normalize).ToArray();

        Assert.Equal(normalized, normalized.Order());
        Assert.All(values.Zip(normalized), pair =>
        {
            var difference = pair.First - pair.Second;
            Assert.InRange(difference, TimeSpan.Zero, TimeSpan.FromTicks(9));
            Assert.Equal(pair.Second, UtcMicrosecondPrecision.Normalize(pair.Second));
        });
    }

    [Fact]
    public void Non_utc_offset_is_rejected()
    {
        var localOffset = DateTimeOffset.Parse(
            "2026-07-25T11:00:00.1234567-07:00",
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Throws<ArgumentException>(() => UtcMicrosecondPrecision.Normalize(localOffset));
    }

    [Fact]
    public void Equality_uses_the_canonical_microsecond_and_rejects_the_next_one()
    {
        Assert.True(UtcMicrosecondPrecision.AreEqual(Base, Base.AddTicks(7)));
        Assert.False(UtcMicrosecondPrecision.AreEqual(Base.AddTicks(9), Base.AddTicks(10)));
    }

    [Theory]
    [InlineData("2026-07-25T18:00:00.9999999Z", "2026-07-25T18:00:00.9999990Z")]
    [InlineData("2026-07-25T18:59:59.9999999Z", "2026-07-25T18:59:59.9999990Z")]
    [InlineData("2026-07-25T23:59:59.9999999Z", "2026-07-25T23:59:59.9999990Z")]
    public void Boundary_values_truncate_without_crossing_the_boundary(
        string input,
        string expected)
    {
        Assert.Equal(
            DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            UtcMicrosecondPrecision.Normalize(
                DateTimeOffset.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
