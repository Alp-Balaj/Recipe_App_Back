namespace RecipeApp.Application.Recipes;

// Stream E's outcome type. A separate type rather than new members on RecipeOutcome: the
// generator is the only recipe path that can be refused for budget or fail at a provider,
// and widening the shared enum would silently add unhandled cases to every existing
// endpoint switch. Mirrors ChatOutcome, which draws the same two distinctions.
public enum RecipeGenerationOutcome
{
    Success,
    // The source conversation does not exist, is deleted, or belongs to someone else.
    // NotFound, never Forbidden — same non-leaking rule the rest of the app follows.
    NotFound,
    // The provider call failed or returned something that could not be salvaged into a
    // valid recipe. Nothing was persisted.
    AssistantUnavailable,
    // The caller's per-user daily AI budget is spent (stream B). Refused BEFORE the call,
    // so an exhausted budget costs no money.
    QuotaExceeded,
}

public record RecipeGenerationResult<T>(RecipeGenerationOutcome Outcome, T? Value)
{
    public static RecipeGenerationResult<T> Success(T value) => new(RecipeGenerationOutcome.Success, value);
    public static RecipeGenerationResult<T> NotFound() => new(RecipeGenerationOutcome.NotFound, default);
    public static RecipeGenerationResult<T> AssistantUnavailable() => new(RecipeGenerationOutcome.AssistantUnavailable, default);
    public static RecipeGenerationResult<T> QuotaExceeded() => new(RecipeGenerationOutcome.QuotaExceeded, default);
}
