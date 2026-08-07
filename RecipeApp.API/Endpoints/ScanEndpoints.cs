using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.Application.Images;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Scanning;
using RecipeApp.Application.Scanning.Abstractions;

namespace RecipeApp.API.Endpoints;

// Stream N (D13): the food scanner's two routes. Under their own /scan group rather than
// /recipes or /shopping-list, because neither answer IS a recipe or a list row — a pantry
// scan answers with matches into the existing catalogue and a receipt scan answers with a
// DRAFT the user confirms through the existing POST /shopping-list. Both are 200s, not
// 201s: nothing was created, which is D19 made visible in the status code.
//
// The multipart handling is /recipes/import/photo's, copied deliberately and in the same
// order: ContentLength refused before the body is parsed, HasFormContentType, a part named
// "file", and ImageUploadRules.TryValidate — the same magic-byte sniff an upload gets —
// BEFORE a byte reaches the provider. Without that ordering a mislabelled file becomes a
// paid vision call that fails at Gemini's allow-list instead of a free 400 here.
public static class ScanEndpoints
{
    // Multipart framing and headers on top of a max-size photo — the same ceiling the
    // import photo route allows, so a scan and an import refuse the same file.
    private const long MaxDeclaredPhotoBytes = ImageUploadRules.MaxSizeBytes + 64 * 1024;

    public static void MapScanEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/scan").RequireRateLimiting(RateLimitPolicies.Scan);

        group.MapPost("/pantry", async (HttpRequest httpRequest, IFoodScanService scans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);

            var (image, error) = await ReadPhotoAsync(httpRequest, cancellationToken);
            if (image is null)
            {
                return error!;
            }

            var result = await scans.ScanPantryAsync(image, userId, cancellationToken);
            return result.Outcome switch
            {
                FoodScanOutcome.Success => Results.Ok(result.Value),
                FoodScanOutcome.QuotaExceeded => QuotaExhausted(),
                _ => ScannerUnavailable(),
            };
        });

        group.MapPost("/receipt", async (HttpRequest httpRequest, IFoodScanService scans, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);

            var (image, error) = await ReadPhotoAsync(httpRequest, cancellationToken);
            if (image is null)
            {
                return error!;
            }

            var result = await scans.ScanReceiptAsync(image, userId, cancellationToken);
            return result.Outcome switch
            {
                FoodScanOutcome.Success => Results.Ok(result.Value),
                FoodScanOutcome.QuotaExceeded => QuotaExhausted(),
                _ => ScannerUnavailable(),
            };
        });
    }

    /// <summary>
    /// The shared multipart-photo intake, one guard at a time in the import route's order.
    /// Returns the validated image, or the 400 that explains itself.
    /// </summary>
    private static async Task<(RecipeImageContent? Image, IResult? Error)> ReadPhotoAsync(
        HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        if (httpRequest.ContentLength is > MaxDeclaredPhotoBytes)
        {
            return (null, PhotoProblem(
                $"The photo exceeds the {ImageUploadRules.MaxSizeBytes / (1024 * 1024)} MB size limit."));
        }

        if (!httpRequest.HasFormContentType)
        {
            return (null, PhotoProblem("The request must be multipart/form-data with a 'file' part."));
        }

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null)
        {
            return (null, PhotoProblem("No photo was uploaded. Send it as a multipart part named 'file'."));
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();

        var header = content.AsSpan(0, Math.Min(ImageUploadRules.HeaderSniffLength, content.Length));
        if (!ImageUploadRules.TryValidate(file.ContentType, content.Length, header, out _, out var error))
        {
            return (null, PhotoProblem(error!));
        }

        return (new RecipeImageContent(content, file.ContentType), null);
    }

    private static Guid GetUserId(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // The same problem shapes as every other AI lane, so the SPA shares one handler.
    private static IResult QuotaExhausted() =>
        Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Daily AI budget exhausted.",
            detail: "You have used today's AI allowance. The budget resets at 00:00 UTC.");

    private static IResult ScannerUnavailable() =>
        Results.Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "The scanner is temporarily unavailable.",
            detail: "The photo could not be read just now. Nothing was saved — please try again.");

    private static IResult PhotoProblem(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [message] });
}
