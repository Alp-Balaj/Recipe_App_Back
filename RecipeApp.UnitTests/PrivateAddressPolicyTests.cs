using System.Net;
using RecipeApp.Infrastructure.Recipes.Import;

namespace RecipeApp.UnitTests;

// Stream L's SSRF guard. This is the highest-consequence code in the stream — a gap here is
// not a broken import, it is the server fetching its own credentials on a stranger's behalf —
// and it is pure, so it can be tested exhaustively rather than sampled.
public class PrivateAddressPolicyTests
{
    // THE ONE THAT MATTERS MOST. 169.254.169.254 is the cloud metadata endpoint: it hands out
    // instance credentials to anything that asks, and it is neither loopback nor RFC-1918, so
    // a guard built from IPAddress.IsLoopback plus a private-range check misses it entirely.
    [Fact]
    public void Blocks_the_cloud_metadata_endpoint()
    {
        Assert.True(PrivateAddressPolicy.IsBlocked(IPAddress.Parse("169.254.169.254")));
    }

    [Theory]
    // Loopback — the whole /8, not just 127.0.0.1.
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    // "This network". 0.0.0.0 reaches localhost on Linux.
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    // RFC 1918 private space.
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    // Link-local, all of it.
    [InlineData("169.254.0.1")]
    // Carrier-grade NAT — a real internal range on some hosts.
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    // Documentation and benchmarking ranges.
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    // Multicast, reserved, broadcast.
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    public void Blocks_non_public_ipv4(string address)
    {
        Assert.True(PrivateAddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    // Adjacent to blocked ranges on both sides — these pin that the boundaries are right
    // rather than merely that the middle of each range is covered.
    [InlineData("11.0.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("192.0.1.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.255")]
    public void Allows_public_ipv4(string address)
    {
        Assert.False(PrivateAddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    // ── THE BYPASS THAT LOOKS LIKE NOTHING ──────────────────────────────────────────────
    // ::ffff:127.0.0.1 is loopback wearing an IPv6 type. IPAddress.IsLoopback answers FALSE
    // for it, and so does every IsIPv6* predicate, so a guard that dispatches on address
    // family without unwrapping the mapping lets the whole v4 deny-list be walked around by
    // writing the address differently.
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    public void Blocks_ipv4_mapped_ipv6(string address)
    {
        Assert.True(PrivateAddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Fact]
    public void Allows_a_public_address_written_as_ipv4_mapped_ipv6()
    {
        Assert.False(PrivateAddressPolicy.IsBlocked(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Theory]
    // Unspecified — routes to localhost.
    [InlineData("::")]
    // Loopback.
    [InlineData("::1")]
    // Unique local (fc00::/7).
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    // Link-local (fe80::/10).
    [InlineData("fe80::1")]
    // Multicast.
    [InlineData("ff02::1")]
    // NAT64, which would translate to an IPv4 destination this policy never gets to judge.
    [InlineData("64:ff9b::7f00:1")]
    public void Blocks_non_public_ipv6(string address)
    {
        Assert.True(PrivateAddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    public void Allows_public_ipv6(string address)
    {
        Assert.False(PrivateAddressPolicy.IsBlocked(IPAddress.Parse(address)));
    }
}
