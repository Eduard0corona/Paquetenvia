using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.ContractTests.Cryptography;

internal static class TrackingTokenReference
{
    public const int EntropyBytes = 32;

    public static string Encode(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != EntropyBytes)
        {
            throw new ArgumentException($"Tracking token entropy must be exactly {EntropyBytes} bytes.", nameof(entropy));
        }

        return Convert.ToBase64String(entropy).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string encodedToken)
    {
        ArgumentNullException.ThrowIfNull(encodedToken);
        return SHA256.HashData(Encoding.UTF8.GetBytes(encodedToken));
    }
}
