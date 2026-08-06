using RecipeApp.Application.Chat.Dtos;

namespace RecipeApp.Application.Recipes.Dtos;

/// <summary>
/// POST /recipes/{id}/cook/ask — one cook-mode question (stream M).
///
/// ── DECISION D14 IS WHY <see cref="History"/> IS IN THE REQUEST ────────────────────────────
/// A cook-mode exchange is session-scoped: no <c>Conversation</c> row, no <c>ChatMessage</c>
/// row, no migration. The client holds the transcript and hands it back each turn, which is the
/// whole of the persistence design.
///
/// It is not a shortcut, it is a containment measure. <c>ChatService.BuildHistoryAsync</c> feeds
/// a conversation's trailing 20 messages into the recommender's grounding, so a cook-mode
/// exchange persisted as an ordinary conversation would put "can I use margarine instead of
/// butter" into the prompt that decides what to suggest for dinner next Tuesday. Storing it
/// somewhere else instead would have cost a table and a migration to hold text nobody reads
/// twice — a transcript of a conversation with a frying pan.
///
/// The cost is that history is caller-supplied and therefore untrusted, which the server
/// handles the only way it can: the turn is metered per call whatever the history says, the
/// list is capped server-side so a long one cannot inflate the prompt without bound, and the
/// grounding block is built from the DATABASE row, never from anything in this body. A client
/// can fabricate its own past; it cannot fabricate the recipe.
/// </summary>
/// <param name="Question">What the cook asked.</param>
/// <param name="History">The exchange so far, oldest first. Empty on the first turn.</param>
/// <param name="Servings">
/// The serving count the cook is actually cooking for, so the quantities in the prompt match
/// the quantities on their screen. Scaled server-side by <see cref="ServingScale"/> — the same
/// arithmetic the SPA runs, never the model. Null means "as the recipe is written".
/// </param>
public record CookQuestionRequest(
    string Question,
    IReadOnlyList<CookHistoryItem>? History,
    int? Servings);

/// <summary>
/// One prior turn of the session-scoped exchange. Role is "user" or "assistant" — the same
/// two-value constraint the persisted <c>ChatMessage</c> carries, kept identical so the wire
/// shape does not fork just because this end of it is not stored.
/// </summary>
public record CookHistoryItem(string Role, string Content);

/// <summary>
/// The answer, plus what the caller has left to spend.
///
/// <paramref name="Refused"/> is the load-bearing field: it is the model's classification of
/// its own answer as off-recipe, promoted to the wire so refusal is a state the client renders
/// and a test asserts, rather than a sentence somebody reads. Budget rides along exactly as it
/// does on the generator's 201 — a surface that spends the daily allowance is a surface that
/// has to be able to show what is left of it.
/// </summary>
public record CookAnswerResponse(
    string Answer,
    bool Refused,
    AiBudgetResponse Budget);

/// <summary>
/// Stream M's outcome type. A third sibling beside <c>ChatOutcome</c> and
/// <c>RecipeGenerationOutcome</c> for the reason the second one gives: each endpoint's switch
/// stays exhaustive over its OWN cases, and widening a shared enum would silently add unhandled
/// ones to endpoints that cannot produce them.
/// </summary>
public enum CookAssistantOutcome
{
    Success,
    // The recipe does not exist, is soft-deleted, or is not visible to the caller. NotFound,
    // never Forbidden — the app's standing rule, and unchanged by cook mode.
    NotFound,
    // The provider call failed or returned unparseable output. Nothing was billed.
    AssistantUnavailable,
    // The caller's per-user daily AI budget is spent. Refused BEFORE the call.
    QuotaExceeded,
}

public record CookAssistantResult<T>(CookAssistantOutcome Outcome, T? Value)
{
    public static CookAssistantResult<T> Success(T value) => new(CookAssistantOutcome.Success, value);
    public static CookAssistantResult<T> NotFound() => new(CookAssistantOutcome.NotFound, default);
    public static CookAssistantResult<T> AssistantUnavailable() => new(CookAssistantOutcome.AssistantUnavailable, default);
    public static CookAssistantResult<T> QuotaExceeded() => new(CookAssistantOutcome.QuotaExceeded, default);
}
