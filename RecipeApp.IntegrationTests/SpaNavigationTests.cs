using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace RecipeApp.IntegrationTests;

// Publish fix (2026-07-23): SPA routes that share a path with a root API route (/feed,
// /users/:id, /recipes/:id) must serve index.html on a cold browser GET (Accept:
// text/html) instead of matching the API endpoint and 401ing before the SPA — and its
// redirect-to-login guard — can load. The shared factory has no wwwroot, so this one
// fabricates a temp web root with a stub index.html, the shape the Dockerfile ships.
public class SpaWebRootFactory : IntegrationTestFactory
{
    public const string IndexMarker = "<!-- spa-index-stub -->";

    private readonly string _webRoot = Path.Combine(
        Path.GetTempPath(), $"recipeapp-tests-wwwroot-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(
            Path.Combine(_webRoot, "index.html"),
            $"<!doctype html><html><body>{IndexMarker}</body></html>");
        builder.UseWebRoot(_webRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (Directory.Exists(_webRoot))
        {
            Directory.Delete(_webRoot, recursive: true);
        }
    }
}

public class SpaNavigationTests(SpaWebRootFactory factory) : IClassFixture<SpaWebRootFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // What a real address-bar navigation sends (Chromium's default Accept header) —
    // distinguishes it from client.ts fetches, which go through /api/* with JSON accepts.
    private static HttpRequestMessage BrowserGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return request;
    }

    [Theory]
    [InlineData("/feed")]                                          // the reported bug
    [InlineData("/users/0f8fad5b-d9cb-469f-a165-70867728950e")]    // collides with GET /users/{id:guid}
    [InlineData("/recipes/7c9e6679-7425-40de-944b-e07fc1f90ae7")]  // collides with GET /recipes/{id:guid}
    [InlineData("/login")]                                         // no collision — must keep working
    public async Task BrowserNavigation_Anonymous_ServesIndexHtml(string path)
    {
        var response = await _client.SendAsync(BrowserGet(path));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(SpaWebRootFactory.IndexMarker, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Feed_ApiPrefixed_IsNeverTreatedAsNavigation()
    {
        // Even with a browser Accept header, /api/* stays an API route. GET /feed is
        // anonymous-capable now (guest access), so the proof it hit the API and not the
        // SPA fallback is the JSON body, not a 401.
        var response = await _client.SendAsync(BrowserGet("/api/feed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Feed_WithoutHtmlAccept_StillRoutesToApi()
    {
        // No text/html in Accept (client.ts, curl) → the root API route answers as before
        // (200 JSON since guest access — the assertion is that it is NOT index.html).
        var response = await _client.GetAsync("/feed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Health_WithHtmlAccept_StaysBrowserInspectable()
    {
        // /health is exempt from the navigation rewrite so it can be opened in a browser.
        var response = await _client.SendAsync(BrowserGet("/health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }
}
