using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

public interface IRecipeService
{
    Task<RecipeResponse> CreateRecipeAsync(CreateRecipeRequest request, Guid createdByUserId, CancellationToken cancellationToken = default);

    // Guest access (guest-access plan §3.2): the read methods take a NULLABLE caller id —
    // null means an anonymous caller, who sees Public recipes only. Write methods keep the
    // non-nullable Guid; they are only reachable authenticated.
    Task<RecipeResult<RecipeResponse>> GetRecipeByIdAsync(Guid id, Guid? currentUserId, CancellationToken cancellationToken = default);

    Task<RecipeListResponse> GetRecipesAsync(RecipeListQuery query, Guid? currentUserId, CancellationToken cancellationToken = default);

    Task<RecipeResult<RecipeResponse>> UpdateRecipeAsync(Guid id, UpdateRecipeRequest request, Guid currentUserId, CancellationToken cancellationToken = default);

    // Soft delete restricted to the owner. RecipeResult<bool> carries no meaningful payload
    // (the endpoint maps Success to 204 No Content); bool is a throwaway T for the generic.
    Task<RecipeResult<bool>> DeleteRecipeAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default);
}
