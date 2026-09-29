using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.Contracts.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK: an order's public tracking token is derived, not drawn at random, so the same order and
/// generation always give back the same link and nothing but its SHA-256 has to be stored.
/// </summary>
/// <remarks>
/// <para>
/// token = Base64URL without padding of HMAC-SHA256(key, UTF-8 bytes of the canonical input), 32 bytes and
/// therefore 43 characters, the shape AI-24 <c>tracking_token</c> fixes. The stored value and the public lookup do
/// not change: SHA-256 over the exact UTF-8 bytes of the token (AI-01 invariant 6).
/// </para>
/// <para>
/// The canonical input is exact and versioned: <c>paquetenvia-trk-v1|{key_version}|{order_id}|{generation}</c>,
/// with the key version and the generation as invariant decimal integers and the order id in lowercase
/// <c>D</c> format. A new scheme gets a new prefix; the key version separates keys even if the same bytes were
/// ever configured under two versions.
/// </para>
/// </remarks>
public static class TrackingLinkTokenDerivation
{
    public const string SchemeVersion = "paquetenvia-trk-v1";
    public const int TokenBytes = 32;
    public const int TokenLength = 43;
    public const int MinimumKeyBytes = 32;
    public const int MaximumKeyBytes = 128;
    public const int MaximumKeyVersion = 32767;

    public static string CanonicalInput(int keyVersion, Guid orderId, int generation)
    {
        Validate(keyVersion, orderId, generation);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{SchemeVersion}|{keyVersion}|{orderId:D}|{generation}");
    }

    public static string DeriveToken(ReadOnlySpan<byte> key, int keyVersion, Guid orderId, int generation)
    {
        if (key.Length is < MinimumKeyBytes or > MaximumKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                "A tracking link key must hold 32 to 128 bytes.");
        }

        var input = Encoding.UTF8.GetBytes(CanonicalInput(keyVersion, orderId, generation));
        Span<byte> digest = stackalloc byte[TokenBytes];
        HMACSHA256.HashData(key, input, digest);
        var token = Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(digest);
        return token;
    }

    private static void Validate(int keyVersion, Guid orderId, int generation)
    {
        if (keyVersion is < 1 or > MaximumKeyVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion), "The key version must be 1 to 32767.");
        }

        if (orderId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(orderId), "The order id must not be empty.");
        }

        if (generation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "The generation starts at 1.");
        }
    }
}
