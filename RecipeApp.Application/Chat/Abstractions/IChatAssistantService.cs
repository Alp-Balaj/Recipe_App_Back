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
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default);
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
// subset of the candidate ids passed in (empty is valid — nothing fit).
public record ChatAssistantResult(string Reply, IReadOnlyList<Guid> SuggestedRecipeIds);
