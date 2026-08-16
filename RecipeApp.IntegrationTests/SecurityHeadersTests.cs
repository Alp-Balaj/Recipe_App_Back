using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RecipeApp.API;

namespace RecipeApp.IntegrationTests;

/// <summary>
/// Accounts (KAN-19) — the security headers, asserted on real responses.
///
/// The reason these are worth testing at all is that the middleware's placement is what makes
/// them work, and placement is the kind of thing a later edit moves without noticing. So the
/// assertions deliberately span response KINDS rather than repeating one: an API 200, an
/// anonymous endpoint, a 401 from the auth pipeline, and an error response — the last two
/// being the ones a naive "set the headers on the way in" implementation loses.
/// </summary>
public class SecurityHeadersTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private readonly IntegrationTestFactory _factory = factory;

    private static void AssertCoreHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));

        var csp = Single(response, "Content-Security-Policy");
        // The half that protects the session token in localStorage: no inline script, ever.
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        // Clickjacking, in the modern spelling.
        Assert.Contains("frame-ancestors 'none'", csp);
        // And the deliberate weakness, asserted so that relaxing it further is a visible edit:
        // inline STYLES are this codebase's idiom and the policy has to admit them.
        Assert.Contains("style-src 'self' 'unsafe-inline'", csp);
    }

    // The SPA's whole typography arrives from Google Fonts — index.css opens with two
    // @import lines. A CSP that forgets either host degrades every screen to system fonts
    // and reports nothing anywhere: no error, no log line, no failing request the server can
    // see. This is the assertion that turns that into a red test instead.
    [Fact]
    public async Task ThePolicy_StillAdmitsTheFontsTheSpaActuallyLoads()
    {
        var client = _factory.CreateClient();
        var csp = Single(await client.GetAsync("/health"), "Content-Security-Policy");

        Assert.Contains("https://fonts.googleapis.com", csp);
        Assert.Contains("https://fonts.gstatic.com", csp);
    }

    private static string Single(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : response.Content.Headers.TryGetValues(name, out var contentValues)
                ? string.Join(",", contentValues)
                : "";

    [Fact]
    public async Task AnOrdinaryApiResponse_CarriesTheSecurityHeaders()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCoreHeaders(response);
    }

    // A 401 is produced by the auth pipeline before any endpoint runs, so it is a different
    // path through the middleware than the one above.
    [Fact]
    public async Task AnUnauthorizedResponse_CarriesTheSecurityHeaders()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertCoreHeaders(response);
    }

    // The SPA fallback and the /images mount are STATIC-FILE pipelines, which sit outside
    // endpoint routing entirely. A 404 from there still goes through the middleware above them.
    [Fact]
    public async Task AStaticFilePathResponse_CarriesTheSecurityHeaders()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/images/does-not-exist.jpg");

        AssertCoreHeaders(response);
    }

    // The one that actually neutralises a polyglot upload, asserted on the mount that serves
    // user-supplied bytes. Set on that mount directly as well as globally, so it survives a
    // future reordering of the pipeline.
    [Fact]
    public async Task TheImageMount_MarksItsResponsesNosniff()
    {
        var client = _factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var upload = await UploadOnePixelPngAsync(client);
        var url = upload.Url;

        // Local-disk mode in tests, so the URL is the /images path the mount serves.
        var served = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("nosniff", Single(served, "X-Content-Type-Options"));
    }

    // HSTS is only meaningful over HTTPS and the spec says a browser must ignore it elsewhere,
    // so it is emitted on the strength of the (possibly forwarded) scheme. TestServer honours
    // an https base address, which is what makes the conditional observable here.
    [Fact]
    public async Task OverHttps_HstsIsSet()
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri("https://localhost/");

        var response = await client.GetAsync("/health");

        Assert.Equal(SecurityHeaders.HstsValue, Single(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task OverPlainHttp_HstsIsNotSet()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal("", Single(response, "Strict-Transport-Security"));
    }

    private static async Task<ImageUpload> UploadOnePixelPngAsync(HttpClient client)
    {
        // The smallest valid PNG — enough to pass the magic-byte check on the upload path.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "pixel.png");

        var response = await client.PostAsync("/images", content);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ImageUpload>(TestJson.Options))!;
    }

    private record ImageUpload(string Url);
}
