namespace Orders.Domain;

/// <summary>
/// D6-COD-EXPECTED bounds for the COD amount a dispatcher declares on an order, in MXN integer cents.
/// COD-CAP-20000-2026-10-02 (owner literal: "Tope COD 20,000 pesos"): at most 20,000.00 MXN per order, inclusive.
/// Zero means the order carries no COD; a negative amount is never an expectation.
/// </summary>
public static class OrderCodExpectationPolicy
{
    /// <summary>20,000.00 MXN in integer cents; the cap is inclusive.</summary>
    public const long MaximumCents = 2_000_000;

    public static bool IsValid(long cents) => cents is >= 0 and <= MaximumCents;
}
