using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.Contracts.Tracking;

public sealed class TrackingTokenHasher
{
    private const int EntropyBytes = 32;

    public string CreateToken()
    {
        Span<byte> entropy = stackalloc byte[EntropyBytes];
        RandomNumberGenerator.Fill(entropy);
        return Convert.ToBase64String(entropy)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public byte[] HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}
