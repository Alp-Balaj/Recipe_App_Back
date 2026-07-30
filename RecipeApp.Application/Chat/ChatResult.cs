namespace RecipeApp.Application.Chat;

public enum ChatOutcome { Success, NotFound, AssistantUnavailable, QuotaExceeded }

// Service-layer outcome record for the chat endpoints (chat-ai plan, checkpoint 03), mirroring
// RecipeResult<T>. There is no Forbidden case: a conversation owned by another user (or
// soft-deleted) is always NotFound so 404s never leak that it exists. AssistantUnavailable
// signals an LLM failure — the endpoint maps it to a 502-style problem response, and no
// half-turn is persisted. QuotaExceeded (ai-quotas) means the caller's daily AI budget is
// spent — refused BEFORE the provider call, so nothing was persisted and nothing was billed;
// the endpoint maps it to 429.
public record ChatResult<T>(ChatOutcome Outcome, T? Value)
{
    public static ChatResult<T> Success(T v) => new(ChatOutcome.Success, v);
    public static ChatResult<T> NotFound() => new(ChatOutcome.NotFound, default);
    public static ChatResult<T> AssistantUnavailable() => new(ChatOutcome.AssistantUnavailable, default);
    public static ChatResult<T> QuotaExceeded() => new(ChatOutcome.QuotaExceeded, default);
}
