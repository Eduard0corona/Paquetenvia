namespace Locations.Application.Locations;

/// <summary>
/// The protected values of one stored location, exactly as <c>locations.locations</c> keeps them: the owning
/// organization and the row the ciphertexts are bound to, and the key version they were sealed under.
/// <see cref="ToString"/> never prints the ciphertexts.
/// </summary>
public sealed record StoredLocationPii(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string KeyVersion,
    byte[] AddressCiphertext,
    byte[]? ContactNameCiphertext)
{
    public override string ToString() => $"StoredLocationPii {{ LocationId = {LocationId:D}, [ciphertexts redacted] }}";
}

/// <summary>
/// The readable text of one location: its address, the references the dispatcher gave (null when none) and its
/// contact name (null when none). It exists only in memory for the caller; <see cref="ToString"/> never prints it.
/// </summary>
public sealed record LocationPiiText(string AddressText, string? References, string? ContactName)
{
    public override string ToString() => "LocationPiiText { [redacted] }";
}

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: reads back the protected address and contact name of stored locations,
/// the same way the Locations module protected them (<c>Locations:PiiProtector</c>), so no other module repeats the
/// protection mode, the envelope purposes or the storage format. It never writes and never logs a value.
/// </summary>
public interface ILocationPiiReader
{
    /// <summary>
    /// Returns one entry per input, in the same order. An entry is <see langword="null"/> when that location cannot
    /// be read (a value rejected for its row, an unavailable key service or no configured protector); a value is
    /// never partial, garbled or the ciphertext itself. Call it outside any database transaction.
    /// </summary>
    Task<IReadOnlyList<LocationPiiText?>> ReadAsync(
        IReadOnlyList<StoredLocationPii> locations,
        CancellationToken cancellationToken);
}

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
