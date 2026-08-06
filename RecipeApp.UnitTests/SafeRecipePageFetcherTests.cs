using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Infrastructure.Recipes.Import;

namespace RecipeApp.UnitTests;

// Stream L. The URL-level half of the SSRF guard — the checks the connect callback cannot make
// because by the time it runs there is no URL left, only a host and a port.
//
// Every case below is refused BEFORE any socket is opened, which the failing handler proves:
// it throws on any request at all, so a test that gets a clean RecipeFetchException instead of
// that throw has demonstrated the refusal happened first.
public class SafeRecipePageFetcherTests
{
    // Any attempt to actually send is a test failure — see the class note.
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"The fetcher opened a connection to {request.RequestUri} that it should have refused.");
    }

    private static SafeRecipePageFetcher Build() =>
        new(new HttpClient(new ExplodingHandler()),
            new RecipeImportOptions(),
            NullLogger<SafeRecipePageFetcher>.Instance);

    private static async Task<RecipeFetchException> RefusesAsync(string url) =>
        await Assert.ThrowsAsync<RecipeFetchException>(
            () => Build().FetchPageAsync(new Uri(url)));

    // file:// would turn "import a recipe" into an arbitrary file read; the others are simply
    // not web pages and have no business being fetched by this feature.
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/recipe")]
    [InlineData("gopher://example.test/recipe")]
    public async Task Refuses_a_non_http_scheme(string url)
    {
        var ex = await RefusesAsync(url);

        Assert.True(ex.IsPolicyRejection);
    }

    // http://real-looking-host@evil.test/ — everything before the @ is credentials, not a host.
    // In a pasted URL this exists almost exclusively to mislead the human reading it.
    [Fact]
    public async Task Refuses_embedded_credentials()
    {
        var ex = await RefusesAsync("https://recipes.example.test@evil.test/page");

        Assert.True(ex.IsPolicyRejection);
    }

    // A literal address can be judged without a lookup, so it is refused here rather than at
    // connect time — a better error, and no wasted connection.
    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fd00::1]/")]
    public async Task Refuses_a_literal_private_address(string url)
    {
        var ex = await RefusesAsync(url);

        Assert.True(ex.IsPolicyRejection);
    }

    // A public literal address is NOT refused up front — it goes to the handler, which here
    // explodes. That is the assertion: the guard did not over-block.
    [Fact]
    public async Task Allows_a_literal_public_address_through_to_the_connection()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build().FetchPageAsync(new Uri("http://93.184.216.34/recipe")));
    }

    // A hostname is deliberately NOT resolved here. Resolving to validate would be the
    // time-of-check-to-time-of-use bug GuardedHttpHandler exists to close, so this class must
    // pass a name straight through to the connection.
    [Fact]
    public async Task Passes_a_hostname_through_without_resolving_it()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build().FetchPageAsync(new Uri("https://recipes.example.test/page")));
    }

    // An image fetch NEVER throws — a recipe without its photo is still the recipe. Contrast
    // the page fetch above, where there is no recipe at all without the page.
    [Theory]
    [InlineData("http://169.254.169.254/image.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://recipes.example.test/photo.jpg")]
    public async Task Image_fetch_returns_null_instead_of_throwing(string url)
    {
        Assert.Null(await Build().FetchImageAsync(new Uri(url)));
    }
}
