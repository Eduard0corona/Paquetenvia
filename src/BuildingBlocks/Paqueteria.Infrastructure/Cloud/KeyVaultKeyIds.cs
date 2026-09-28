using System.Text.RegularExpressions;

namespace Paqueteria.Infrastructure.Cloud;

/// <summary>Parses the versionless Key Vault key URIs the ADP-001 adapters are configured with.</summary>
public static partial class KeyVaultKeyIds
{
    /// <summary>
    /// Accepts only <c>https://{host}/keys/{name}</c> with no version, query, fragment, user info or
    /// port: the version is always resolved by the server from Key Vault.
    /// </summary>
    public static bool TryParseVersionless(string? value, out Uri vaultUri, out string keyName)
    {
        vaultUri = null!;
        keyName = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments[0] != "keys" || !IsKeyName(segments[1]))
        {
            return false;
        }

        vaultUri = new Uri($"https://{uri.Host}/");
        keyName = segments[1];
        return true;
    }

    public static bool IsKeyName(string? value) => value is not null && KeyNamePattern().IsMatch(value);

    [GeneratedRegex("^[0-9A-Za-z-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyNamePattern();
}
