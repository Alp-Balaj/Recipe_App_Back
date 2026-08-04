using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// Reads of the ingredient catalogue (stream G, slice G2). Public reference data — the
/// catalogue belongs to nobody, so there is no caller id here and no visibility rule to
/// apply, which is the first read in this codebase that is genuinely unscoped.
/// </summary>
public interface IIngredientCatalogueService
{
    /// <summary>
    /// The catalogue, optionally narrowed by a prefix/substring search over names and
    /// aliases. <paramref name="limit"/> bounds the page; the response carries the total.
    /// </summary>
    Task<IngredientListResponse> SearchAsync(
        string? query, int limit, CancellationToken cancellationToken = default);
}
