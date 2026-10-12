namespace Locations.Application.Locations;

/// <summary>
/// How the address of a quote location is stored: the address and, when the dispatcher gave them, the references,
/// in one protected value <c>{address}\nReferences: {references}</c>. Writing and reading both go through here.
/// </summary>
public static class LocationAddressText
{
    /// <summary>The separator written between the address and its references.</summary>
    public const string ReferencesSeparator = "\nReferences: ";

    public static string Compose(string addressText, string? references) =>
        string.IsNullOrWhiteSpace(references) ? addressText : $"{addressText}{ReferencesSeparator}{references}";

    /// <summary>
    /// Splits a stored value at the first separator. The parts are returned as stored, untrimmed; references are
    /// <see langword="null"/> when there is no separator.
    /// </summary>
    public static (string AddressText, string? References) Split(string storedText)
    {
        ArgumentNullException.ThrowIfNull(storedText);
        var separator = storedText.IndexOf(ReferencesSeparator, StringComparison.Ordinal);
        return separator < 0
            ? (storedText, null)
            : (storedText[..separator], storedText[(separator + ReferencesSeparator.Length)..]);
    }
}
