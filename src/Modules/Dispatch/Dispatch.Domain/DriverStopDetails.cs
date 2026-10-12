using System.Globalization;
using System.Text;

namespace Dispatch.Domain;

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the contact name the assigned driver sees, derived on read from the
/// location's protected contact name and never stored. The owner answered "Completo en recolección" and "Completo,
/// como recolección": PICKUP and RETURN stops show the full name (usually a business, "Farmacia Las Quintas");
/// DELIVERY stops show only the short name ("M. López").
/// </summary>
public static class DriverStopContactName
{
    /// <summary>Upper bound, in UTF-16 code units, of the short name (AI-05 <c>DriverStop.contact_short_name</c>).</summary>
    public const int MaximumLength = 64;

    /// <summary>Upper bound, in UTF-16 code units, of the full name (AI-05 <c>DriverStop.contact_full_name</c>).</summary>
    public const int MaximumFullLength = 200;

    /// <summary>
    /// The form of the contact name a stop of <paramref name="stopType"/> carries: the full name on PICKUP and RETURN
    /// stops and the short name on DELIVERY stops. The other form is always <see langword="null"/>, so the full name
    /// of a delivery recipient never leaves the server.
    /// </summary>
    public static DriverStopContact ForStop(DriverStopType stopType, string? contactName) =>
        stopType == DriverStopType.Delivery
            ? new DriverStopContact(null, Shorten(contactName))
            : new DriverStopContact(Full(contactName), null);

    /// <summary>
    /// The full name, normalized as <see cref="DriverStopText.NormalizeLine"/> does, and cut at
    /// <see cref="MaximumFullLength"/> on a text-element boundary with "…". A name that is empty after normalization
    /// is <see langword="null"/>.
    /// </summary>
    public static string? Full(string? contactName)
    {
        var normalized = DriverStopText.NormalizeLine(contactName);
        return normalized is null ? null : Present(DriverStopText.Fit(normalized, MaximumFullLength, ellipsis: true));
    }

    /// <summary>
    /// The initial of the first word, a period, a space and the last word: "María López" becomes "M. López" and
    /// "María López Pérez" becomes "M. Pérez" (the first given name and the first surname cannot be told apart
    /// reliably, so the last word is used). A single word is kept as it is. Words are split after normalization
    /// (white space and Unicode format characters); the initial is upper-cased and the last word keeps the case it
    /// was typed with. The result never exceeds <see cref="MaximumLength"/>: every part is cut on a text-element
    /// boundary, and an initial that alone does not fit is left out. It never throws; a name with nothing left is
    /// <see langword="null"/>.
    /// </summary>
    public static string? Shorten(string? contactName)
    {
        var normalized = DriverStopText.NormalizeLine(contactName);
        if (normalized is null)
        {
            return null;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
        {
            return Present(DriverStopText.Fit(words[0], MaximumLength, ellipsis: false));
        }

        // ". " and at least one character of the last word must still fit after the initial.
        var initial = DriverStopText.Fit(
            DriverStopText.FirstTextElement(words[0]).ToUpperInvariant(),
            MaximumLength - 3,
            ellipsis: false);
        var prefix = initial.Length == 0 ? string.Empty : initial + ". ";
        var last = DriverStopText.Fit(words[^1], MaximumLength - prefix.Length, ellipsis: false);
        return Present((prefix + last).Trim());
    }

    private static string? Present(string value) => value.Length == 0 ? null : value;
}

/// <summary>The contact name of one stop in the form its type allows; <see cref="ToString"/> never prints it.</summary>
public sealed record DriverStopContact(string? FullName, string? ShortName)
{
    public override string ToString() => "DriverStopContact { [redacted] }";
}

/// <summary>UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the operating zone name shown on a stop.</summary>
public static class DriverStopZoneName
{
    /// <summary>Upper bound, in UTF-16 code units, of <c>DriverStop.zone_name</c>; the driver app uses the same one.</summary>
    public const int MaximumLength = 200;

    /// <summary>The zone name normalized and cut at <see cref="MaximumLength"/> with "…"; empty means absent.</summary>
    public static string? Normalize(string? zoneName)
    {
        var normalized = DriverStopText.NormalizeLine(zoneName);
        var fitted = normalized is null ? string.Empty : DriverStopText.Fit(normalized, MaximumLength, ellipsis: true);
        return fitted.Length == 0 ? null : fitted;
    }
}

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the address text and references the assigned driver needs at the door,
/// as the Locations module reads them back, bounded to the AI-05 limits.
/// </summary>
public static class DriverStopAddressText
{
    /// <summary>Upper bound, in UTF-16 code units, of <c>DriverStop.navigation.address_text</c>.</summary>
    public const int MaximumAddressLength = 1000;

    /// <summary>Upper bound, in UTF-16 code units, of <c>DriverStop.navigation.references</c>.</summary>
    public const int MaximumReferencesLength = 500;

    /// <summary>
    /// Normalizes both parts (<see cref="DriverStopText.NormalizeText"/>) and cuts each at its bound with "…".
    /// Returns <see langword="false"/> when no address is left, so the caller reports the details as unavailable
    /// instead of inventing one; empty references become <see langword="null"/>.
    /// </summary>
    public static bool TryNormalize(string? addressText, string? references, out string address, out string? referencesText)
    {
        address = string.Empty;
        referencesText = null;
        var normalizedAddress = DriverStopText.NormalizeText(addressText);
        if (normalizedAddress is null)
        {
            return false;
        }

        address = DriverStopText.Fit(normalizedAddress, MaximumAddressLength, ellipsis: true);
        var normalizedReferences = DriverStopText.NormalizeText(references);
        var fittedReferences = normalizedReferences is null
            ? string.Empty
            : DriverStopText.Fit(normalizedReferences, MaximumReferencesLength, ellipsis: true);
        referencesText = fittedReferences.Length == 0 ? null : fittedReferences;
        return address.Length > 0;
    }
}

/// <summary>
/// Text rules shared by every stop detail. Unicode format characters (category Cf, such as U+FEFF and U+200B) count
/// as white space, so a value the driver app would see as blank is never sent as present, and every value is cut on
/// a text-element boundary so no grapheme is split.
/// </summary>
public static class DriverStopText
{
    private const string Ellipsis = "…";

    /// <summary>One line: every run of white space or format characters becomes one space; trimmed; empty is null.</summary>
    public static string? NormalizeLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (IsBlank(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Free text that may keep its line breaks (an address, references): format characters become spaces and the
    /// whole value is trimmed; empty is null.
    /// </summary>
    public static string? NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.GetUnicodeCategory(character) == UnicodeCategory.Format ? ' ' : character);
        }

        var trimmed = builder.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// <paramref name="value"/> when it fits in <paramref name="maximumLength"/> UTF-16 code units; otherwise as many
    /// whole text elements as fit, followed by "…" when <paramref name="ellipsis"/> is set. Never throws for a
    /// non-positive bound: nothing fits, so the result is empty.
    /// </summary>
    public static string Fit(string value, int maximumLength, bool ellipsis)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var suffix = ellipsis ? Ellipsis : string.Empty;
        var room = maximumLength - suffix.Length;
        if (room <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(room);
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (builder.Length + element.Length > room)
            {
                break;
            }

            builder.Append(element);
        }

        var kept = builder.ToString().TrimEnd();
        return kept.Length == 0 ? string.Empty : kept + suffix;
    }

    /// <summary>The first text element (grapheme) of <paramref name="value"/>, however long.</summary>
    public static string FirstTextElement(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0 ? value : value[..StringInfo.GetNextTextElementLength(value)];
    }

    private static bool IsBlank(char character) =>
        char.IsWhiteSpace(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format;
}
