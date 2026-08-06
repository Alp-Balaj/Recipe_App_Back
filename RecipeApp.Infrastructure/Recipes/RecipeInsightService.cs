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
            ComputeNutrition(recipe, catalogue),
            // The check moved to Application/Recipes/DietaryCheck.cs (stream H, 2026-08-06)
            // so propose-week and the generator can run the same one — see that file for why
            // it had to leave this class. The catalogue stays loaded HERE and is passed in:
            // nutrition needs the same rows, and routing this through IDietaryCheckService
            // would read them a second time for one recipe.
            DietaryCheck.For(recipe.Ingredients, catalogue, restrictions)));
    }

    /// <summary>
    /// Sums each line's grams against its catalogue entry's per-100 g figures, then
    /// divides by servings.
    ///
    /// A line contributes only if it resolved AND converts to grams (see
    /// NutritionEstimate). Everything else is counted as uncovered rather than
    /// treated as zero — a zero would silently drag the total down and make a
    /// partially-known recipe look low-calorie, which is the specific way a
    /// nutrition figure becomes actively misleading rather than merely incomplete.
    /// </summary>
    private static ComputedNutritionResponse ComputeNutrition(
        Recipe recipe, IReadOnlyDictionary<Guid, Ingredient> catalogue)
    {
        double kcal = 0, protein = 0, fat = 0, carbs = 0, fibre = 0;
        var covered = 0;
        // Tracked per nutrient: USDA publishes calories for everything in the
        // catalogue but fibre for rather less, so a protein total can be complete
        // while a fibre total is not.
        bool anyKcal = false, anyProtein = false, anyFat = false, anyCarbs = false, anyFibre = false;

        foreach (var line in recipe.Ingredients)
        {
            if (line.IngredientId is not Guid id || !catalogue.TryGetValue(id, out var ingredient))
            {
                continue;
            }

            var grams = NutritionEstimate.GramsFor(
                line.Quantity, line.Unit, ingredient.GramsPerMillilitre, ingredient.GramsPerPiece);

            if (grams is not decimal g)
            {
                continue;
            }

            covered++;
            var hundreds = (double)g / 100.0;

            if (ingredient.Kcal is double k) { kcal += k * hundreds; anyKcal = true; }
            if (ingredient.ProteinG is double p) { protein += p * hundreds; anyProtein = true; }
            if (ingredient.FatG is double f) { fat += f * hundreds; anyFat = true; }
            if (ingredient.CarbsG is double c) { carbs += c * hundreds; anyCarbs = true; }
            if (ingredient.FibreG is double fb) { fibre += fb * hundreds; anyFibre = true; }
        }

        var servings = Math.Max(1, recipe.Servings);

        return new ComputedNutritionResponse(
            anyKcal ? (int)Math.Round(kcal / servings, MidpointRounding.AwayFromZero) : null,
            anyProtein ? Math.Round(protein / servings, 1) : null,
            anyFat ? Math.Round(fat / servings, 1) : null,
            anyCarbs ? Math.Round(carbs / servings, 1) : null,
            anyFibre ? Math.Round(fibre / servings, 1) : null,
            covered,
            recipe.Ingredients.Count);
    }
}
