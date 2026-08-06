using System.Net;
using System.Net.Sockets;

namespace RecipeApp.Infrastructure.Recipes.Import;

/// <summary>
/// Builds the <see cref="SocketsHttpHandler"/> the import fetcher uses, with the address policy
/// enforced AT CONNECT TIME.
///
/// ── WHY THE CHECK CANNOT LIVE ONLY IN THE FETCHER ───────────────────────────────────────
/// The obvious SSRF guard is "resolve the hostname, check the addresses, then fetch", and it
/// has a hole big enough to drive the whole attack through: DNS REBINDING. Between the check
/// and the fetch, the name is resolved a SECOND time — by HttpClient — and nothing says the
/// two resolutions agree. An attacker controls the authoritative server for their own domain
/// and returns a public address with a one-second TTL for our validating lookup, then
/// 169.254.169.254 for the connection a moment later. Every check passes; the request still
/// goes to the metadata endpoint.
///
/// It is a time-of-check-to-time-of-use bug, and TOCTOU bugs are not fixed by checking more
/// carefully. They are fixed by making the check and the use the same event. <see
/// cref="SocketsHttpHandler.ConnectCallback"/> is that event: it hands over the endpoint the
/// socket is about to be opened to, AFTER resolution, and refusing there refuses the actual
/// connection rather than a prediction of it.
///
/// The fetcher still pre-validates. That is for the error message and to avoid a pointless
/// connection attempt — it is not the security boundary. This is.
///
/// Automatic redirects stay OFF here for the same class of reason: a 302 is a second URL that
/// nothing validated, and following it inside the handler would skip the fetcher's per-hop
/// check entirely.
/// </summary>
public static class GuardedHttpHandler
{
    public static SocketsHttpHandler Create(RecipeImportOptions options) =>
        new()
        {
            // Every hop is validated by the fetcher, one at a time. See the class comment.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(options.FetchTimeoutSeconds, 10)),
            // Recipe sites set cookies; carrying them between unrelated imports would be a
            // slow-motion way of building a shared browsing identity on the server.
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                if (addresses.Length == 0)
                {
                    throw new IOException($"'{host}' did not resolve to any address.");
                }

                // ALL of them, not the first. A host that resolves to one public address and
                // one private address is not a host we will talk to — round-robin DNS would
                // otherwise make the guard a coin flip, passing on most attempts and failing
                // open on the rest.
                foreach (var address in addresses)
                {
                    if (PrivateAddressPolicy.IsBlocked(address))
                    {
                        throw new IOException(
                            $"'{host}' resolves to a non-public address; refusing to connect.");
                    }
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    // Connect to the VALIDATED addresses, not to the hostname — handing the
                    // name back to the socket would mean a third resolution and reopen the
                    // very window this callback exists to close.
                    await socket.ConnectAsync(addresses, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
}
