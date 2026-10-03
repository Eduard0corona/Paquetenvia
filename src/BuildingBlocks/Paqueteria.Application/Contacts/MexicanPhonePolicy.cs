namespace Paqueteria.Application.Contacts;

/// <summary>
/// ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02 and ORD-PHONE-PLUS52-LOCATIONS-2026-10-03: a contact phone is a
/// 10-digit Mexican number. The only separators tolerated are the ASCII space and the ASCII hyphen-minus, which are
/// removed. An optional leading <c>+52</c> country prefix (the literal plus sign immediately followed by
/// <c>52</c>, with separators allowed before and after it) is removed too. What remains must be exactly ten ASCII
/// digits <c>0-9</c>. Any other country prefix, a <c>52</c> without the plus sign, a second prefix, parentheses,
/// dots, other whitespace, letters and non-ASCII digits are rejected, so <c>+52 667 123 4567</c>,
/// <c>+52-6671234567</c>, <c>667 123 4567</c> and <c>667-123-4567</c> all normalize to <c>6671234567</c> while
/// <c>526671234567</c> and <c>+1 667 123 4567</c> are invalid. The normalized value is the one hashed, protected
/// (ADP-001) and stored; neither the input nor the result is ever logged.
/// </summary>
public static class MexicanPhonePolicy
{
    public const int DigitCount = 10;

    /// <summary>Upper bound on the raw text, so a separator-padded value cannot grow without limit.</summary>
    public const int MaximumInputLength = 32;

    /// <summary>The only country prefix accepted, removed before counting the national digits.</summary>
    public const string CountryPrefix = "+52";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumInputLength)
        {
            return false;
        }

        var index = 0;
        while (index < value.Length && value[index] is ' ' or '-')
        {
            index++;
        }

        if (index < value.Length && value[index] == '+')
        {
            if (string.CompareOrdinal(value, index, CountryPrefix, 0, CountryPrefix.Length) != 0)
            {
                return false;
            }

            index += CountryPrefix.Length;
        }

        Span<char> digits = stackalloc char[DigitCount];
        var count = 0;
        for (; index < value.Length; index++)
        {
            var character = value[index];
            if (character is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character) || count == DigitCount)
            {
                return false;
            }

            digits[count++] = character;
        }

        if (count != DigitCount)
        {
            return false;
        }

        normalized = new string(digits);
        return true;
    }

    public static bool IsValid(string? value) => TryNormalize(value, out _);
}
