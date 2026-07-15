namespace RecipeApp.Application.Chat;

public enum ChatOutcome { Success, NotFound, AssistantUnavailable }

// Service-layer outcome record for the chat endpoints (chat-ai plan, checkpoint 03), mirroring
// RecipeResult<T>. There is no Forbidden case: a conversation owned by another user (or
// soft-deleted) is always NotFound so 404s never leak that it exists. AssistantUnavailable
// signals an LLM failure — the endpoint maps it to a 502-style problem response, and no
// half-turn is persisted.
public record ChatResult<T>(ChatOutcome Outcome, T? Value)
{
    public static ChatResult<T> Success(T v) => new(ChatOutcome.Success, v);
    public static ChatResult<T> NotFound() => new(ChatOutcome.NotFound, default);
    public static ChatResult<T> AssistantUnavailable() => new(ChatOutcome.AssistantUnavailable, default);
}
