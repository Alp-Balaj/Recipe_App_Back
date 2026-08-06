using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Chat.Abstractions;

// AI seam for chat-driven recipe suggestions (chat-ai plan, checkpoint 02). The service
// grounds a Claude request on the recipes visible to the caller (Decisions §3, candidate
// injection — no RAG) and returns a reply plus the ids it suggests, guaranteed to be a
// subset of the candidate ids (hallucinated/unparseable ids are dropped server-side).
//
// Candidates are an INPUT: assembling them from the DB (visibility rule 1, most-recent N,
// compact projection) is cp03's orchestration concern. These are service-layer types, NOT
// wire DTOs — cp03 maps its own request/response contract onto them.
public interface IChatAssistantService
{
    Task<ChatAssistantResult> GetReplyAsync(
        string userMessage,
        IReadOnlyList<ChatHistoryItem> recentHistory,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        AiPreferenceContext preferences,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the caller's own account says about their food, as prose the prompt can use
/// (stream K). Both lists are already rendered by <c>Vocabulary.Describe</c> — the assistants
/// take words, never enum members, for the reason that class documents.
/// </summary>
///
/// <remarks>
/// A record rather than two adjacent <c>IReadOnlyList&lt;string&gt;</c> parameters, and that
/// is the whole point of it. The two lists are semantically OPPOSITE — restrictions are
/// absolute and exclude, preferences are soft and merely lean — so transposing them at a call
/// site would turn "never serve me shellfish" into a gentle suggestion and "I like Thai food"
/// into a rule, with no compiler error and no test that obviously fails. Named members make
/// that transposition impossible to write. (Stream C learned the same lesson positionally,
/// with IChatMessageCaller's token arguments.)
///
/// IMealPlanAssistantService deliberately does NOT take this: propose-week consumes
/// preferences by weighting its candidate set rather than by prompt, so it has exactly one
/// such list and nothing to confuse it with.
/// </remarks>
public record AiPreferenceContext(
    IReadOnlyList<string> DietaryRestrictions,
    IReadOnlyList<string> CuisinePreferences)
{
    public static readonly AiPreferenceContext None = new([], []);
}

// Compact projection of a recipe the model may recommend. Mirrors the fields the recipe
// list projection already exposes (id, title, description, cuisine, difficulty, total time,
// calories, tags) — serialized into the prompt, no embeddings.
public record ChatCandidateRecipe(
    Guid Id,
    string Title,
    string Description,
    string? Cuisine,
    DifficultyLevel Difficulty,
    int TotalTimeMinutes,
    int? Calories,
    IReadOnlyList<string> Tags);

// One prior chat message included for continuity. Role is "user" or "assistant" (the same
// service-layer constraint the ChatMessage entity documents).
public record ChatHistoryItem(string Role, string Content);

// The assistant's reply plus the recipe ids it suggested. SuggestedRecipeIds is always a
// subset of the candidate ids passed in (empty is valid — nothing fit). Usage is the
// provider-reported token cost of the call (ai-quotas): null when the provider omitted usage
// metadata, in which case the call is still accounted (at zero tokens) by IAiUsageService.
public record ChatAssistantResult(
    string Reply,
    IReadOnlyList<Guid> SuggestedRecipeIds,
    ChatTokenUsage? Usage = null);

// Provider-reported token counts for one AI call (ai-quotas). Provider-neutral: prompt and
// completion mean what every provider means by them; TotalTokens is the provider's own total
// and can exceed the sum (Gemini 3.x counts thinking tokens there), so budgets sum TotalTokens.
public record ChatTokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);
