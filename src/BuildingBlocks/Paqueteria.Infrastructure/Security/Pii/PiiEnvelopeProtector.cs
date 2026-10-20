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
/// The wrapping half of a key-encryption key: the server-side choice of version and the wrap of one data key under
/// that exact version. NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2): the API seals the recipient-notice copies through
/// this contract only, so it holds no way to unwrap them.
/// </summary>
public interface IPiiKeyWrapper
{
    Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken);

    Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken);
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

public sealed class PiiEnvelopeProtector(IPiiKeyWrapClient keyWrapClient) : IPiiEnvelopeProtector
{
    public Task<PiiProtectedBatch> ProtectAsync(
        PiiBinding binding,
        IReadOnlyList<PiiPlaintext> values,
        CancellationToken cancellationToken) =>
        PiiEnvelopeFormat.SealAsync(new WrapperView(keyWrapClient), binding, values, cancellationToken);

    public async Task<string> UnprotectAsync(
        PiiBinding binding,
        string purpose,
        byte[] ciphertext,
        string keyVersion,
        CancellationToken cancellationToken)
    {
        PiiEnvelopeFormat.ValidatePurpose(purpose);
        if (!binding.IsValid)
        {
            throw new PiiCiphertextRejectedException();
        }

        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!PiiEnvelopeFormat.IsValidKeyVersion(keyVersion) ||
            !PiiEnvelopeFormat.TryParse(ciphertext, out var envelope))
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

        return PiiEnvelopeFormat.Open(binding, purpose, keyVersion, envelope, dataKey);
    }

    /// <summary>The wrapping half of the full client, so sealing never sees the unwrap operation.</summary>
    private sealed class WrapperView(IPiiKeyWrapClient client) : IPiiKeyWrapper
    {
        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) =>
            client.GetCurrentKeyVersionAsync(cancellationToken);

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
            client.WrapKeyAsync(keyVersion, dataKey, cancellationToken);
    }
}
