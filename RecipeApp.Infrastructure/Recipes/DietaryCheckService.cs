using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

/// <summary>
/// IDietaryCheckService — the catalogue read behind a batch dietary check (stream H).
///
/// This is the only thing the extraction added that RecipeInsightService did not
/// already do: one catalogue query for every recipe in the batch instead of one per
/// recipe. RecipeInsightService keeps loading its own catalogue, because it needs
/// the same rows for nutrition and would otherwise read them twice — it calls
/// <see cref="DietaryCheck.For"/> directly with what it already has.
/// </summary>
public class DietaryCheckService : IDietaryCheckService
{
    private readonly ApplicationDbContext _db;

    public DietaryCheckService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<DietaryCheckResponse>>> CheckAsync(
        IReadOnlyCollection<Recipe> recipes,
        IReadOnlyCollection<DietaryRestriction> restrictions,
        CancellationToken cancellationToken = default)
    {
        // No restrictions means nothing to verify — and, more usefully, no reason to
        // touch the catalogue. The AI lanes call this unconditionally, so the users
        // who set no restrictions (most of them) pay nothing for the feature.
        if (recipes.Count == 0 || restrictions.Count == 0)
        {
            return recipes.ToDictionary(
                r => r.Id,
                _ => (IReadOnlyList<DietaryCheckResponse>)[]);
        }

        var resolvedIds = DietaryCheck.ResolvedIngredientIds(recipes);

        var catalogue = resolvedIds.Count == 0
            ? []
            : await _db.Ingredients
                .Where(i => resolvedIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, cancellationToken);

        // Distinct on id: a proposal can put the same dish in two slots, and checking
        // it twice would be the same answer for a second pass over its lines.
        return recipes
            .DistinctBy(r => r.Id)
            .ToDictionary(
                r => r.Id,
                r => DietaryCheck.For(r.Ingredients, catalogue, restrictions));
    }
}
