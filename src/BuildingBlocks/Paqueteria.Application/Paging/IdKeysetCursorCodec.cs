namespace Paqueteria.Application.Paging;

/// <summary>
/// The opaque cursor of a list ordered by id: Base64URL without padding of one version byte, which names the
/// operation that issued it, and the last listed id in its 16 big-endian bytes (23 characters). A value that
/// another operation issued, that does not round-trip exactly or that names the empty id is rejected, so a
/// cursor is never reinterpreted.
/// </summary>
public static class IdKeysetCursorCodec
{
    private const int EncodedLength = 23;

    public static string Encode(byte version, Guid afterId)
    {
        if (afterId == Guid.Empty)
        {
            throw new ArgumentException("A cursor names a listed, non-empty id.", nameof(afterId));
        }

        Span<byte> bytes = stackalloc byte[17];
        bytes[0] = version;
        afterId.TryWriteBytes(bytes[1..], bigEndian: true, out _);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, byte version, out Guid afterId)
    {
        afterId = Guid.Empty;
        if (value is null || value.Length != EncodedLength ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != 17 || bytes[0] != version)
        {
            return false;
        }

        var decoded = new Guid(bytes.AsSpan(1), bigEndian: true);
        if (decoded == Guid.Empty || Encode(version, decoded) != value)
        {
            return false;
        }

        afterId = decoded;
        return true;
    }
}
