using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

public interface IRecipeService
{
    Task<RecipeResponse> CreateRecipeAsync(CreateRecipeRequest request, Guid createdByUserId, CancellationToken cancellationToken = default);

    Task<RecipeResult<RecipeResponse>> GetRecipeByIdAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default);

    Task<RecipeListResponse> GetRecipesAsync(RecipeListQuery query, Guid currentUserId, CancellationToken cancellationToken = default);
}
