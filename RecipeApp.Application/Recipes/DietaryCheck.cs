using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes;

/// <summary>
/// The per-recipe dietary check, EXTRACTED from RecipeInsightService by stream H
/// (2026-08-06) so more than one surface can run it.
///
/// It moved because of what G4 shipped versus what G4 wired: the checker itself is
/// a Domain service (<see cref="DietaryRules"/>) and was always shareable, but the
/// orchestration around it — which lines are checkable, how many were not, one
/// verdict per restriction — lived as a private method on the recipe-detail read
/// path, so the two AI lanes could not reach it. That is the gap stream H exists
/// to close: the model is TOLD the restriction, and now the system VERIFIES it at
/// the boundary where the model's answer arrives, not only when somebody later
/// opens the recipe.
///
/// Nothing about the logic changed in the move. This is deliberately still pure —
/// no DbContext, no user lookup — so the caller decides where the catalogue and
/// the restrictions came from. <see cref="Abstractions.IDietaryCheckService"/> is
/// the batch loader for callers that have neither loaded yet.
///
/// The honesty rules travel with it, and they are not stylistic. Read
/// <see cref="DietaryRules"/>'s class comment before changing anything here: this
/// reports conflicts FOUND, it reports how many lines it could not read, and it
/// never claims a recipe is safe.
/// </summary>
public static class DietaryCheck
{
    /// <summary>
    /// One verdict per (distinct) restriction, or an empty list when the caller has
    /// none — there is nothing to verify against.
    /// </summary>
    /// <param name="ingredients">The recipe's lines, resolved or not.</param>
    /// <param name="catalogue">Catalogue rows for the lines that resolved.</param>
    /// <param name="restrictions">The CALLER's own restrictions, never the author's.</param>
    public static IReadOnlyList<DietaryCheckResponse> For(
        IReadOnlyCollection<RecipeIngredient> ingredients,
        IReadOnlyDictionary<Guid, Ingredient> catalogue,
        IReadOnlyCollection<DietaryRestriction> restrictions)
    {
        if (restrictions.Count == 0)
        {
            return [];
        }

        var checkable = ingredients
            .Where(i => i.IngredientId is not null && catalogue.ContainsKey(i.IngredientId!.Value))
            .Select(i => catalogue[i.IngredientId!.Value])
            .Select(i => (i.Name, i.Category))
            .ToList();

        // The honest half, and the reason a clean verdict is not a clearance: a line
        // that did not resolve has no catalogue name to test, so it was not checked at
        // all. Reported per restriction because that is the unit a client renders.
        var uncheckable = ingredients.Count - checkable.Count;

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

    /// <summary>
    /// The distinct catalogue ids a set of recipes' lines resolved to — what a caller
    /// must load before calling <see cref="For"/>. One place so the batch service and
    /// any future caller agree on which lines are worth a catalogue read.
    /// </summary>
    public static IReadOnlyList<Guid> ResolvedIngredientIds(IEnumerable<Recipe> recipes)
        => recipes
            .SelectMany(r => r.Ingredients)
            .Where(i => i.IngredientId is not null)
            .Select(i => i.IngredientId!.Value)
            .Distinct()
            .ToList();
}
