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
    public static string QualifyWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return Current;
        }

        var qualified = $"{workerId}:{Current}";
        return qualified.Length <= MaximumWorkerIdLength
            ? qualified
            : qualified[..MaximumWorkerIdLength];
    }

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
