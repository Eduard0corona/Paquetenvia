using System.Security.Cryptography;
using System.Text;
using Incidents.Application.Incidents;
using Paqueteria.Infrastructure.Security.Pii;

namespace Incidents.Infrastructure.Incidents;

/// <summary>
/// The default: nothing protects the description, so nothing may be persisted. INC-001 turns this
/// into a 503 and writes no incident, evidence, reservation or audit entry at all.
/// </summary>
public sealed class DisabledIncidentPiiProtector : IIncidentPiiProtector
{
    public byte[] Protect(string plaintext, string keyVersion) =>
        throw new IncidentPiiProtectionUnavailableException();

    public Task<ProtectedIncidentDescription> ProtectAsync(string plaintext, CancellationToken cancellationToken) =>
        Task.FromException<ProtectedIncidentDescription>(new IncidentPiiProtectionUnavailableException());
}

/// <summary>
/// The deterministic synthetic protector, following the GEO-001 precedent: a one-way transform
/// that is stable for the same input and key version, so a replay reproduces the same stored
/// bytes, and never reversible to the plaintext it replaces. It is DEV_SYNTHETIC_ONLY and the
/// module refuses to select it outside Development, Testing and authorized DevSynthetic. Its key
/// version is the synthetic <c>Incidents:PiiKeyVersion</c> label chosen by the server deployment.
/// </summary>
public sealed class DeterministicMockIncidentPiiProtector(string keyVersion = DeterministicMockIncidentPiiProtector.DefaultKeyVersion)
    : IIncidentPiiProtector
{
    public const string DefaultKeyVersion = "inc001-v1";

    public string KeyVersion { get; } = keyVersion;

    public byte[] Protect(string plaintext, string keyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        return SHA256.HashData(Encoding.UTF8.GetBytes($"INC-001-MOCK\0{keyVersion}\0{plaintext}"));
    }

    public Task<ProtectedIncidentDescription> ProtectAsync(string plaintext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProtectedIncidentDescription(Protect(plaintext, KeyVersion), KeyVersion));
    }
}

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE: production protector. The description is sealed with its own
/// AES-256-GCM data key wrapped by the Key Vault key; Key Vault supplies the version and
/// <c>Incidents:PiiKeyVersion</c> is ignored. Any failure becomes
/// <see cref="IncidentPiiProtectionUnavailableException"/> (503, nothing written).
/// </summary>
public sealed class AzureKeyVaultIncidentPiiProtector(IPiiEnvelopeProtector envelope) : IIncidentPiiProtector
{
    public const string DescriptionPurpose = "incidents.description";

    public async Task<ProtectedIncidentDescription> ProtectAsync(string plaintext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        PiiProtectedBatch batch;
        try
        {
            batch = await envelope.ProtectAsync([new PiiPlaintext(DescriptionPurpose, plaintext)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IncidentPiiProtectionUnavailableException(exception);
        }

        return batch.Ciphertexts is [{ Length: > 0 } ciphertext] && !string.IsNullOrWhiteSpace(batch.KeyVersion)
            ? new ProtectedIncidentDescription(ciphertext, batch.KeyVersion)
            : throw new IncidentPiiProtectionUnavailableException();
    }
}
