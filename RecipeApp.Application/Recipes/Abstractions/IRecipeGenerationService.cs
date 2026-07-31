using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

// Endpoint-facing orchestration for POST /recipes/generate (stream E). Same division of
// labour as ChatService/MealPlanProposalService: this owns the quota gate, the conversation
// ownership check, persistence and the rank decision; IRecipeGenerationAssistant owns
// everything about what the model is asked and what comes back.
public interface IRecipeGenerationService
{
    Task<RecipeGenerationResult<GenerateRecipeResponse>> GenerateRecipeAsync(
        GenerateRecipeRequest request,
        Guid userId,
        CancellationToken cancellationToken = default);
}
