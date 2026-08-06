using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;

namespace RecipeApp.Application.Recipes;

// Single source of truth for Recipe -> RecipeResponse mapping (per 02-01/02-04). Extracted from
// RecipeService so chat suggestion hydration (chat-ai cp03) maps recipes the same way the
// recipe endpoints do, rather than duplicating the projection.
public static class RecipeMapper
{
    public static RecipeResponse ToResponse(Recipe recipe)
    {
        return new RecipeResponse(
            recipe.Id,
            recipe.Title,
            recipe.Description,
            recipe.PrepTimeMinutes,
            recipe.CookTimeMinutes,
            recipe.TotalTimeMinutes,
            recipe.Servings,
            recipe.Difficulty,
            recipe.CuisineType,
            recipe.CaloriesPerServing,
            recipe.ImageUrl,
            recipe.Visibility,
            recipe.CreatedAt,
            recipe.UpdatedAt,
            recipe.Ingredients,
            recipe.Steps,
            recipe.Tags,
            recipe.CreatedByUserId,
            recipe.IsAiGenerated,
            recipe.SourceConversationId,
            recipe.SourceUrl);
    }
}
