namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// Retrieves a user-supplied URL on the server's behalf (stream L).
///
/// THIS INTERFACE EXISTS BECAUSE THE FETCH IS DANGEROUS, not because it is complicated.
/// "Given a URL, fetch the page" is server-side request forgery written out as a feature: the
/// caller chooses the address and the server has credentials, a private network and a cloud
/// metadata endpoint that the caller does not. Concentrating every outbound fetch behind one
/// seam means the guard is written once and cannot be forgotten by the second call site — and
/// there IS a second call site, because re-hosting the source image fetches another
/// user-influenced URL.
///
/// It is also the seam the integration suite replaces, so the test host never makes an
/// outbound request.
/// </summary>
public interface IRecipePageFetcher
{
    /// <summary>
    /// Fetches an HTML page. Throws <see cref="RecipeFetchException"/> — never returns a
    /// partial or empty success — so a caller cannot accidentally treat a failed fetch as an
    /// empty page and go on to report "no recipe found" for a site that was simply down.
    /// </summary>
    Task<FetchedPage> FetchPageAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches an image for re-hosting. Returns null when the image is missing, too large, or
    /// not an image at all: unlike the page fetch, a failure here is NOT fatal to the import.
    /// A recipe without its photo is still the recipe, and refusing the whole import because a
    /// CDN 403'd one JPEG would trade the user's actual goal for a cosmetic one.
    /// </summary>
    Task<FetchedImage?> FetchImageAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <param name="Html">The decoded response body.</param>
/// <param name="FinalUrl">
/// The address the content actually came from after redirects. This — not the URL the user
/// pasted — is what gets stored as provenance: a shortener or a tracking wrapper is not where
/// the recipe lives, and D15 promises the reader a source domain that means something.
/// </param>
public sealed record FetchedPage(string Html, Uri FinalUrl);

public sealed record FetchedImage(byte[] Content, string ContentType);

/// <summary>
/// A fetch that did not happen or did not succeed. <see cref="IsPolicyRejection"/> separates
/// "this server will not fetch that" (the caller's fault, a 400) from "that fetch failed"
/// (somebody else's server, a 502).
/// </summary>
public sealed class RecipeFetchException : Exception
{
    public RecipeFetchException(string message, bool isPolicyRejection, Exception? innerException = null)
        : base(message, innerException)
    {
        IsPolicyRejection = isPolicyRejection;
    }

    public bool IsPolicyRejection { get; }

    /// <summary>The URL names something this server refuses to reach. Message is client-safe.</summary>
    public static RecipeFetchException Rejected(string message) => new(message, isPolicyRejection: true);

    /// <summary>The fetch was attempted and failed. Message is client-safe.</summary>
    public static RecipeFetchException Unreachable(string message, Exception? inner = null) =>
        new(message, isPolicyRejection: false, inner);
}
