namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-20): turn a User-Agent into something a person can recognise on the
// active-devices list.
//
// This is deliberately crude and must stay that way. The list answers ONE question — "is
// that one me?" — and "Chrome on Windows" answers it. Parsing the string properly means
// carrying a device database and keeping it current, for a screen whose reader already knows
// what they own. Anything unrecognised reads as "Unknown device", which is honest: a wrong
// guess on this screen could talk somebody out of dropping the session they should drop.
internal static class DeviceLabel
{
    public static string From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown device";
        }

        var browser = Browser(userAgent);
        var platform = Platform(userAgent);

        if (browser is null && platform is null) return "Unknown device";
        if (browser is null) return platform!;
        if (platform is null) return browser;
        return $"{browser} on {platform}";
    }

    // Order matters: every one of these ships "Safari" in its own UA, and Edge and Opera also
    // ship "Chrome". Narrowest claim first.
    private static string? Browser(string ua) =>
        Has(ua, "Edg/") ? "Edge"
        : Has(ua, "OPR/") || Has(ua, "Opera") ? "Opera"
        : Has(ua, "Firefox") ? "Firefox"
        : Has(ua, "Chrome") || Has(ua, "CriOS") ? "Chrome"
        : Has(ua, "Safari") ? "Safari"
        : null;

    // "Android" before "Linux" for the same reason: an Android UA also says Linux.
    private static string? Platform(string ua) =>
        Has(ua, "Android") ? "Android"
        : Has(ua, "iPhone") ? "iPhone"
        : Has(ua, "iPad") ? "iPad"
        : Has(ua, "Windows") ? "Windows"
        : Has(ua, "Mac OS X") || Has(ua, "Macintosh") ? "macOS"
        : Has(ua, "Linux") ? "Linux"
        : null;

    private static bool Has(string ua, string needle) =>
        ua.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
