using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Recipes.Abstractions;

namespace RecipeApp.Infrastructure.Recipes.Import;

/// <summary>
/// The real <see cref="IRecipePageFetcher"/>: retrieves a user-supplied URL under the address
/// policy, following redirects by hand and refusing to read more than a bounded amount.
///
/// The security boundary is <see cref="GuardedHttpHandler"/>'s connect callback, not this class
/// — see that file for why a pre-flight DNS check alone is defeated by rebinding. What lives
/// HERE is everything the callback cannot see: the scheme, the credentials in the URL, the
/// per-hop redirect validation, the size ceiling, and the content type.
/// </summary>
public sealed class SafeRecipePageFetcher : IRecipePageFetcher
{
    private static readonly string[] AcceptablePageTypes =
        ["text/html", "application/xhtml+xml", "application/xml", "text/plain"];

    private readonly HttpClient _http;
    private readonly RecipeImportOptions _options;
    private readonly ILogger<SafeRecipePageFetcher> _logger;

    public SafeRecipePageFetcher(HttpClient http, RecipeImportOptions options, ILogger<SafeRecipePageFetcher> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<FetchedPage> FetchPageAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var (response, finalUrl) = await SendFollowingRedirectsAsync(url, cancellationToken);

        using (response)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AcceptablePageTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            {
                throw RecipeFetchException.Unreachable(
                    $"That address returned {(mediaType.Length == 0 ? "no content type" : mediaType)}, not a web page.");
            }

            var bytes = await ReadBoundedAsync(response.Content, _options.MaxPageBytes, cancellationToken);
            return new FetchedPage(Decode(bytes, response.Content.Headers.ContentType), finalUrl);
        }
    }

    public async Task<FetchedImage?> FetchImageAsync(Uri url, CancellationToken cancellationToken = default)
    {
        // Every failure here is swallowed. A recipe without its photo is still the recipe, and
        // an import that 502'd because a CDN rate-limited one JPEG would trade what the user
        // actually asked for against something cosmetic. Contrast FetchPageAsync, which throws:
        // there is no recipe at all without the page.
        try
        {
            var (response, _) = await SendFollowingRedirectsAsync(url, cancellationToken);

            using (response)
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var bytes = await ReadBoundedAsync(response.Content, _options.MaxImageBytes, cancellationToken);
                return new FetchedImage(bytes, mediaType);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not fetch source image {Url}; importing without it.", url);
            return null;
        }
    }

    private async Task<(HttpResponseMessage Response, Uri FinalUrl)> SendFollowingRedirectsAsync(
        Uri url, CancellationToken cancellationToken)
    {
        var current = Validate(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.FetchTimeoutSeconds));

        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,image/*;q=0.8,*/*;q=0.5");

            HttpResponseMessage response;
            try
            {
                // ResponseHeadersRead so the size ceiling can be enforced while the body
                // streams, rather than after the whole thing is already in memory.
                response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw RecipeFetchException.Unreachable("That address took too long to respond.");
            }
            catch (HttpRequestException ex)
            {
                // The connect callback's refusal arrives here as an IOException wrapped in an
                // HttpRequestException. It is reported as unreachable rather than rejected:
                // by this point a lookup has happened, and distinguishing "your DNS pointed
                // somewhere private" from "the site is down" in the response would confirm
                // internal addresses to whoever is probing.
                throw RecipeFetchException.Unreachable("That address could not be reached.", ex);
            }

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();

                if (location is null)
                {
                    throw RecipeFetchException.Unreachable("That address redirected without saying where.");
                }

                // Relative Locations are legal and common.
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);

                // EVERY hop is re-validated. A public URL that 302s to http://169.254.169.254
                // is the entire attack, and it is why AllowAutoRedirect is off.
                current = Validate(next);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw RecipeFetchException.Unreachable($"That address returned HTTP {status}.");
            }

            return (response, current);
        }

        throw RecipeFetchException.Unreachable("That address redirected too many times.");
    }

    /// <summary>
    /// The checks the connect callback cannot make, because by then there is no URL left — only
    /// a host and a port.
    /// </summary>
    private static Uri Validate(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            throw RecipeFetchException.Rejected("That is not a complete web address.");
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            // file://, ftp://, gopher:// and data: are all fetchable by something, somewhere,
            // and none of them is a recipe page. file:// in particular would turn this feature
            // into an arbitrary file read.
            throw RecipeFetchException.Rejected("Only http and https addresses can be imported.");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            // http://expected-host@evil.test/ — the part before the @ is credentials, not the
            // host, and it exists in a pasted URL almost exclusively to mislead a human
            // reading it.
            throw RecipeFetchException.Rejected("Web addresses with embedded credentials cannot be imported.");
        }

        // A literal IP can be judged now, without a lookup. A hostname is left to the connect
        // callback, which is the only place the answer cannot go stale.
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal)
            && PrivateAddressPolicy.IsBlocked(literal))
        {
            throw RecipeFetchException.Rejected("That address is not publicly reachable.");
        }

        return url;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/>, counting what actually arrives.
    ///
    /// Content-Length is consulted as a fast rejection but is never TRUSTED as the limit: it is
    /// a claim by the remote server, it is absent under chunked encoding, and a decompression
    /// bomb reports a small one honestly. The running total is what binds.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > maxBytes)
        {
            throw RecipeFetchException.Unreachable("That page is too large to import.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();

        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw RecipeFetchException.Unreachable("That page is too large to import.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes the body using the charset the server declared, falling back to UTF-8.
    /// Getting this wrong is not cosmetic — a Latin-1 page read as UTF-8 turns every accented
    /// character in "crème brûlée" into a replacement character, and that corruption is then
    /// stored in the recipe permanently.
    /// </summary>
    private static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet?.Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // An unregistered or misspelled charset — fall through to UTF-8.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
