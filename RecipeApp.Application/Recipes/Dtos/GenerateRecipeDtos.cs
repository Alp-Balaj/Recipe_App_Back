using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Recipes.Dtos;

// Wire contract for POST /recipes/generate (stream E).
//
// Prompt is what the user wants ("something with the leftover roast chicken, 20 minutes").
// ConversationId is optional provenance AND context: when present the recipe records where
// it came from (decision D1) and the assistant sees that thread's recent messages, so
// "make that one but vegetarian" means something. It must be a conversation the caller
// owns; anyone else's is a 404.
//
// Visibility is optional and defaults to the caller's DefaultRecipeVisibility preference —
// the same default the create form prefills — so a generated recipe is as public as
// anything else that user writes, no more and no less.
public record GenerateRecipeRequest(
    string Prompt,
    Guid? ConversationId,
    RecipeVisibility? Visibility);

// 201 body. The recipe is a real, persisted, user-owned row (D1), so it is returned in the
// SAME shape every other recipe endpoint returns — the SPA can route straight to
// /recipes/{id} with no special case. Budget rides along exactly as it does on a chat turn,
// so the surface can say how much of today's allowance is left without a second request.
public record GenerateRecipeResponse(
    RecipeResponse Recipe,
    AiBudgetResponse Budget);
