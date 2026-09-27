using Finance.Domain.Settlements;
using Finance.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Paqueteria.UnitTests.Finance;

/// <summary>
/// Settlement periods are whole operational days of the configured IANA time zone, compared against
/// timestamps through their UTC boundaries: the start of period_from inclusive and the start of the
/// day after period_to exclusive.
/// </summary>
public sealed class SettlementPeriodTests
{
    private static TimeZoneInfo Mazatlan => FinanceOperationalTimeZone.Resolve("America/Mazatlan");

    [Fact]
    public void The_operational_time_zone_defaults_to_America_Mazatlan()
    {
        Assert.Equal("America/Mazatlan", new FinanceOptions().OperationalTimeZone);
        Assert.Equal("America/Mazatlan", FinanceOptions.DefaultOperationalTimeZone);
        Assert.True(FinanceOperationalTimeZone.TryResolve("America/Mazatlan", out var zone));
        Assert.Equal("America/Mazatlan", zone.Id);
        Assert.True(zone.HasIanaId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Pacific Standard Time")]
    [InlineData("Mountain Standard Time (Mexico)")]
    public void Only_a_known_IANA_time_zone_is_accepted(string? id)
    {
        Assert.False(FinanceOperationalTimeZone.TryResolve(id, out _));
        Assert.Throws<InvalidOperationException>(() => FinanceOperationalTimeZone.Resolve(id));
    }

    [Fact]
    public void A_misconfigured_operational_time_zone_fails_closed_at_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Finance:OperationalTimeZone"] = "Mars/Olympus_Mons",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddFinanceInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<FinanceOptions>>().Value);
        Assert.Contains("Finance:OperationalTimeZone", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_week_in_Culiacan_is_bounded_by_local_midnights_in_UTC()
    {
        Assert.True(SettlementPeriod.TryResolve(new(2026, 9, 14), new(2026, 9, 20), Mazatlan, out var period));

        // Mexico has not observed daylight saving time since 2022: Culiacán is UTC-07:00 all year.
        Assert.Equal(new DateOnly(2026, 9, 14), period.From);
        Assert.Equal(new DateOnly(2026, 9, 20), period.To);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 7, 0, 0, TimeSpan.Zero), period.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero), period.EndsBeforeUtc);
        Assert.Equal(TimeSpan.Zero, period.StartsAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, period.EndsBeforeUtc.Offset);
    }

    [Fact]
    public void A_single_day_period_covers_exactly_that_local_day()
    {
        Assert.True(SettlementPeriod.TryResolve(new(2026, 12, 31), new(2026, 12, 31), Mazatlan, out var period));
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 7, 0, 0, TimeSpan.Zero), period.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 7, 0, 0, TimeSpan.Zero), period.EndsBeforeUtc);

        // 23:59:59 local on the last day is inside; local midnight of the next day is not.
        var lastLocalSecond = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(-7));
        Assert.True(lastLocalSecond >= period.StartsAtUtc && lastLocalSecond < period.EndsBeforeUtc);
        var nextLocalMidnight = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(-7));
        Assert.False(nextLocalMidnight < period.EndsBeforeUtc);
    }

    [Fact]
    public void Historical_daylight_saving_days_keep_their_real_length()
    {
        // 5 April 2020: Culiacán moved from UTC-07:00 to UTC-06:00 at 02:00, a 23-hour day.
        Assert.True(SettlementPeriod.TryResolve(new(2020, 4, 5), new(2020, 4, 5), Mazatlan, out var period));
        Assert.Equal(new DateTimeOffset(2020, 4, 5, 7, 0, 0, TimeSpan.Zero), period.StartsAtUtc);
        Assert.Equal(new DateTimeOffset(2020, 4, 6, 6, 0, 0, TimeSpan.Zero), period.EndsBeforeUtc);
    }

    [Fact]
    public void A_daylight_saving_gap_at_midnight_starts_the_day_at_its_first_existing_minute()
    {
        // Clocks jump from 00:00 to 01:00 on the first Sunday of September (6 September 2026).
        var zone = CustomZone(
            "Test/MidnightGap",
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 9, 1, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));

        Assert.True(SettlementPeriod.TryStartOfDay(new(2026, 9, 6), zone, out var gapDay));
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 3, 0, 0, TimeSpan.Zero), gapDay);
        Assert.True(SettlementPeriod.TryResolve(new(2026, 9, 5), new(2026, 9, 5), zone, out var dayBefore));
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero), dayBefore.StartsAtUtc);
        Assert.Equal(gapDay, dayBefore.EndsBeforeUtc);
    }

    [Fact]
    public void An_ambiguous_midnight_starts_the_day_at_its_first_occurrence()
    {
        // Clocks fall back from 01:00 to 00:00 on the first Sunday of November (1 November 2026).
        var zone = CustomZone(
            "Test/AmbiguousMidnight",
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 1, DayOfWeek.Sunday));

        Assert.True(SettlementPeriod.TryResolve(new(2026, 10, 31), new(2026, 11, 1), zone, out var period));
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 2, 0, 0, TimeSpan.Zero), period.StartsAtUtc);
        Assert.True(SettlementPeriod.TryStartOfDay(new(2026, 11, 1), zone, out var ambiguousDay));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero), ambiguousDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero), period.EndsBeforeUtc);
    }

    [Fact]
    public void Consecutive_days_neither_overlap_nor_leave_a_gap()
    {
        var day = new DateOnly(2026, 1, 1);
        for (var index = 0; index < 366; index++, day = day.AddDays(1))
        {
            Assert.True(SettlementPeriod.TryResolve(day, day, Mazatlan, out var current));
            Assert.True(SettlementPeriod.TryResolve(day.AddDays(1), day.AddDays(1), Mazatlan, out var next));
            Assert.Equal(current.EndsBeforeUtc, next.StartsAtUtc);
            Assert.True(current.StartsAtUtc < current.EndsBeforeUtc);
        }
    }

    [Fact]
    public void A_period_is_ordered_and_covers_at_most_366_days()
    {
        Assert.False(SettlementPeriod.TryResolve(new(2026, 9, 20), new(2026, 9, 14), Mazatlan, out _));
        Assert.True(SettlementPeriod.TryResolve(new(2028, 1, 1), new(2028, 12, 31), Mazatlan, out _));
        Assert.False(SettlementPeriod.TryResolve(new(2027, 1, 1), new(2028, 1, 2), Mazatlan, out _));
        Assert.False(SettlementPeriod.TryResolve(DateOnly.MaxValue, DateOnly.MaxValue, Mazatlan, out _));
    }

    private static TimeZoneInfo CustomZone(
        string id,
        TimeZoneInfo.TransitionTime daylightStart,
        TimeZoneInfo.TransitionTime daylightEnd) =>
        TimeZoneInfo.CreateCustomTimeZone(
            id,
            TimeSpan.FromHours(-3),
            id,
            id,
            id + " daylight",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2000, 1, 1),
                    new DateTime(2099, 12, 31),
                    TimeSpan.FromHours(1),
                    daylightStart,
                    daylightEnd),
            ]);
}
