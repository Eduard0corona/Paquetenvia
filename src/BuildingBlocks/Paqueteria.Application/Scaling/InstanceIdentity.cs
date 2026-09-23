using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.Application.Scaling;

/// <summary>
/// Resolves the identity a replica reports when it claims work. Leases are already settled by
/// lease token, so this value never decides ownership; it keeps <c>locked_by</c> attributable
/// when several replicas of the same deployment claim from one lane.
/// </summary>
public static class InstanceIdentity
{
    public const string EnvironmentVariableName = "PAQUETERIA_INSTANCE_ID";

    public const int MaximumWorkerIdLength = 100;

    /// <summary>
    /// Length of the stable instance digest used when the configured worker id leaves no room
    /// for the readable instance identity.
    /// </summary>
    public const int InstanceDigestLength = 16;

    private const char Separator = ':';

    /// <summary>
    /// The longest configured worker id that can still be carried verbatim next to an instance
    /// digest: the remainder of the bounded value is the separator plus the digest.
    /// </summary>
    private const int MaximumBaseLength = MaximumWorkerIdLength - 1 - InstanceDigestLength;

    private static readonly Lazy<string> CurrentValue = new(() => Sanitize(
        Environment.GetEnvironmentVariable(EnvironmentVariableName) is { } configured &&
        !string.IsNullOrWhiteSpace(configured)
            ? configured
            : $"{Environment.MachineName}-{Environment.ProcessId}"));

    /// <summary>
    /// Stable for the lifetime of the process and distinct across replicas. Container
    /// orchestrators set <c>PAQUETERIA_INSTANCE_ID</c>; otherwise host and process disambiguate.
    /// </summary>
    public static string Current => CurrentValue.Value;

    /// <summary>
    /// Combines the configured deployment-wide worker id with this replica's identity, staying
    /// inside the bounded length the outbox claim contract accepts.
    /// </summary>
    public static string QualifyWorkerId(string workerId) => QualifyWorkerId(workerId, Current);

    /// <summary>
    /// Deterministic qualification against an explicit instance identity. The instance identity
    /// always survives: when the configured worker id is too long to carry it verbatim, the base
    /// is truncated and the replica is represented by a stable digest of its identity, so two
    /// replicas sharing one lane never collapse onto the same qualified id.
    /// </summary>
    public static string QualifyWorkerId(string workerId, string instanceId)
    {
        var instance = string.IsNullOrWhiteSpace(instanceId) ? "instance" : Sanitize(instanceId);
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return instance;
        }

        var sanitized = Sanitize(workerId);
        if (sanitized.Length <= MaximumBaseLength &&
            sanitized.Length + 1 + instance.Length <= MaximumWorkerIdLength)
        {
            return $"{sanitized}{Separator}{instance}";
        }

        var baseSegment = sanitized.Length <= MaximumBaseLength
            ? sanitized
            : sanitized[..MaximumBaseLength];
        return $"{baseSegment}{Separator}{Digest(instance)}";
    }

    private static string Digest(string instance) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance)))
            .ToLowerInvariant()[..InstanceDigestLength];

    private static string Sanitize(string value)
    {
        var trimmed = value.Trim();
        var buffer = new char[Math.Min(trimmed.Length, MaximumWorkerIdLength)];
        for (var index = 0; index < buffer.Length; index++)
        {
            var character = trimmed[index];
            buffer[index] = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-';
        }

        return buffer.Length == 0 ? "instance" : new string(buffer);
    }
}
