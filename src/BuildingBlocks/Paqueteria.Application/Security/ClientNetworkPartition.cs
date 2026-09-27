using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Paqueteria.Application.Security;

/// <summary>
/// Rate-limit partition for an anonymous client network. The address must already be the
/// client address resolved by the host's trusted forwarded-headers configuration
/// (<c>HttpContext.Connection.RemoteIpAddress</c> after <c>UseForwardedHeaders</c>), never a
/// raw header. IPv4 (including IPv4-mapped IPv6) partitions by exact address; IPv6 partitions
/// by its /64 prefix, because a single subscriber usually controls a whole /64.
/// </summary>
public static class ClientNetworkPartition
{
    private const int Ipv6PrefixBytes = 8;

    public static string Describe(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return $"ipv4:{address}";
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, Ipv6PrefixBytes, bytes.Length - Ipv6PrefixBytes);
            return $"ipv6:{new IPAddress(bytes)}/64";
        }

        return "unknown";
    }

    /// <summary>Opaque partition key so limiter state never stores the client address.</summary>
    public static string Key(IPAddress? address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"network:{Describe(address)}")));
}
