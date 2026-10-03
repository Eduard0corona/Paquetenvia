using Pricing.Application.Quotes;
using Pricing.Infrastructure.Quotes;

namespace Paqueteria.UnitTests.Pricing;

/// <summary>
/// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02: a quote contact phone is a 10-digit Mexican number; only ASCII spaces
/// and hyphens are removed before counting.
/// </summary>
public sealed class QuotePhonePolicyTests
{
    private static readonly Guid OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData("6671234567", "6671234567")]
    [InlineData("667 123 4567", "6671234567")]
    [InlineData("667-123-4567", "6671234567")]
    [InlineData(" 66 71 23 45 67 ", "6671234567")]
    [InlineData("667 - 123 - 4567", "6671234567")]
    [InlineData("0000000000", "0000000000")]
    public void Ten_digits_with_spaces_or_hyphens_normalize_to_the_digits(string input, string expected)
    {
        Assert.True(QuotePhonePolicy.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("667123456")]
    [InlineData("66712345678")]
    [InlineData("+526671234567")]
    [InlineData("+52 667 123 4567")]
    [InlineData("526671234567")]
    [InlineData("52 667 123 4567")]
    [InlineData("(667) 123 4567")]
    [InlineData("667.123.4567")]
    [InlineData("667_123_4567")]
    [InlineData("667\t123\t4567")]
    [InlineData("667 123 4567")]
    [InlineData("667–123–4567")]
    [InlineData("٦٦٧١٢٣٤٥٦٧")]
    [InlineData("６６７１２３４５６７")]
    [InlineData("667123456a")]
    [InlineData("------------------------6671234567")]
    public void Anything_else_is_rejected(string? input)
    {
        Assert.False(QuotePhonePolicy.TryNormalize(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("+526671111111", "6672222222")]
    [InlineData("6671111111", "52 667 222 2222")]
    [InlineData("667111111", "6672222222")]
    public async Task The_quote_service_rejects_an_invalid_phone_before_any_dependency(string origin, string destination)
    {
        var service = new PostgreSqlQuoteService(null!, null!, null!, null!, null!, null!, null!);

        var exception = await Assert.ThrowsAsync<QuoteValidationException>(() =>
            service.CreateAsync(Command(origin, destination), CancellationToken.None));

        Assert.Equal(QuoteValidationCode.InvalidRequest, exception.Code);
    }

    [Fact]
    public void Separators_do_not_change_the_normalized_value_the_quote_hashes()
    {
        Assert.True(QuotePhonePolicy.TryNormalize("667 111 1111", out var spaced));
        Assert.True(QuotePhonePolicy.TryNormalize("667-111-1111", out var hyphenated));

        Assert.Equal(
            PostgreSqlQuoteService.ComputeInputHash(Command(spaced, "6672222222")),
            PostgreSqlQuoteService.ComputeInputHash(Command(hyphenated, "6672222222")));
        Assert.NotEqual(
            PostgreSqlQuoteService.ComputeInputHash(Command("6671111111", "6672222222")),
            PostgreSqlQuoteService.ComputeInputHash(Command("6671111112", "6672222222")));
    }

    private static CreateQuoteCommand Command(string originPhone, string destinationPhone) => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        OrganizationId,
        "synthetic-key-0001",
        null,
        new QuoteAddressInput("Synthetic origin 100", "Synthetic Sender", originPhone, 24.8, -107.4, null),
        new QuoteAddressInput("Synthetic destination 200", "Synthetic Receiver", destinationPhone, 24.81, -107.41, null),
        "SAME_DAY",
        false,
        [new QuotePackageInput("Synthetic parcel", 1000, 50_00, 100, 100, 100)],
        "request-1");
}
