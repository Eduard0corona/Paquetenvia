using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.Infrastructure.Security.Pii;

/// <summary>
/// Shared settings of the production PII protector. The modules still select their protector with
/// <c>Locations:PiiProtector</c> and <c>Incidents:PiiProtector</c>; this section only describes the
/// Key Vault key they use when either selects <c>AzureKeyVault</c>.
/// </summary>
public sealed class PiiProtectionOptions
{
    public const string SectionName = "PiiProtection";

    public AzureKeyVaultPiiOptions AzureKeyVault { get; set; } = new();
}

public sealed class AzureKeyVaultPiiOptions
{
    /// <summary>Versionless key URI, <c>https://{vault}.vault.azure.net/keys/{name}</c>.</summary>
    public string KeyId { get; set; } = string.Empty;

    public int CurrentVersionRefreshSeconds { get; set; } = 300;

    public int HealthCacheSeconds { get; set; } = 300;
}

public static class PiiProtectionOptionsValidator
{
    public static bool IsValid(PiiProtectionOptions? options) =>
        options?.AzureKeyVault is { } keyVault &&
        KeyVaultKeyIds.TryParseVersionless(keyVault.KeyId, out _, out _) &&
        keyVault.CurrentVersionRefreshSeconds is >= 30 and <= 3_600 &&
        keyVault.HealthCacheSeconds is >= 30 and <= 3_600;
}
