namespace Pricing.Domain;

/// <summary>
/// GATE-011-VAT-INCLUDED-2026-09-29: the pilot presents every price with IVA included, the same for every
/// organization and client company. Only <see cref="TaxMode.VatIncluded"/> tariff rules are quoted;
/// <see cref="TaxMode.PlusVat"/> and <see cref="TaxMode.Exempt"/> stay in the AI-06 vocabulary (stored rules keep
/// their value) but are not selectable and fail closed.
/// </summary>
public static class PilotTaxPolicy
{
    public const TaxMode SelectableTaxMode = TaxMode.VatIncluded;

    public static bool IsSelectable(TaxMode taxMode) => taxMode == SelectableTaxMode;
}

/// <summary>The four amounts of a quote, in integer cents: total = subtotal - discount + tax.</summary>
public readonly record struct TaxedAmounts(Money Subtotal, Money Discount, Money Tax, Money Total);

/// <summary>
/// Integer-only tax arithmetic for a tariff amount (AI-01 §4.15: int64 cents, no floating point). The IVA rate is
/// 16% (<see cref="VatRateBasisPoints"/>). Rounding is round-half-up to the cent, computed as
/// <c>floor((2 * numerator + denominator) / (2 * denominator))</c> on 128-bit integers:
/// <list type="bullet">
/// <item><c>VAT_INCLUDED</c>: the tariff amount is the total the customer pays. The pre-tax subtotal is
/// <c>round_half_up(total * 10000 / 11600)</c> = <c>floor((100 * total + 58) / 116)</c> and the tax is the
/// remainder <c>total - subtotal</c>, so subtotal + tax always equals the tariff amount exactly;</item>
/// <item><c>PLUS_VAT</c> (not selectable in the pilot; kept tested for GATE-011): the tariff amount is the
/// pre-tax subtotal, the tax is <c>round_half_up(subtotal * 1600 / 10000)</c> = <c>floor((16 * subtotal + 50) / 100)</c>
/// and the total is <c>subtotal + tax</c>;</item>
/// <item><c>EXEMPT</c> (not selectable in the pilot): no tax; subtotal = total = tariff amount.</item>
/// </list>
/// With a 16% rate an exact half cent never occurs in either direction (<c>100 * total / 116 = 25 * total / 29</c>
/// has an odd denominator, and <c>16 * subtotal ≡ 50 (mod 100)</c> would need <c>8 * subtotal</c> to be odd), so
/// the half-up choice never changes a result; it is fixed only to keep the rule total and deterministic.
/// No discount exists yet: it is always zero.
/// </summary>
public static class TariffTaxCalculator
{
    /// <summary>IVA general rate: 16.00%.</summary>
    public const long VatRateBasisPoints = 1_600;

    private const long BasisPointsDenominator = 10_000;

    public static TaxedAmounts Calculate(TaxMode taxMode, long tariffAmountCents)
    {
        if (tariffAmountCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tariffAmountCents));
        }

        var discount = new Money(0);
        switch (taxMode)
        {
            case TaxMode.VatIncluded:
            {
                var subtotal = RoundHalfUp(
                    (Int128)tariffAmountCents * BasisPointsDenominator,
                    BasisPointsDenominator + VatRateBasisPoints);
                return new TaxedAmounts(
                    new Money(subtotal),
                    discount,
                    new Money(checked(tariffAmountCents - subtotal)),
                    new Money(tariffAmountCents));
            }

            case TaxMode.PlusVat:
            {
                var tax = RoundHalfUp((Int128)tariffAmountCents * VatRateBasisPoints, BasisPointsDenominator);
                return new TaxedAmounts(
                    new Money(tariffAmountCents),
                    discount,
                    new Money(tax),
                    new Money(checked(tariffAmountCents + tax)));
            }

            case TaxMode.Exempt:
                return new TaxedAmounts(
                    new Money(tariffAmountCents),
                    discount,
                    new Money(0),
                    new Money(tariffAmountCents));

            default:
                throw new ArgumentOutOfRangeException(nameof(taxMode));
        }
    }

    private static long RoundHalfUp(Int128 numerator, long denominator) =>
        checked((long)((2 * numerator + denominator) / (2 * (Int128)denominator)));
}
