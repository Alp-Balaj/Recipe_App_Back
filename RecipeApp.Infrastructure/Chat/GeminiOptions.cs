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

    public string Model { get; set; } = "gemini-2.0-flash";

    // Must end in '/': the caller resolves "models/{Model}:generateContent" against it.
    // https://generativelanguage.googleapis.com/v1beta/models
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    // Matches the old Anthropic MaxTokens (provider-agnostic tuning, chat-ai Decisions §5).
    public int MaxOutputTokens { get; set; } = 1024;
}
