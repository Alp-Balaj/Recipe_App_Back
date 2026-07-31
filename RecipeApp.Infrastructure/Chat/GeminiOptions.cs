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

    // An ALIAS, not a version pin (changed 2026-07-31). The previous pin,
    // gemini-3.5-flash, returned 503 to even a five-token request for over 40
    // minutes that day, which took out all three AI lanes at once — chat,
    // propose-week and recipe generation — with no code path able to route
    // around it. gemini-flash-latest resolves to whatever flash model Google
    // currently serves, so a single overloaded version no longer grounds the
    // whole feature set.
    //
    // The trade, stated so nobody rediscovers it: an alias is NOT reproducible.
    // The model behind it can change without a deploy, which can move output
    // quality, token cost and latency underneath us. Everything downstream is
    // built to survive that — structured output constrains the shape, and every
    // lane re-validates defensively (ids against a candidate set for chat and
    // propose-week, values against range bounds for generation) — but if a
    // future model change needs to be ruled out while debugging, pin a concrete
    // version here or via Gemini__Model and reproduce.
    public string Model { get; set; } = "gemini-flash-latest";

    // Must end in '/': the caller resolves "models/{Model}:generateContent" against it.
    // https://generativelanguage.googleapis.com/v1beta/models
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    // Every flash model the alias above can resolve to is a THINKING model: it
    // spends "thinking" tokens BEFORE the visible reply, and thinking counts
    // against this cap. At the old 1024 a thinking spike left no room for the
    // JSON reply → finishReason MAX_TOKENS with no text part → GeminiMessageCaller
    // throws → 502 AssistantUnavailable (~25-50% of calls, measured). 4096 fixed
    // that for gemini-3.5-flash, which thought for ~400-700 tokens.
    //
    // Raised to 8192 on 2026-07-31, together with the move to the alias, because
    // the newer flash thinks MUCH harder: propose-week (the most demanding call —
    // assign 21 slots) came back TRUNCATED MID-STRING at ~500 bytes of JSON, so
    // thinking had eaten nearly the whole 4096. That failure is quiet and nasty:
    // the caller gets a 200 with a partial text part, and the truncation only
    // surfaces as a JsonException in the assistant's parser.
    //
    // This ceiling is not a spend — it is only reached when thinking actually
    // runs long — but it IS the thing that has to grow when the alias moves to a
    // model that reasons more. If a lane starts 502ing with "malformed JSON"
    // after a model change, look here first.
    public int MaxOutputTokens { get; set; } = 8192;
}
