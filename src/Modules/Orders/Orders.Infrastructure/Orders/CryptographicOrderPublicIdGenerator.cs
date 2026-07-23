using System.Security.Cryptography;
using Orders.Application.Orders;
using Orders.Domain;

namespace Orders.Infrastructure.Orders;

public sealed class CryptographicOrderPublicIdGenerator : IOrderPublicIdGenerator
{
    public string Create()
    {
        Span<byte> entropy = stackalloc byte[OrderPublicIdPolicy.EntropyBytes];
        RandomNumberGenerator.Fill(entropy);
        var encoded = Convert.ToBase64String(entropy)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return OrderPublicIdPolicy.Prefix + encoded;
    }
}
