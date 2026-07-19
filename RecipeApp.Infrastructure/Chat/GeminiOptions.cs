namespace RecipeApp.Infrastructure.Chat;

// Configuration for the Gemini generateContent REST call (v2 provider pivot). Mirrors the
// JwtSettings `const string SectionName` precedent. ApiKey is resolved lazily in
// ChatServiceCollectionExtensions (never at startup) so an absent key can't break the host or
// CI — the key is server-side only and must never be committed or sent to the browser.
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    // Set via user-secrets ("Gemini:ApiKey") or the Gemini__ApiKey env var. Empty by default.
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "gemini-3.5-flash";

    // Must end in '/': the caller resolves "models/{Model}:generateContent" against it.
    // https://generativelanguage.googleapis.com/v1beta/models
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    // gemini-3.x is a THINKING model: it spends ~400-700 "thinking" tokens per
    // call BEFORE the visible reply, and thinking counts against this cap. At the
    // old 1024 a thinking spike left no room for the JSON reply → finishReason
    // MAX_TOKENS with no text part → GeminiMessageCaller throws → 502
    // AssistantUnavailable (~25-50% of calls, measured). 4096 leaves ample room
    // for thinking + the small structured reply; the reply always lands.
    public int MaxOutputTokens { get; set; } = 4096;
}
