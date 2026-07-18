using RecipeApp.Application.Images;
using RecipeApp.Application.Images.Abstractions;
using RecipeApp.Application.Images.Dtos;

namespace RecipeApp.API.Endpoints;

// Image upload (social-feed cp04, decision I1: local disk behind the IImageStorage seam).
// POST /images accepts one multipart "file" part, validates content type (jpeg/png/webp),
// size (5 MB cap), and magic bytes BEFORE storing, then returns 201 { url }. Serving is
// the static-file middleware on /images (Program.cs) — public-read, unguessable GUID
// names. Auth comes from the global RequireAuthenticatedUser fallback policy; the lane
// has its own "images" rate budget (disk writes are heavier than the social DB taps).
//
// The handler binds HttpRequest and reads the form itself (instead of an IFormFile
// parameter) for two reasons: the declared Content-Length can be rejected before the
// multipart body is ever parsed, and no inferred form parameter means no antiforgery
// metadata requirement on this JWT-bearer (cookie-less) API.
public static class ImageEndpoints
{
    // Multipart framing + headers on top of a max-size file. Requests declaring more than
    // this are rejected from the Content-Length header alone.
    private const long MaxDeclaredRequestBytes = ImageUploadRules.MaxSizeBytes + 64 * 1024;

    public static void MapImageEndpoints(this WebApplication app)
    {
        app.MapPost("/images", async (HttpRequest request, IImageStorage storage, CancellationToken cancellationToken) =>
        {
            if (request.ContentLength is > MaxDeclaredRequestBytes)
            {
                return FileProblem($"The image exceeds the {ImageUploadRules.MaxSizeBytes / (1024 * 1024)} MB size limit.");
            }

            if (!request.HasFormContentType)
            {
                return FileProblem("The request must be multipart/form-data with a 'file' part.");
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null)
            {
                return FileProblem("No file was uploaded. Send the image as a multipart part named 'file'.");
            }

            // Sniff the leading bytes so a mislabeled file can't smuggle a non-image in
            // under an image/* content type (never trust the client's declaration alone).
            await using var content = file.OpenReadStream();
            var header = new byte[ImageUploadRules.HeaderSniffLength];
            var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

            if (!ImageUploadRules.TryValidate(file.ContentType, file.Length, header.AsSpan(0, headerLength), out var extension, out var error))
            {
                return FileProblem(error!);
            }

            content.Position = 0;
            var url = await storage.SaveAsync(content, extension, cancellationToken);
            return Results.Created(url, new ImageUploadResponse(url));
        })
        .RequireRateLimiting(RateLimitPolicies.Images);
    }

    // Same 400 shape as the ValidationFilter lanes: RFC-7807 ValidationProblem keyed on
    // the offending field ("file").
    private static IResult FileProblem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["file"] = [message],
        });
}
