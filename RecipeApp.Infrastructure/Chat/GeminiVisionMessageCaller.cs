using System.Net.Http.Json;
using System.Text.Json;

namespace RecipeApp.Infrastructure.Chat;

// The real IVisionMessageCaller, and the sibling GeminiMessageCaller does not become (D4).
//
// Everything AROUND the request is copied from GeminiMessageCaller on purpose — the key as a
// query parameter so it never lands in a path-based access log, the status-code-only error so
// no provider body or ?key= URI escapes to the caller, the shared response reader. The only
// genuinely new thing in this file is the request BODY, because Gemini's inlineData part had
// no precedent anywhere in this codebase: the existing seam sends parts: [{ text }] and
// nothing else.
public sealed class GeminiVisionMessageCaller : IVisionMessageCaller
{
    // Gemini rejects an image part whose mimeType it does not know, and the failure is a flat
    // 400 with no indication of which part was at fault. The three here are exactly the three
    // ImageUploadRules already accepts, so anything that can be uploaded through the front door
    // can also be read by this caller. HEIC is deliberately absent — iPhones produce it, Gemini
    // accepts it, and ImageUploadRules does not, so allowing it here would make the vision path
    // accept a file the rest of the app cannot store.
    private static readonly string[] SupportedMimeTypes = ["image/jpeg", "image/png", "image/webp"];

    private readonly HttpClient _http;
    private readonly GeminiOptions _options;

    public GeminiVisionMessageCaller(HttpClient http, GeminiOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<ChatMessageCall> CreateJsonMessageFromImagesAsync(
        string systemPrompt,
        IReadOnlyList<VisionImagePart> images,
        string userMessage,
        object? responseSchema = null,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
        {
            throw new InvalidOperationException("A vision call requires at least one image.");
        }

        // ONE user turn holding every image part and then the text. The ordering matters more
        // than it looks: Gemini attends to a prompt that FOLLOWS its images noticeably better
        // than one that precedes them, which is the documented guidance for single-turn
        // multimodal requests and is why the text part is appended last rather than first.
        var parts = new List<object>(images.Count + 1);
        foreach (var image in images)
        {
            var mimeType = Normalise(image.ContentType);
            if (!SupportedMimeTypes.Contains(mimeType))
            {
                throw new InvalidOperationException($"Unsupported image type '{image.ContentType}'.");
            }

            parts.Add(new
            {
                inlineData = new
                {
                    mimeType,
                    // inlineData is base64 in the JSON body — there is no multipart form here,
                    // which is why the size ceiling upstream matters: this inflates the bytes
                    // by a third before they go over the wire.
                    data = Convert.ToBase64String(image.Content),
                },
            });
        }

        parts.Add(new { text = userMessage });

        var requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema,
                maxOutputTokens = _options.MaxOutputTokens,
            },
        };

        var requestUri = $"{_options.BaseUrl}models/{_options.Model}:generateContent?key={_options.ApiKey}";

        using var response = await _http.PostAsJsonAsync(requestUri, requestBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Status code only — never the Gemini error body or the ?key= URI. Same rule and
            // same reason as GeminiMessageCaller.
            throw new InvalidOperationException(
                $"Gemini generateContent (vision) returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return new ChatMessageCall(
            GeminiResponseReader.ExtractText(doc.RootElement),
            GeminiResponseReader.ExtractUsage(doc.RootElement));
    }

    // Browsers and CDNs send "image/jpeg; charset=binary" and "IMAGE/JPEG" alike; Gemini's
    // allow-list is exact and case-sensitive.
    private static string Normalise(string? contentType) =>
        contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
}
