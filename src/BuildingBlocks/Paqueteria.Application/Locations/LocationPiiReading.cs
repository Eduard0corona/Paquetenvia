namespace Paqueteria.Application.Locations;

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
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11: reads back the protected address and contact name of stored locations.
/// The Locations module implements it, the same way it protected them (<c>Locations:PiiProtector</c>), so no other
/// module repeats the protection mode, the envelope purposes or the storage format. It never writes and never logs a
/// value. The port lives in this shared layer, like the VOICE-001 contracts, so a reader (Dispatch) needs no project
/// reference to the Locations module, whose geometry package would otherwise enter the reader's package graph.
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
