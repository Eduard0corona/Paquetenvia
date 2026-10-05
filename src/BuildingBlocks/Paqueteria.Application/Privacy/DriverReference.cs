using System.Security.Cryptography;

namespace Paqueteria.Application.Privacy;

/// <summary>
/// The only driver label an operations screen shows (OBS-001): <c>DRV-</c> followed by the first four bytes, in
/// lowercase hexadecimal, of the SHA-256 of the driver id's 16 big-endian bytes. It is not personal data (AI-06
/// stores no driver name) and it is stable, so the dashboard, the order detail and the assignable-driver list
/// (UI-PHASE2-DRIVER-PICKER-2026-10-05) name the same driver the same way.
/// </summary>
public static class DriverReference
{
    public const string Prefix = "DRV-";

    /// <summary>The label of a non-empty driver id, for example <c>DRV-1a2b3c4d</c>.</summary>
    public static string From(Guid driverId)
    {
        if (driverId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty driver id is required.", nameof(driverId));
        }

        Span<byte> guidBytes = stackalloc byte[16];
        driverId.TryWriteBytes(guidBytes, bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(guidBytes, hash);
        return Prefix + Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }
}
