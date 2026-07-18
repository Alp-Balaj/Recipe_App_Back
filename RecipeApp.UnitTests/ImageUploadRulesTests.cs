using RecipeApp.Application.Images;

namespace RecipeApp.UnitTests;

// social-feed cp4: the pure upload-validation rules behind POST /images — content-type
// allowlist, 5 MB cap, and the magic-byte sniff that stops mislabeled files. The endpoint
// integration tests re-prove the wire behavior; these pin the decision table itself.
public class ImageUploadRulesTests
{
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] WebpHeader = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50]; // "RIFF" size "WEBP"

    [Fact]
    public void Jpeg_WithMatchingMagicBytes_IsAccepted_WithJpgExtension()
    {
        var ok = ImageUploadRules.TryValidate("image/jpeg", 1024, JpegHeader, out var extension, out var error);

        Assert.True(ok);
        Assert.Equal(".jpg", extension);
        Assert.Null(error);
    }

    [Fact]
    public void Png_WithMatchingMagicBytes_IsAccepted_WithPngExtension()
    {
        var ok = ImageUploadRules.TryValidate("image/png", 1024, PngHeader, out var extension, out var error);

        Assert.True(ok);
        Assert.Equal(".png", extension);
        Assert.Null(error);
    }

    [Fact]
    public void Webp_WithMatchingMagicBytes_IsAccepted_WithWebpExtension()
    {
        var ok = ImageUploadRules.TryValidate("image/webp", 1024, WebpHeader, out var extension, out var error);

        Assert.True(ok);
        Assert.Equal(".webp", extension);
        Assert.Null(error);
    }

    [Fact]
    public void ContentType_IsMatchedCaseInsensitively()
    {
        var ok = ImageUploadRules.TryValidate("IMAGE/PNG", 1024, PngHeader, out var extension, out _);

        Assert.True(ok);
        Assert.Equal(".png", extension);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void DisallowedOrMissingContentType_IsRejected(string? contentType)
    {
        var ok = ImageUploadRules.TryValidate(contentType, 1024, PngHeader, out _, out var error);

        Assert.False(ok);
        Assert.Contains("JPEG, PNG, and WebP", error);
    }

    [Fact]
    public void SizeExactlyAtTheCap_IsAccepted()
    {
        var ok = ImageUploadRules.TryValidate("image/png", ImageUploadRules.MaxSizeBytes, PngHeader, out _, out _);

        Assert.True(ok);
    }

    [Fact]
    public void SizeOverTheCap_IsRejected()
    {
        var ok = ImageUploadRules.TryValidate("image/png", ImageUploadRules.MaxSizeBytes + 1, PngHeader, out _, out var error);

        Assert.False(ok);
        Assert.Contains("5 MB", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EmptyOrNegativeSize_IsRejected(long sizeBytes)
    {
        var ok = ImageUploadRules.TryValidate("image/png", sizeBytes, PngHeader, out _, out var error);

        Assert.False(ok);
        Assert.Contains("empty", error);
    }

    [Fact]
    public void DeclaredPng_WithJpegBytes_IsRejected()
    {
        var ok = ImageUploadRules.TryValidate("image/png", 1024, JpegHeader, out _, out var error);

        Assert.False(ok);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void DeclaredJpeg_WithPngBytes_IsRejected()
    {
        var ok = ImageUploadRules.TryValidate("image/jpeg", 1024, PngHeader, out _, out var error);

        Assert.False(ok);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void DeclaredWebp_WithRiffButNoWebpTag_IsRejected()
    {
        // A RIFF container that is not WebP (e.g. a .wav file): "RIFF" size "WAVE".
        byte[] wavHeader = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45];

        var ok = ImageUploadRules.TryValidate("image/webp", 1024, wavHeader, out _, out var error);

        Assert.False(ok);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void HeaderShorterThanTheMagicNumber_IsRejected()
    {
        // A 2-byte file can't satisfy any of the sniffs even with a valid content type.
        byte[] tiny = [0xFF, 0xD8];

        var ok = ImageUploadRules.TryValidate("image/jpeg", 2, tiny, out _, out var error);

        Assert.False(ok);
        Assert.Contains("does not match", error);
    }
}
