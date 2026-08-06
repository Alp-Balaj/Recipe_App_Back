using System.Net;
using System.Net.Sockets;

namespace RecipeApp.Infrastructure.Recipes.Import;

/// <summary>
/// Decides whether an IP address is one this server will make an outbound request to
/// (stream L's SSRF guard).
///
/// Import is the first feature in this backend that fetches an address a USER chose, which
/// makes it the first place server-side request forgery is possible at all. The asymmetry that
/// makes it worth this much code: the server sits inside a private network, holds credentials,
/// and — on most hosting — can reach a cloud metadata endpoint that hands out those
/// credentials to anything that asks. A user has none of that. "Fetch this URL for me" borrows
/// the server's position on the network, so every address it can reach and the caller cannot
/// has to be refused here.
///
/// DENY-LIST BY RANGE, and the ranges are enumerated rather than reduced to
/// <c>IPAddress.IsLoopback</c> plus a couple of checks, because the interesting ones are not
/// the obvious ones. 169.254.169.254 (the metadata endpoint) is link-local, not loopback or
/// private, and is the single most valuable target on the list. 0.0.0.0 routes to localhost on
/// Linux. 100.64/10 is carrier-grade NAT and is a real internal range on some hosts. An
/// IPv4-mapped IPv6 address (<c>::ffff:127.0.0.1</c>) is loopback wearing a different type,
/// and every v6 check in the BCL answers false for it.
/// </summary>
public static class PrivateAddressPolicy
{
    /// <summary>
    /// True when the address must not be connected to. Errs toward refusal: an address family
    /// this method does not understand is refused rather than allowed, because the failure
    /// modes are not symmetrical — wrongly refusing an import costs the user a recipe, wrongly
    /// allowing one costs the server its credentials.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        // An IPv4 address delivered as ::ffff:a.b.c.d must be judged as the IPv4 address it is.
        // Skipping this is the classic bypass: IsLoopback and the v6 predicates all answer
        // false for ::ffff:127.0.0.1.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(address),
            AddressFamily.InterNetworkV6 => IsBlockedV6(address),
            _ => true,
        };
    }

    private static bool IsBlockedV4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes[0] switch
        {
            // 0.0.0.0/8 — "this network". 0.0.0.0 reaches localhost on Linux.
            0 => true,
            // 10.0.0.0/8 — private.
            10 => true,
            // 127.0.0.0/8 — loopback. The whole /8, not just 127.0.0.1.
            127 => true,
            // 169.254.0.0/16 — link-local, and the home of 169.254.169.254. THE one to get right.
            169 when bytes[1] == 254 => true,
            // 100.64.0.0/10 — carrier-grade NAT.
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
            // 172.16.0.0/12 — private.
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            // 192.168.0.0/16 private; 192.0.0.0/24 IETF protocol assignments; 192.0.2.0/24 TEST-NET-1.
            192 when bytes[1] == 168 || (bytes[1] == 0 && bytes[2] is 0 or 2) => true,
            // 198.18.0.0/15 benchmarking; 198.51.100.0/24 TEST-NET-2.
            198 when (bytes[1] is 18 or 19) || (bytes[1] == 51 && bytes[2] == 100) => true,
            // 203.0.113.0/24 — TEST-NET-3.
            203 when bytes[1] == 0 && bytes[2] == 113 => true,
            // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast.
            >= 224 => true,
            _ => false,
        };
    }

    private static bool IsBlockedV6(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        // :: (unspecified) — routes to localhost.
        if (bytes.All(b => b == 0))
        {
            return true;
        }

        // 64:ff9b::/96 — NAT64. Translates to an IPv4 destination that this policy would then
        // never get to judge, so the whole prefix is refused rather than unwrapped.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
        {
            return true;
        }

        return false;
    }
}
