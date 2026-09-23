namespace Finance.Domain;

/// <summary>
/// Signed monetary amount expressed in integer cents. FIN-001 forbids binary floating point for
/// money, so every arithmetic operation is checked <see cref="long"/> arithmetic and overflow is
/// surfaced instead of silently truncated.
/// </summary>
public readonly record struct MoneyCents : IComparable<MoneyCents>
{
    public const string Currency = "MXN";

    public MoneyCents(long amountCents) => AmountCents = amountCents;

    public static MoneyCents Zero => default;

    public long AmountCents { get; }

    public bool IsNegative => AmountCents < 0;

    public static MoneyCents FromNonNegative(long amountCents) => amountCents < 0
        ? throw new ArgumentOutOfRangeException(nameof(amountCents))
        : new(amountCents);

    public static MoneyCents Add(MoneyCents left, MoneyCents right) =>
        new(checked(left.AmountCents + right.AmountCents));

    public static MoneyCents Subtract(MoneyCents left, MoneyCents right) =>
        new(checked(left.AmountCents - right.AmountCents));

    public static MoneyCents operator +(MoneyCents left, MoneyCents right) => Add(left, right);

    public static MoneyCents operator -(MoneyCents left, MoneyCents right) => Subtract(left, right);

    public int CompareTo(MoneyCents other) => AmountCents.CompareTo(other.AmountCents);

    public static bool operator <(MoneyCents left, MoneyCents right) => left.AmountCents < right.AmountCents;

    public static bool operator >(MoneyCents left, MoneyCents right) => left.AmountCents > right.AmountCents;

    public static bool operator <=(MoneyCents left, MoneyCents right) => left.AmountCents <= right.AmountCents;

    public static bool operator >=(MoneyCents left, MoneyCents right) => left.AmountCents >= right.AmountCents;

    /// <summary>
    /// Ratio of this amount to <paramref name="reference"/> in basis points, truncated toward zero.
    /// The intermediate product is widened to <see cref="Int128"/> so no representable cents pair can
    /// overflow, and a zero reference yields <see langword="null"/> instead of an undefined ratio.
    /// </summary>
    public long? BasisPointsOf(MoneyCents reference)
    {
        if (reference.AmountCents == 0)
        {
            return null;
        }

        return (long)((Int128)AmountCents * 10_000 / reference.AmountCents);
    }

    public override string ToString() => $"{AmountCents} {Currency}";
}
