using Orders.Application.Csv;
using Orders.Application.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// AI05-INPUT-LIMITS: the acceptance version identifiers are bounded identically on POST /orders and on a
/// CSV-001 row, at the 64 characters CSV-001 already enforced.
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
