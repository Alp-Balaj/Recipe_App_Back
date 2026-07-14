using RecipeApp.Application.Chat.Abstractions;

namespace RecipeApp.Infrastructure.Chat;

// Thin, fakeable seam over the single Anthropic SDK call. ClaudeChatAssistantService owns
// everything testable (prompt assembly, structured-output parsing, id filtering); this
// interface isolates the one part that talks to the network so unit tests can inject canned
// or malformed responses without the real client. Public only so the test project can supply
// a fake — it is not part of any consumer-facing contract.
public interface IAnthropicMessageCaller
{
    // Sends the request and returns the model's raw structured-output JSON text, shaped
    // { "reply": string, "suggestedRecipeIds": string[] }. Parsing/validation is the caller's
    // job — this returns the text of the first content block verbatim.
    Task<string> CreateJsonMessageAsync(
        string systemPrompt,
        IReadOnlyList<ChatHistoryItem> history,
        string userMessage,
        CancellationToken cancellationToken = default);
}
