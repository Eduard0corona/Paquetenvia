namespace Pricing.Domain;

public readonly record struct Money
{
    public const string Currency = "MXN";

    public Money(long amountCents)
    {
        if (amountCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amountCents));
        }

        AmountCents = amountCents;
    }

    public long AmountCents { get; }

    public static Money Add(Money left, Money right) => new(checked(left.AmountCents + right.AmountCents));

    public static Money Subtract(Money left, Money right) => new(checked(left.AmountCents - right.AmountCents));
}
