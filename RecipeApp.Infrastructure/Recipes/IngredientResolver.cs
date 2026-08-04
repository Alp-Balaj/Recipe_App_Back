using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

/// <summary>
/// The write-path resolver (stream G, slice G2 — decision D8). See
/// <see cref="IIngredientResolver"/> for the contract; this is the whole implementation
/// and it is deliberately this short.
/// </summary>
public class IngredientResolver : IIngredientResolver
{
    private readonly ApplicationDbContext _db;

    public IngredientResolver(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task ResolveAsync(
        IReadOnlyList<RecipeIngredient> ingredients, CancellationToken cancellationToken = default)
    {
        if (ingredients.Count == 0)
        {
            return;
        }

        // Key every line first, then one query for the distinct keys. Per-line lookups
        // would be up to 60 round trips on a single recipe save (the generator's ceiling
        // is 60 ingredients), and the whole point of MatchKey being the primary key is
        // that this is an index probe either way.
        var keys = ingredients
            .Select(i => IngredientKey.For(i.Name))
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0)
        {
            return;
        }

        var owners = await _db.IngredientAliases
            .Where(a => keys.Contains(a.MatchKey))
            .ToDictionaryAsync(a => a.MatchKey, a => a.IngredientId, StringComparer.Ordinal, cancellationToken);

        foreach (var ingredient in ingredients)
        {
            var key = IngredientKey.For(ingredient.Name);

            // Assigned unconditionally, including to null on a miss. That matters on the
            // UPDATE path: an author who corrects "flor" to "flour" must gain the id, and
            // one who changes "flour" to "gochujang" must LOSE it. Leaving a stale id
            // behind would be worse than never resolving — the recipe would claim a
            // catalogue entry that no longer describes it, and G4's nutrition would be
            // computed from the wrong food.
            ingredient.IngredientId = key.Length > 0 && owners.TryGetValue(key, out var id) ? id : null;
        }
    }
}
