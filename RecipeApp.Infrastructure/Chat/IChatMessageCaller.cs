using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// Thin, fakeable seam over the single provider network call. ChatAssistantService owns
// everything testable (prompt assembly, structured-output parsing, id filtering); this
// interface isolates the one part that talks to the network so unit tests can inject canned
// or malformed responses without the real client. Public only so the test project can supply
// a fake — it is not part of any consumer-facing contract. Provider-neutral by design: the
// real implementation (Gemini today) is swappable without touching anything above this seam.
public interface IChatMessageCaller
{
    // Sends the request and returns the model's raw structured-output JSON text plus the
    // provider-reported token usage (ai-quotas; Usage is null when the provider omitted it).
    // With no responseSchema the output is shaped { "reply": string, "suggestedRecipeIds":
    // string[] } (the chat lane's contract); a caller with a different output shape (meal-plan
    // proposals) passes its own schema object in the implementation's dialect.
    // Parsing/validation of the JSON is the caller's job — the text of the first content part
    // is returned verbatim.
    Task<ChatMessageCall> CreateJsonMessageAsync(
        string systemPrompt,
        IReadOnlyList<ChatHistoryItem> history,
        string userMessage,
        object? responseSchema = null,
        CancellationToken cancellationToken = default);
}

// What one provider call produced: the structured-output text and what it cost.
public record ChatMessageCall(string Json, ChatTokenUsage? Usage);
