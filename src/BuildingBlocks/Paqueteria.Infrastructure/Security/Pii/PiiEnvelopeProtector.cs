using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Paqueteria.Infrastructure.Security.Pii;

/// <summary>One value to protect and the column it belongs to (for example <c>locations.phone</c>).</summary>
public sealed record PiiPlaintext(string Purpose, string Value);

/// <summary>
/// The row every ciphertext of a batch belongs to: the owning organization (tenant) and the row id.
/// Both are authenticated, so a ciphertext copied into another tenant's row, or into another row of
/// the same tenant, fails to decrypt.
/// </summary>
public readonly record struct PiiBinding(Guid OwnerOrganizationId, Guid EntityId)
{
    public bool IsValid => OwnerOrganizationId != Guid.Empty && EntityId != Guid.Empty;
}

/// <summary>
/// Ciphertexts in the same order as the request, all under <see cref="KeyVersion"/>: the single
/// value the row stores in <c>pii_key_version</c>. The protector chose it; no caller supplies it.
/// </summary>
public sealed record PiiProtectedBatch(string KeyVersion, IReadOnlyList<byte[]> Ciphertexts);

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE: envelope encryption of personal data. Every value gets a fresh
/// AES-256-GCM data key; the data key is wrapped by the key-encryption key version the server
/// selects, and that version is returned so it is persisted next to the ciphertext.
/// </summary>
public interface IPiiEnvelopeProtector
{
    Task<PiiProtectedBatch> ProtectAsync(
        PiiBinding binding,
        IReadOnlyList<PiiPlaintext> values,
        CancellationToken cancellationToken);

    Task<string> UnprotectAsync(
        PiiBinding binding,
        string purpose,
        byte[] ciphertext,
        string keyVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// The key-encryption key. <see cref="GetCurrentKeyVersionAsync"/> is the server-side choice of
/// version; wrap and unwrap always name the exact version, so data protected under an earlier
/// version stays readable after rotation.
/// </summary>
public interface IPiiKeyWrapClient
{
    Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken);

    Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken);

    Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken);
}

/// <summary>
/// The protector could not run. Callers publish 503 and write nothing; the message never carries
/// the value being protected.
/// </summary>
public sealed class PiiProtectionUnavailableException(Exception? innerException = null)
    : Exception("PII protection is unavailable.", innerException);

/// <summary>A stored ciphertext is malformed, tampered with or bound to another column or version.</summary>
public sealed class PiiCiphertextRejectedException()
    : Exception("The protected PII value was rejected.");

public sealed partial class PiiEnvelopeProtector(IPiiKeyWrapClient keyWrapClient) : IPiiEnvelopeProtector
{
    /// <summary>Envelope format v1 marker: <c>PQE</c> followed by the format byte.</summary>
    private static readonly byte[] Magic = [0x50, 0x51, 0x45, 0x01];

    private const int DataKeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaximumWrappedKeyBytes = 1024;
    private const int MaximumKeyVersionLength = 200;

    public async Task<PiiProtectedBatch> ProtectAsync(
        PiiBinding binding,
        IReadOnlyList<PiiPlaintext> values,
        CancellationToken cancellationToken)
    {
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
            var keyVersion = await keyWrapClient.GetCurrentKeyVersionAsync(cancellationToken).ConfigureAwait(false);
            ValidateKeyVersion(keyVersion);
            var ciphertexts = await Task.WhenAll(values.Select(value =>
                ProtectOneAsync(binding, value, keyVersion, cancellationToken))).ConfigureAwait(false);
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

    public async Task<string> UnprotectAsync(
        PiiBinding binding,
        string purpose,
        byte[] ciphertext,
        string keyVersion,
        CancellationToken cancellationToken)
    {
        ValidatePurpose(purpose);
        if (!binding.IsValid)
        {
            throw new PiiCiphertextRejectedException();
        }

        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!IsValidKeyVersion(keyVersion) || !TryParse(ciphertext, out var envelope))
        {
            throw new PiiCiphertextRejectedException();
        }

        byte[] dataKey;
        try
        {
            dataKey = await keyWrapClient.UnwrapKeyAsync(keyVersion, envelope.WrappedKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PiiProtectionUnavailableException(exception);
        }

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

    private async Task<byte[]> ProtectOneAsync(
        PiiBinding binding,
        PiiPlaintext value,
        string keyVersion,
        CancellationToken cancellationToken)
    {
        var dataKey = RandomNumberGenerator.GetBytes(DataKeyBytes);
        var plaintext = Encoding.UTF8.GetBytes(value.Value);
        try
        {
            var wrappedKey = await keyWrapClient.WrapKeyAsync(keyVersion, dataKey, cancellationToken)
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

    private static bool TryParse(byte[] ciphertext, out Envelope envelope)
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

    private static void ValidatePurpose(string purpose)
    {
        if (purpose is null || !PurposePattern().IsMatch(purpose))
        {
            throw new ArgumentException("The PII purpose must be a lower-case column identifier.", nameof(purpose));
        }
    }

    private static void ValidateKeyVersion(string keyVersion)
    {
        if (!IsValidKeyVersion(keyVersion))
        {
            throw new PiiProtectionUnavailableException();
        }
    }

    private static bool IsValidKeyVersion(string? keyVersion) =>
        !string.IsNullOrWhiteSpace(keyVersion) &&
        keyVersion.Length <= MaximumKeyVersionLength &&
        keyVersion.All(character => character is > ' ' and < (char)0x7F);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PurposePattern();

    private readonly record struct Envelope(byte[] WrappedKey, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
}
