using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

/// <summary>
/// GET /recipes/{id}/insights (stream G, slice G4). Computed nutrition and a
/// system-side dietary-restriction check, both off the catalogue.
/// </summary>
public class RecipeInsightService : IRecipeInsightService
{
    private readonly ApplicationDbContext _db;

    public RecipeInsightService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<RecipeResult<RecipeInsightsResponse>> GetAsync(
        Guid recipeId, Guid? currentUserId, CancellationToken cancellationToken = default)
    {
        // Visibility rule 1, exactly as GetRecipeByIdAsync applies it — the same shared
        // RecipeVisibilityPolicy expression, and for the same reason: insights describe a
        // recipe, so they must not be readable for one the caller cannot open. Everything
        // non-Success collapses to 404 upstream.
        var recipe = await _db.Recipes
            .Where(RecipeVisibilityPolicy.VisibleTo(currentUserId))
            .SingleOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            return RecipeResult<RecipeInsightsResponse>.NotFound();
        }

        var resolvedIds = recipe.Ingredients
            .Where(i => i.IngredientId is not null)
            .Select(i => i.IngredientId!.Value)
            .Distinct()
            .ToList();

        var catalogue = resolvedIds.Count == 0
            ? []
            : await _db.Ingredients
                .Where(i => resolvedIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, cancellationToken);

        var restrictions = currentUserId is Guid userId
            ? await _db.Users.Where(u => u.Id == userId)
                .Select(u => u.DietaryRestrictions)
                .SingleOrDefaultAsync(cancellationToken) ?? []
            : [];

        return RecipeResult<RecipeInsightsResponse>.Success(new RecipeInsightsResponse(
            ToResponse(RecipeNutrition.PerServing(recipe, catalogue)),
            CheckRestrictions(recipe, catalogue, restrictions)));
    }

    /// <summary>
    /// Rounds one recipe's raw per-serving totals onto the wire (stream I moved the
    /// summing itself into RecipeNutrition, so the plan surfaces compute the same
    /// figure rather than a second one that drifts).
    ///
    /// The rounding stays HERE rather than in the domain because it is a property of
    /// this response — the plan's day ribbon sums several servings before it rounds,
    /// and rounding each meal first would make a day's total disagree with its parts.
    /// </summary>
    private static ComputedNutritionResponse ToResponse(NutritionTotals totals) =>
        new(
            totals.Kcal is double kcal ? (int)Math.Round(kcal, MidpointRounding.AwayFromZero) : null,
            totals.ProteinG is double protein ? Math.Round(protein, 1) : null,
            totals.FatG is double fat ? Math.Round(fat, 1) : null,
            totals.CarbsG is double carbs ? Math.Round(carbs, 1) : null,
            totals.FibreG is double fibre ? Math.Round(fibre, 1) : null,
            totals.CoveredLines,
            totals.TotalLines);

    /// <summary>
    /// Runs the caller's own restrictions against the recipe's RESOLVED ingredients.
    ///
    /// UncheckableLines is the honest half and is reported per restriction: a line
    /// that did not resolve has no catalogue name to test, so it was not checked at
    /// all. A client showing "no conflicts" without showing that number would be
    /// making a safety claim this cannot support.
    /// </summary>
    private static List<DietaryCheckResponse> CheckRestrictions(
        Recipe recipe,
        IReadOnlyDictionary<Guid, Ingredient> catalogue,
        List<DietaryRestriction> restrictions)
    {
        if (restrictions.Count == 0)
        {
            return [];
        }

        var checkable = recipe.Ingredients
            .Where(i => i.IngredientId is not null && catalogue.ContainsKey(i.IngredientId!.Value))
            .Select(i => catalogue[i.IngredientId!.Value])
            .Select(i => (i.Name, i.Category))
            .ToList();

        var uncheckable = recipe.Ingredients.Count - checkable.Count;

        return restrictions
            .Distinct()
            .Select(restriction => new DietaryCheckResponse(
                restriction,
                DietaryRules.Conflicts(restriction, checkable)
                    .Select(c => new DietaryConflictResponse(c.Name, c.Reason))
                    .ToList(),
                uncheckable))
            .ToList();
    }
}
