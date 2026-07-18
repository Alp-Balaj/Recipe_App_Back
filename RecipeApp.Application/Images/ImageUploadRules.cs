namespace RecipeApp.Application.Images;

// Validation rules for image uploads (social-feed cp04). Pure and framework-free so the
// rules are unit-testable without HTTP: the endpoint hands in the declared content type,
// the size, and the first few bytes; this decides accept/reject and picks the extension
// the stored file gets. The client's filename is never consulted — the server-side
// extension comes from the (magic-byte-verified) content type alone.
public static class ImageUploadRules
{
    /// <summary>Maximum accepted image size: 5 MB (kickoff's suggested cap).</summary>
    public const long MaxSizeBytes = 5 * 1024 * 1024;

    /// <summary>
    /// How many leading bytes the caller should read for the magic-byte sniff. 12 covers
    /// the longest check (WebP's RIFF????WEBP container header).
    /// </summary>
    public const int HeaderSniffLength = 12;

    private const string MismatchError =
        "The file content does not match its declared content type.";

    // Magic numbers: JPEG FF D8 FF, PNG 89 'PNG' 0D 0A 1A 0A, WebP "RIFF"<size>"WEBP".
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Validates an upload before anything is stored. On success, <paramref name="extension"/>
    /// is the server-chosen extension (".jpg"/".png"/".webp"); on failure,
    /// <paramref name="error"/> is a client-safe message for a 400 ValidationProblem.
    /// </summary>
    public static bool TryValidate(
        string? contentType,
        long sizeBytes,
        ReadOnlySpan<byte> header,
        out string extension,
        out string? error)
    {
        extension = string.Empty;
        error = null;

        if (sizeBytes <= 0)
        {
            error = "The uploaded file is empty.";
            return false;
        }

        if (sizeBytes > MaxSizeBytes)
        {
            error = $"The image exceeds the {MaxSizeBytes / (1024 * 1024)} MB size limit.";
            return false;
        }

        switch (contentType?.Trim().ToLowerInvariant())
        {
            case "image/jpeg":
                if (!header.StartsWith(JpegMagic))
                {
                    error = MismatchError;
                    return false;
                }

                extension = ".jpg";
                return true;

            case "image/png":
                if (!header.StartsWith(PngMagic))
                {
                    error = MismatchError;
                    return false;
                }

                extension = ".png";
                return true;

            case "image/webp":
                if (header.Length < HeaderSniffLength
                    || !header[..4].SequenceEqual("RIFF"u8)
                    || !header[8..12].SequenceEqual("WEBP"u8))
                {
                    error = MismatchError;
                    return false;
                }

                extension = ".webp";
                return true;

            default:
                error = "Only JPEG, PNG, and WebP images are accepted "
                    + "(Content-Type image/jpeg, image/png, or image/webp).";
                return false;
        }
    }
}
