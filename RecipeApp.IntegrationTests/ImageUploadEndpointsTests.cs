using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using RecipeApp.Application.Images;
using RecipeApp.Application.Images.Dtos;

namespace RecipeApp.IntegrationTests;

// social-feed cp4: POST /images (multipart upload behind the IImageStorage seam) and the
// public static-file serving on /images. The factory points the storage root at a temp
// directory it deletes on dispose, so these tests never write into the repo tree.
public class ImageUploadEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // A real, complete 1x1 transparent PNG — the GET round-trip serves these exact bytes back.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];
    private static readonly byte[] WebpBytes = [0x52, 0x49, 0x46, 0x46, 0x0C, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20];

    [Fact]
    public async Task UploadPng_Returns201_AndTheUrlServesTheBytesBack_WithoutAuth()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/images", CreateUpload(OnePixelPng, "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ImageUploadResponse>();
        Assert.NotNull(body);
        Assert.Matches(new Regex("^/images/[0-9a-f]{32}\\.png$"), body.Url);
        Assert.Equal(body.Url, response.Headers.Location?.ToString());

        // Serving is deliberately public-read (decision I1): fetch with a client that
        // carries NO bearer token and confirm the exact bytes round-trip.
        var anonymousClient = factory.CreateClient();
        var get = await anonymousClient.GetAsync(body.Url);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(OnePixelPng, await get.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UploadJpeg_Returns201_WithJpgUrl()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/images", CreateUpload(JpegBytes, "image/jpeg"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ImageUploadResponse>();
        Assert.Matches(new Regex("^/images/[0-9a-f]{32}\\.jpg$"), body!.Url);
    }

    [Fact]
    public async Task UploadWebp_Returns201_WithWebpUrl()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/images", CreateUpload(WebpBytes, "image/webp"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ImageUploadResponse>();
        Assert.Matches(new Regex("^/images/[0-9a-f]{32}\\.webp$"), body!.Url);
    }

    [Fact]
    public async Task TwoUploads_GetDistinctServerGeneratedNames_IgnoringTheClientFilename()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var first = await client.PostAsync("/images", CreateUpload(OnePixelPng, "image/png", "../../evil name.png"));
        var second = await client.PostAsync("/images", CreateUpload(OnePixelPng, "image/png", "../../evil name.png"));

        var firstUrl = (await first.Content.ReadFromJsonAsync<ImageUploadResponse>())!.Url;
        var secondUrl = (await second.Content.ReadFromJsonAsync<ImageUploadResponse>())!.Url;

        // Same client filename in, two different GUID names out — nothing of the client's
        // (path segments, spaces, original name) survives into the URL.
        Assert.NotEqual(firstUrl, secondUrl);
        Assert.Matches(new Regex("^/images/[0-9a-f]{32}\\.png$"), firstUrl);
        Assert.Matches(new Regex("^/images/[0-9a-f]{32}\\.png$"), secondUrl);
    }

    [Fact]
    public async Task UploadDisallowedContentType_Returns400ValidationProblem()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        byte[] gifBytes = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00];
        var response = await client.PostAsync("/images", CreateUpload(gifBytes, "image/gif"));

        await AssertFileValidationProblem(response, "JPEG, PNG, and WebP");
    }

    [Fact]
    public async Task UploadMismatchedMagicBytes_Returns400ValidationProblem()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // Declared as PNG, but the bytes are JPEG — the sniff must reject the mislabel.
        var response = await client.PostAsync("/images", CreateUpload(JpegBytes, "image/png"));

        await AssertFileValidationProblem(response, "does not match");
    }

    [Fact]
    public async Task UploadOverTheSizeCap_Returns400ValidationProblem()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // One byte over the cap, with a valid PNG header so ONLY the size rule can fail.
        var oversize = new byte[ImageUploadRules.MaxSizeBytes + 1];
        OnePixelPng.CopyTo(oversize, 0);
        var response = await client.PostAsync("/images", CreateUpload(oversize, "image/png"));

        await AssertFileValidationProblem(response, "size limit");
    }

    [Fact]
    public async Task UploadEmptyFile_Returns400ValidationProblem()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync("/images", CreateUpload([], "image/png"));

        await AssertFileValidationProblem(response, "empty");
    }

    [Fact]
    public async Task UploadMultipartWithoutAFilePart_Returns400ValidationProblem()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var content = new MultipartFormDataContent { { new StringContent("not a file"), "note" } };
        var response = await client.PostAsync("/images", content);

        await AssertFileValidationProblem(response, "No file");
    }

    [Fact]
    public async Task UploadNonMultipartBody_Returns400ValidationProblem()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsJsonAsync("/images", new { file = "nope" });

        await AssertFileValidationProblem(response, "multipart/form-data");
    }

    [Fact]
    public async Task UploadWithoutToken_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync("/images", CreateUpload(OnePixelPng, "image/png"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetUnknownImage_DoesNotServe()
    {
        // A missing file falls through the static middleware into normal routing, where no
        // endpoint matches. With a token that's a 404; without one it's the app-wide 401
        // that EVERY unmatched route gets from the RequireAuthenticatedUser fallback policy
        // (pre-existing behavior, e.g. GET /definitely-not-a-route → 401) — either way, no
        // bytes and no existence signal beyond what any bad URL gives.
        var authenticatedClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(authenticatedClient);
        var authenticated = await authenticatedClient.GetAsync($"/images/{Guid.NewGuid():N}.png");
        Assert.Equal(HttpStatusCode.NotFound, authenticated.StatusCode);

        var anonymousClient = factory.CreateClient();
        var anonymous = await anonymousClient.GetAsync($"/images/{Guid.NewGuid():N}.png");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static MultipartFormDataContent CreateUpload(byte[] bytes, string contentType, string fileName = "photo.png")
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { fileContent, "file", fileName } };
    }

    private static async Task AssertFileValidationProblem(HttpResponseMessage response, string expectedFragment)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<TestValidationProblem>();
        Assert.NotNull(problem);
        var messages = Assert.Contains("file", (IDictionary<string, string[]>)problem.Errors);
        Assert.Contains(messages, m => m.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record TestValidationProblem(Dictionary<string, string[]> Errors);
}
