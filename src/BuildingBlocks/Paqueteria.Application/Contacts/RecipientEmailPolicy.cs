namespace Paqueteria.Application.Contacts;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002; AI-05 <c>x-pilot-contract-deltas</c> NTF-RECIPIENT-NOTICES, part
/// NTF-002): the optional e-mail of the delivery recipient, given only on the destination of <c>createQuote</c>
/// (<c>DestinationAddressInput.email</c>). The raw text holds at most <see cref="MaximumLength"/> characters; ASCII
/// whitespace (tab, line feed, vertical tab, form feed, carriage return and space) is trimmed from both ends; what
/// remains is printable ASCII without spaces (<c>!</c> to <c>~</c>), holds exactly one <c>@</c> after a non-empty local
/// part, and its domain has a dot with at least one character before and after it. The local part is kept as typed and
/// the domain is lower-cased (ASCII). The normalized value is the one hashed, protected (ADP-001 envelope, purpose
/// <c>locations.email</c>) and copied under the recipient-notice key; neither the input nor the result is ever returned,
/// logged, audited or placed in a snapshot, an outbox payload or an event.
/// </summary>
public static class RecipientEmailPolicy
{
    /// <summary>AI-05 <c>DestinationAddressInput.email</c> maxLength, on the raw text.</summary>
    public const int MaximumLength = 254;

    /// <summary>
    /// The AI-05 pattern of <c>DestinationAddressInput.email</c>: optional ASCII whitespace, a local part without
    /// <c>@</c>, one <c>@</c>, a domain with an inner dot, optional ASCII whitespace. Together with
    /// <see cref="MaximumLength"/> it accepts exactly what <see cref="TryNormalize"/> accepts (a contract test pins it).
    /// </summary>
    public const string Pattern = "^[\\t-\\r ]*[!-?A-~]+@[!-?A-~]+\\.[!-?A-~]+[\\t-\\r ]*$";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        var start = 0;
        var end = value.Length;
        while (start < end && IsAsciiWhitespace(value[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWhitespace(value[end - 1]))
        {
            end--;
        }

        var trimmed = value.AsSpan(start, end - start);
        var at = -1;
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (character is < '!' or > '~')
            {
                return false;
            }

            if (character == '@')
            {
                if (at >= 0)
                {
                    return false;
                }

                at = index;
            }
        }

        if (at < 1)
        {
            return false;
        }

        var domain = trimmed[(at + 1)..];
        if (domain.Length < 3 || !domain[1..^1].Contains('.'))
        {
            return false;
        }

        Span<char> lower = stackalloc char[domain.Length];
        for (var index = 0; index < domain.Length; index++)
        {
            lower[index] = char.ToLowerInvariant(domain[index]);
        }

        normalized = string.Concat(trimmed[..(at + 1)], lower);
        return true;
    }

    public static bool IsValid(string? value) => TryNormalize(value, out _);

    /// <summary>Whether <paramref name="value"/> is already in its normalized form (what the services receive).</summary>
    public static bool IsNormalized(string? value) =>
        TryNormalize(value, out var normalized) && string.Equals(normalized, value, StringComparison.Ordinal);

    private static bool IsAsciiWhitespace(char character) => character is ' ' or (>= '\t' and <= '\r');
}
