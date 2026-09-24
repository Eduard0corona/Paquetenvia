using System.Security.Cryptography;
using System.Text;
using Incidents.Application.Incidents;

namespace Incidents.Infrastructure.Incidents;

/// <summary>
/// The default: nothing protects the description, so nothing may be persisted. INC-001 turns this
/// into a 503 and writes no incident, evidence, reservation or audit entry at all.
/// </summary>
public sealed class DisabledIncidentPiiProtector : IIncidentPiiProtector
{
    public byte[] Protect(string plaintext, string keyVersion) =>
        throw new IncidentPiiProtectionUnavailableException();
}

/// <summary>
/// The deterministic synthetic protector, following the GEO-001 precedent: a one-way transform
/// that is stable for the same input and key version, so a replay reproduces the same stored
/// bytes, and never reversible to the plaintext it replaces. It is DEV_SYNTHETIC_ONLY and the
/// module refuses to select it outside Development, Testing and authorized DevSynthetic.
/// </summary>
public sealed class DeterministicMockIncidentPiiProtector : IIncidentPiiProtector
{
    public byte[] Protect(string plaintext, string keyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);
        return SHA256.HashData(Encoding.UTF8.GetBytes($"INC-001-MOCK\0{keyVersion}\0{plaintext}"));
    }
}
