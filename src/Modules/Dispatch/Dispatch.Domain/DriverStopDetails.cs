using System.Globalization;
using System.Text;

namespace Dispatch.Domain;

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the contact name the assigned driver sees, derived on read from the
/// location's protected contact name and never stored. The owner answered "Completo en recolección": a PICKUP stop
/// shows the full name (usually a business, "Farmacia Las Quintas"); DELIVERY and RETURN stops show only the short
/// name ("M. López").
/// </summary>
public static class DriverStopContactName
{
    /// <summary>Upper bound, in UTF-16 code units, of the short name (AI-05 <c>DriverStop.contact_short_name</c>).</summary>
    public const int MaximumLength = 64;

    /// <summary>Upper bound, in UTF-16 code units, of the full name (AI-05 <c>DriverStop.contact_full_name</c>).</summary>
    public const int MaximumFullLength = 200;

    /// <summary>
    /// The form of the contact name a stop of <paramref name="stopType"/> carries: the full name on a PICKUP stop
    /// and the short name on DELIVERY and RETURN stops. The other form is always <see langword="null"/>, so the
    /// full name of a delivery or return contact never leaves the server.
    /// </summary>
    public static DriverStopContact ForStop(DriverStopType stopType, string? contactName) =>
        stopType == DriverStopType.Pickup
            ? new DriverStopContact(Full(contactName), null)
            : new DriverStopContact(null, Shorten(contactName));

    /// <summary>
    /// The full name with its white space collapsed to single spaces; a name longer than
    /// <see cref="MaximumFullLength"/> is cut on a text-element boundary and ends with "…". A missing or blank name
    /// is <see langword="null"/>.
    /// </summary>
    public static string? Full(string? contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return null;
        }

        var collapsed = string.Join(' ', contactName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return DriverStopText.Truncate(collapsed, MaximumFullLength);
    }

    /// <summary>
    /// The initial of the first word, a period, a space and the last word: "María López" becomes "M. López" and
    /// "María López Pérez" becomes "M. Pérez" (the first given name and the first surname cannot be told apart
    /// reliably, so the last word is used). A single word is kept as it is. Words are split on white space; the
    /// initial is upper-cased and the last word keeps the case it was typed with. A last word that would make the
    /// result longer than <see cref="MaximumLength"/> is cut on a text-element boundary. A missing or blank name is
    /// <see langword="null"/>.
    /// </summary>
    public static string? Shorten(string? contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return null;
        }

        var words = contactName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
        {
            return Bound(words[0], MaximumLength);
        }

        var first = words[0];
        var initial = first[..StringInfo.GetNextTextElementLength(first)].ToUpperInvariant();
        var prefix = initial + ". ";
        return prefix + Bound(words[^1], MaximumLength - prefix.Length);
    }

    private static string Bound(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var builder = new StringBuilder(maximumLength);
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (builder.Length + element.Length > maximumLength)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString();
    }
}

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: the address text the assigned driver needs at the door. Locations keeps
/// the address and, when the dispatcher gave them, the references in one protected value:
/// <c>{address}\nReferences: {references}</c> (<c>PostgreSqlLocationService</c>, quote locations). This splits that
/// value back into the two parts the driver app shows, each bounded to the AI-05 limits.
/// </summary>
public static class DriverStopAddressText
{
    /// <summary>The separator Locations writes between the address and its references.</summary>
    public const string ReferencesSeparator = "\nReferences: ";

    /// <summary>Upper bound, in UTF-16 code units, of <c>DriverStop.navigation.address_text</c>.</summary>
    public const int MaximumAddressLength = 1000;

    /// <summary>Upper bound, in UTF-16 code units, of <c>DriverStop.navigation.references</c>.</summary>
    public const int MaximumReferencesLength = 500;

    /// <summary>
    /// Splits a decrypted location text at the first references separator and trims both parts. Returns
    /// <see langword="false"/> when no address is left, so the caller fails closed instead of inventing one. A part
    /// longer than its bound is cut on a text-element boundary and ends with "…".
    /// </summary>
    public static bool TrySplit(string? protectedText, out string address, out string? references)
    {
        address = string.Empty;
        references = null;
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return false;
        }

        var separator = protectedText.IndexOf(ReferencesSeparator, StringComparison.Ordinal);
        var addressPart = (separator < 0 ? protectedText : protectedText[..separator]).Trim();
        var referencesPart = separator < 0 ? null : protectedText[(separator + ReferencesSeparator.Length)..].Trim();
        if (addressPart.Length == 0)
        {
            return false;
        }

        address = DriverStopText.Truncate(addressPart, MaximumAddressLength);
        references = string.IsNullOrEmpty(referencesPart)
            ? null
            : DriverStopText.Truncate(referencesPart, MaximumReferencesLength);
        return true;
    }
}

/// <summary>The contact name of one stop in the form its type allows; <see cref="ToString"/> never prints it.</summary>
public sealed record DriverStopContact(string? FullName, string? ShortName)
{
    public override string ToString() => "DriverStopContact { [redacted] }";
}

internal static class DriverStopText
{
    /// <summary>A value longer than its bound is cut on a text-element boundary and ends with "…".</summary>
    public static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        const string ellipsis = "…";
        var builder = new StringBuilder(maximumLength);
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (builder.Length + element.Length + ellipsis.Length > maximumLength)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString().TrimEnd() + ellipsis;
    }
}
