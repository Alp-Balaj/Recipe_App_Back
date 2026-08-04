using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// Computed nutrition and system-side dietary-restriction checking for one recipe
/// (stream G, slice G4). Both are derived from the catalogue on every read — nothing
/// is stored, so neither can go stale against a corrected seed or an edited recipe.
/// </summary>
public interface IRecipeInsightService
{
    /// <summary>
    /// Applies visibility rule 1 like every other recipe read: a recipe the caller
    /// cannot open is NotFound, never Forbidden. The dietary checks are the CALLER's
    /// own restrictions, so an anonymous caller gets nutrition and no checks.
    /// </summary>
    Task<RecipeResult<RecipeInsightsResponse>> GetAsync(
        Guid recipeId, Guid? currentUserId, CancellationToken cancellationToken = default);
}
