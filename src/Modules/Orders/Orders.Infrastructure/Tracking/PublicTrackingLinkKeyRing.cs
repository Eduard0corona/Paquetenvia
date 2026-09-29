using System.Security.Cryptography;
using System.Text;
using Orders.Application.Tracking;
using Paqueteria.Contracts.Tracking;

namespace Orders.Infrastructure.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK key material for deriving public tracking tokens. Each configured key is Base64 of 32 to 128
/// random bytes (the Key Vault secret <c>public-tracking-link-key</c> in the pilot). The key never reaches the
/// database: rows keep the SHA-256 of the token and the key version, and the public lookup needs no key at all.
/// </summary>
/// <remarks>
/// Rotation adds a new version and makes it current: new generations use it, links of the previous version keep
/// resolving publicly (the lookup is by hash) and keep being re-derived for operators while the previous key stays
/// configured. Once a version is removed, the next get-or-create of such an order retires its link and issues the
/// next generation under the current key. Development and Testing may run without a configured key; they then use a
/// synthetic key derived at runtime from a public label, which is never accepted anywhere else.
/// </remarks>
public sealed class PublicTrackingLinkKeyRing
{
    private const string SyntheticKeyLabel = "paquetenvia.public-tracking-link.synthetic-key.development-and-testing-only";
    private readonly IReadOnlyDictionary<int, byte[]> keys;

    public PublicTrackingLinkKeyRing(PublicTrackingOptions options, bool allowSyntheticKey)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LinkKeys.Count > 0 && TryDecode(options, out var decoded))
        {
            keys = decoded;
            CurrentKeyVersion = options.CurrentLinkKeyVersion;
            IsSynthetic = false;
        }
        else if (options.LinkKeys.Count == 0 && allowSyntheticKey)
        {
            keys = new Dictionary<int, byte[]>
            {
                [options.CurrentLinkKeyVersion is >= 1 and <= TrackingLinkTokenDerivation.MaximumKeyVersion
                    ? options.CurrentLinkKeyVersion
                    : 1] = SHA256.HashData(Encoding.UTF8.GetBytes(SyntheticKeyLabel)),
            };
            CurrentKeyVersion = keys.Keys.Single();
            IsSynthetic = true;
        }
        else
        {
            keys = new Dictionary<int, byte[]>();
            CurrentKeyVersion = 0;
        }
    }

    public bool IsAvailable => keys.Count > 0;

    /// <summary>True only in Development and Testing when no key is configured.</summary>
    public bool IsSynthetic { get; }

    public int CurrentKeyVersion { get; }

    public bool HasKey(int keyVersion) => keys.ContainsKey(keyVersion);

    /// <summary>Derives the token of an order generation under the current key.</summary>
    public string DeriveCurrent(Guid orderId, int generation)
    {
        if (!IsAvailable)
        {
            throw new PublicTrackingTokenInfrastructureException("No public tracking link key is configured.");
        }

        return TrackingLinkTokenDerivation.DeriveToken(keys[CurrentKeyVersion], CurrentKeyVersion, orderId, generation);
    }

    /// <summary>Re-derives a stored generation, if its key version is still configured.</summary>
    public bool TryDerive(int keyVersion, Guid orderId, int generation, out string token)
    {
        token = string.Empty;
        if (!keys.TryGetValue(keyVersion, out var key))
        {
            return false;
        }

        token = TrackingLinkTokenDerivation.DeriveToken(key, keyVersion, orderId, generation);
        return true;
    }

    /// <summary>Options validation: absent keys are judged by the caller; present keys must all be valid.</summary>
    public static bool IsValid(PublicTrackingOptions options) =>
        options.LinkKeys.Count == 0 || TryDecode(options, out _);

    private static bool TryDecode(PublicTrackingOptions options, out IReadOnlyDictionary<int, byte[]> decoded)
    {
        decoded = new Dictionary<int, byte[]>();
        if (options.LinkKeys.Count is < 1 or > PublicTrackingOptions.MaximumLinkKeys ||
            !options.LinkKeys.ContainsKey(options.CurrentLinkKeyVersion))
        {
            return false;
        }

        var result = new Dictionary<int, byte[]>();
        foreach (var (version, value) in options.LinkKeys)
        {
            if (version is < 1 or > TrackingLinkTokenDerivation.MaximumKeyVersion ||
                string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            if (key.Length is < TrackingLinkTokenDerivation.MinimumKeyBytes
                or > TrackingLinkTokenDerivation.MaximumKeyBytes)
            {
                return false;
            }

            result[version] = key;
        }

        decoded = result;
        return true;
    }
}
