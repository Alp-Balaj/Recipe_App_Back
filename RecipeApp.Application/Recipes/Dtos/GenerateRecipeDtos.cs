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
//
// DietaryChecks is stream H's addition, and this lane is the sharper of the two it covers.
// propose-week is GROUNDED — the model may only point at recipes that already exist, so a
// conflict there is a bad pick from a real corpus. The generator is FREE: it invents the
// ingredient list, and its trust boundary is range testing, not membership. A model that
// cheerfully writes butter into a recipe for someone who told it "dairy-free" produces a row
// that is already saved by the time anyone looks. So the check runs on the way out, against
// the CALLER's restrictions, and its finding travels with the 201.
//
// It reports conflicts FOUND and how many lines could not be read. It does not certify the
// recipe, it does not block the write, and no client may render it as either — see
// DietaryRules. Appended last so existing positional constructions keep compiling.
public record GenerateRecipeResponse(
    RecipeResponse Recipe,
    AiBudgetResponse Budget,
    IReadOnlyList<DietaryCheckResponse> DietaryChecks);
