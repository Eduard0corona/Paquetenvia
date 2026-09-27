using System.Net;
using Paqueteria.Application.Security;

namespace Paqueteria.UnitTests.Security;

public sealed class ClientNetworkPartitionTests
{
    [Fact]
    public void Ipv4_clients_are_partitioned_by_exact_address()
    {
        Assert.Equal("ipv4:203.0.113.10", ClientNetworkPartition.Describe(IPAddress.Parse("203.0.113.10")));
        Assert.NotEqual(
            ClientNetworkPartition.Describe(IPAddress.Parse("203.0.113.10")),
            ClientNetworkPartition.Describe(IPAddress.Parse("203.0.113.11")));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_the_same_partition_as_ipv4()
    {
        Assert.Equal(
            ClientNetworkPartition.Describe(IPAddress.Parse("203.0.113.10")),
            ClientNetworkPartition.Describe(IPAddress.Parse("::ffff:203.0.113.10")));
    }

    [Fact]
    public void Ipv6_clients_share_one_partition_per_64_prefix()
    {
        var first = ClientNetworkPartition.Describe(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var sameSubnet = ClientNetworkPartition.Describe(IPAddress.Parse("2001:db8:1:2::1"));
        var otherSubnet = ClientNetworkPartition.Describe(IPAddress.Parse("2001:db8:1:3::1"));

        Assert.Equal("ipv6:2001:db8:1:2::/64", first);
        Assert.Equal(first, sameSubnet);
        Assert.NotEqual(first, otherSubnet);
    }

    [Fact]
    public void Unknown_address_has_a_single_explicit_partition()
    {
        Assert.Equal("unknown", ClientNetworkPartition.Describe(null));
    }

    [Fact]
    public void Partition_key_is_an_opaque_hash_without_the_address()
    {
        var key = ClientNetworkPartition.Key(IPAddress.Parse("203.0.113.10"));

        Assert.Equal(64, key.Length);
        Assert.DoesNotContain("203", key, StringComparison.Ordinal);
        Assert.Equal(key, ClientNetworkPartition.Key(IPAddress.Parse("::ffff:203.0.113.10")));
    }
}
