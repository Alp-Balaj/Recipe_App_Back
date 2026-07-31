using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Abstractions;

// The AI seam for stream E's generator, mirroring IChatAssistantService and
// IMealPlanAssistantService: the implementation owns the prompt, the structured-output
// schema, the parsing and the defensive re-validation; the network call itself sits one
// level lower behind IChatMessageCaller, so this is unit-testable without a provider.
//
// THE ONE DIFFERENCE FROM THE OTHER TWO SEAMS, and it is the interesting one: the chat
// recommender and the week proposer are GROUNDED — the model may only cite ids from a
// candidate set the server built, and anything else is dropped. This one is FREE: the model
// invents the content, and nothing it returns can be checked against a known set. So the
// trust boundary moves from "is this id real" to "is this value in range", which is what
// the implementation's normalisation does — every field is clamped, trimmed, bounded and
// re-checked against the same limits CreateRecipeRequestValidator enforces on the human
// write path. A generated recipe is never allowed to be a row a human could not have typed.
public interface IRecipeGenerationAssistant
{
    // Runs one generation. `history` is the source conversation's trailing messages (empty
    // when generating outside a conversation) and grounds the request in what was already
    // discussed; `request` is what the user asked for this time. Throws
    // InvalidOperationException when the model's output cannot be salvaged into a valid
    // recipe — the orchestrator funnels that to AssistantUnavailable, exactly as the chat
    // and meal-plan lanes do.
    Task<GeneratedRecipe> GenerateAsync(
        string request,
        IReadOnlyList<ChatHistoryItem> history,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default);
}

// One generated recipe, already normalised into domain shapes, plus what the call cost.
// Deliberately NOT a Recipe entity: ownership, provenance and visibility are the
// orchestrator's to decide, not the model's. The draft -> entity step lives beside the
// prompt and the parser in RecipeGenerationAssistant (stream G edits one file).
public record GeneratedRecipe(GeneratedRecipeDraft Draft, ChatTokenUsage? Usage);

public record GeneratedRecipeDraft(
    string Title,
    string Description,
    int PrepTimeMinutes,
    int CookTimeMinutes,
    int Servings,
    DifficultyLevel Difficulty,
    string? CuisineType,
    int? CaloriesPerServing,
    List<RecipeIngredient> Ingredients,
    List<RecipeStep> Steps,
    List<string> Tags);
