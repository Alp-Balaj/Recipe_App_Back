using System.Net.Http.Json;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// Real IChatMessageCaller: the single, unfaked network boundary, talking to Gemini's
// generateContent REST endpoint (v2 provider pivot — the repo's first raw-HttpClient
// integration). Maps the seam's provider-neutral { reply, suggestedRecipeIds } contract onto
// Gemini's request/response shape. The model is pinned via GeminiOptions (gemini-3.5-flash) and
// structured output is constrained with responseSchema; nothing above this seam knows any of it.
public sealed class GeminiMessageCaller : IChatMessageCaller
{
    // Gemini's schema is a Google-flavored OpenAPI subset: type values are UPPERCASE enums and
    // `additionalProperties` is unsupported (simply omitted). Ids still arrive as strings; the
    // Guid.TryParse + candidate-membership filter in ChatAssistantService already guards
    // hallucinated/malformed ids, so the looser schema costs nothing. This is the DEFAULT
    // schema (the chat lane's); callers with a different output shape pass their own via the
    // responseSchema parameter, in this same dialect.
    private static readonly object DefaultResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            reply = new { type = "STRING" },
            suggestedRecipeIds = new { type = "ARRAY", items = new { type = "STRING" } },
        },
        required = new[] { "reply", "suggestedRecipeIds" },
    };

    private readonly HttpClient _http;
    private readonly GeminiOptions _options;

    public GeminiMessageCaller(HttpClient http, GeminiOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<string> CreateJsonMessageAsync(
        string systemPrompt,
        IReadOnlyList<ChatHistoryItem> history,
        string userMessage,
        object? responseSchema = null,
        CancellationToken cancellationToken = default)
    {
        var contents = new List<object>(history.Count + 1);
        foreach (var item in history)
        {
            // Gemini uses "model" (not "assistant") for the assistant turn; anything else is
            // "user". A wrong role value is a 400 — this mapping keeps parity with the entity's
            // "user"/"assistant" convention.
            var role = item.Role == "assistant" ? "model" : "user";
            contents.Add(new { role, parts = new[] { new { text = item.Content } } });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = userMessage } } });

        var requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents,
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = responseSchema ?? DefaultResponseSchema,
                maxOutputTokens = _options.MaxOutputTokens,
                // No temperature/topP — parity with the old Claude call, which sent neither.
            },
        };

        // Key travels as a query param, not in the URL path, so it never lands in path-based
        // request logs. BaseUrl ends in '/', so this resolves to
        // {BaseUrl}models/{Model}:generateContent.
        var requestUri = $"{_options.BaseUrl}models/{_options.Model}:generateContent?key={_options.ApiKey}";

        using var response = await _http.PostAsJsonAsync(requestUri, requestBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Any non-2xx (429/RESOURCE_EXHAUSTED quota, 403 bad/expired key, 5xx) throws here.
            // ChatService funnels every non-cancellation caller exception to AssistantUnavailable
            // → 502, so no dedicated exception type is needed. Never surface the Gemini error body
            // or the ?key= URI to the caller — status code only.
            throw new InvalidOperationException(
                $"Gemini generateContent returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ExtractText(doc.RootElement);
    }

    // candidates[0].content.parts[] → first part carrying a "text" string, returned verbatim
    // (the seam's contract: hand back the model's structured-output text; ChatAssistantService
    // parses it). Mirrors the old "no text block" guard — throw when absent, which surfaces as
    // AssistantUnavailable upstream.
    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0)
        {
            var first = candidates[0];
            if (first.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString()!;
                    }
                }
            }
        }

        throw new InvalidOperationException("Gemini response contained no text part.");
    }
}
