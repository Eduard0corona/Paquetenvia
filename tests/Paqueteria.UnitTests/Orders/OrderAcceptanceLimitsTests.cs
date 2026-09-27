using Orders.Application.Csv;
using Orders.Application.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// AI05-INPUT-LIMITS with the approved values: acceptance versions are 1-64 characters of
/// ^[A-Za-z0-9._-]+$ on POST /orders and on a CSV-001 row, and accepted_at lies within [-72 h, +5 min].
/// </summary>
public sealed class OrderAcceptanceLimitsTests
{
    private static readonly DateTimeOffset AcceptedAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Json_and_csv_share_one_version_bound()
    {
        Assert.Equal(64, OrderAcceptanceInputPolicy.MaximumVersionLength);
        Assert.Equal(OrderAcceptanceInputPolicy.MaximumVersionLength, CsvOrderImportContract.MaximumVersionLength);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(63, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    [InlineData(4_096, false)]
    public void Terms_and_privacy_versions_accept_1_to_64_characters(int length, bool expected)
    {
        var version = new string('v', length);

        Assert.Equal(expected, OrderAcceptanceInputPolicy.IsValid(version, "privacy-2026-09", AcceptedAt, "WEB"));
        Assert.Equal(expected, OrderAcceptanceInputPolicy.IsValid("terms-2026-09", version, AcceptedAt, "WEB"));
        Assert.Equal(expected, OrderAcceptanceInputPolicy.IsVersion(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_version_is_rejected(string? version)
    {
        Assert.False(OrderAcceptanceInputPolicy.IsValid(version, "privacy-2026-09", AcceptedAt, "WEB"));
        Assert.False(OrderAcceptanceInputPolicy.IsValid("terms-2026-09", version, AcceptedAt, "WEB"));
    }

    [Theory]
    [InlineData("terms-2026.09_v1", true)]
    [InlineData("A.b_c-9", true)]
    [InlineData("terms 2026", false)]
    [InlineData("terms/2026", false)]
    [InlineData("términos", false)]
    [InlineData("v1\n", false)]
    public void Versions_match_the_approved_pattern(string version, bool expected)
    {
        Assert.Equal("^[A-Za-z0-9._-]+$", OrderAcceptanceInputPolicy.VersionPattern);
        Assert.Equal(expected, OrderAcceptanceInputPolicy.IsVersion(version));
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.IsMatch(version, @"\A[A-Za-z0-9._-]+\z"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5 * 60, true)]
    [InlineData(5 * 60 + 1, false)]
    [InlineData(-72 * 3600, true)]
    [InlineData(-72 * 3600 - 1, false)]
    public void Accepted_at_lies_within_plus_5_minutes_and_minus_72_hours_inclusive(int secondsFromNow, bool expected)
    {
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(5), OrderAcceptanceInputPolicy.MaximumFutureSkew);
        Assert.Equal(TimeSpan.FromHours(72), OrderAcceptanceInputPolicy.MaximumAge);
        Assert.Equal(expected, OrderAcceptanceInputPolicy.IsWithinAcceptanceWindow(now.AddSeconds(secondsFromNow), now));
    }

    [Fact]
    public void A_csv_row_with_a_65_character_version_is_rejected_with_its_row_code()
    {
        var csv =
            "quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel\r\n" +
            $"{Guid.NewGuid():D},SENDER,{new string('t', 65)},privacy-2026-09,2026-09-27T12:00:00Z,WEB\r\n" +
            $"{Guid.NewGuid():D},SENDER,{new string('t', 64)},privacy-2026-09,2026-09-27T12:00:00Z,WEB\r\n";

        var result = CsvOrderImportPrevalidator.Prevalidate(System.Text.Encoding.UTF8.GetBytes(csv));

        Assert.Equal(2, result.Rows.Count);
        Assert.False(result.Rows[0].Valid);
        Assert.Contains(result.Rows[0].Errors, error => error.Code == CsvOrderImportRowErrorCodes.TermsVersionInvalid);
        Assert.True(result.Rows[1].Valid);
    }
}
