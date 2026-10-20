using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Paqueteria.Infrastructure.Security.Pii;

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE envelope format v1, shared by the PII protector and, under its own key, by the
/// recipient-notice sealer (NTF-RECIPIENT-NOTICES-2026-10-20, AI-24 <c>recipient_notices.contact.notice_key</c>):
/// <c>PQE\x01</c>, a big-endian <c>uint16</c> wrapped-key length, the wrapped key, a 12-byte nonce, the 16-byte tag
/// and the AES-256-GCM ciphertext. Every value gets a fresh 256-bit data key; the associated data binds it to the
/// owning organization, the row, the purpose (column) and the key version.
/// </summary>
internal static partial class PiiEnvelopeFormat
{
    /// <summary>Envelope format v1 marker: <c>PQE</c> followed by the format byte.</summary>
    private static readonly byte[] Magic = [0x50, 0x51, 0x45, 0x01];

    private const int DataKeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaximumWrappedKeyBytes = 1024;
    private const int MaximumKeyVersionLength = 200;

    /// <summary>
    /// Seals every value under the current version of <paramref name="wrapper"/>. Any failure is
    /// <see cref="PiiProtectionUnavailableException"/>; no plaintext is ever returned.
    /// </summary>
    internal static async Task<PiiProtectedBatch> SealAsync(
        IPiiKeyWrapper wrapper,
        PiiBinding binding,
        IReadOnlyList<PiiPlaintext> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        ArgumentNullException.ThrowIfNull(values);
        if (!binding.IsValid)
        {
            throw new ArgumentException("The owning organization and row are required.", nameof(binding));
        }

        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            ValidatePurpose(value.Purpose);
            ArgumentException.ThrowIfNullOrEmpty(value.Value);
        }

        try
        {
            var keyVersion = await wrapper.GetCurrentKeyVersionAsync(cancellationToken).ConfigureAwait(false);
            if (!IsValidKeyVersion(keyVersion))
            {
                throw new PiiProtectionUnavailableException();
            }

            var ciphertexts = await Task.WhenAll(values.Select(value =>
                SealOneAsync(wrapper, binding, value, keyVersion, cancellationToken))).ConfigureAwait(false);
            return new PiiProtectedBatch(keyVersion, ciphertexts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PiiProtectionUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PiiProtectionUnavailableException(exception);
        }
    }

    /// <summary>Decrypts one parsed envelope with its unwrapped data key, which is zeroed afterwards.</summary>
    internal static string Open(PiiBinding binding, string purpose, string keyVersion, Envelope envelope, byte[] dataKey)
    {
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            if (dataKey.Length != DataKeyBytes)
            {
                throw new PiiCiphertextRejectedException();
            }

            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.Tag,
                plaintext,
                AssociatedData(binding, purpose, keyVersion));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            throw new PiiCiphertextRejectedException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static bool TryParse(byte[] ciphertext, out Envelope envelope)
    {
        envelope = default;
        var span = ciphertext.AsSpan();
        if (span.Length < Magic.Length + sizeof(ushort) || !span[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(span[Magic.Length..]);
        var offset = Magic.Length + sizeof(ushort);
        if (wrappedLength is 0 or > MaximumWrappedKeyBytes ||
            span.Length < offset + wrappedLength + NonceBytes + TagBytes + 1)
        {
            return false;
        }

        envelope = new Envelope(
            span.Slice(offset, wrappedLength).ToArray(),
            span.Slice(offset + wrappedLength, NonceBytes).ToArray(),
            span.Slice(offset + wrappedLength + NonceBytes, TagBytes).ToArray(),
            span[(offset + wrappedLength + NonceBytes + TagBytes)..].ToArray());
        return true;
    }

    internal static void ValidatePurpose(string purpose)
    {
        if (purpose is null || !PurposePattern().IsMatch(purpose))
        {
            throw new ArgumentException("The PII purpose must be a lower-case column identifier.", nameof(purpose));
        }
    }

    internal static bool IsValidKeyVersion(string? keyVersion) =>
        !string.IsNullOrWhiteSpace(keyVersion) &&
        keyVersion.Length <= MaximumKeyVersionLength &&
        keyVersion.All(character => character is > ' ' and < (char)0x7F);

    private static async Task<byte[]> SealOneAsync(
        IPiiKeyWrapper wrapper,
        PiiBinding binding,
        PiiPlaintext value,
        string keyVersion,
        CancellationToken cancellationToken)
    {
        var dataKey = RandomNumberGenerator.GetBytes(DataKeyBytes);
        var plaintext = Encoding.UTF8.GetBytes(value.Value);
        try
        {
            var wrappedKey = await wrapper.WrapKeyAsync(keyVersion, dataKey, cancellationToken)
                .ConfigureAwait(false);
            if (wrappedKey is not { Length: > 0 and <= MaximumWrappedKeyBytes })
            {
                throw new PiiProtectionUnavailableException();
            }

            var envelope = new byte[Magic.Length + sizeof(ushort) + wrappedKey.Length + NonceBytes + TagBytes + plaintext.Length];
            var span = envelope.AsSpan();
            Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16BigEndian(span[Magic.Length..], (ushort)wrappedKey.Length);
            var offset = Magic.Length + sizeof(ushort);
            wrappedKey.CopyTo(span[offset..]);
            offset += wrappedKey.Length;
            var nonce = span.Slice(offset, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            offset += NonceBytes;
            var tag = span.Slice(offset, TagBytes);
            offset += TagBytes;
            using var aes = new AesGcm(dataKey, TagBytes);
            aes.Encrypt(nonce, plaintext, span[offset..], tag, AssociatedData(binding, value.Purpose, keyVersion));
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Binds each ciphertext to its owning organization, its row, its column and its key version,
    /// so a value cannot be moved to another tenant, row or column, or relabelled with another
    /// version, without failing authentication.
    /// </summary>
    private static byte[] AssociatedData(PiiBinding binding, string purpose, string keyVersion)
    {
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var versionBytes = Encoding.UTF8.GetBytes(keyVersion);
        var data = new byte[Magic.Length + 32 + (2 * sizeof(ushort)) + purposeBytes.Length + versionBytes.Length];
        var span = data.AsSpan();
        Magic.CopyTo(span);
        var offset = Magic.Length;
        _ = binding.OwnerOrganizationId.TryWriteBytes(span.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;
        _ = binding.EntityId.TryWriteBytes(span.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)purposeBytes.Length);
        offset += sizeof(ushort);
        purposeBytes.CopyTo(span[offset..]);
        offset += purposeBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)versionBytes.Length);
        offset += sizeof(ushort);
        versionBytes.CopyTo(span[offset..]);
        return data;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PurposePattern();

    internal readonly record struct Envelope(byte[] WrappedKey, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
}
