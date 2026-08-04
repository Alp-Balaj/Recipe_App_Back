using Microsoft.EntityFrameworkCore;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

/// <summary>
/// GET /ingredients (stream G, slice G2).
/// </summary>
public class IngredientCatalogueService : IIngredientCatalogueService
{
    private readonly ApplicationDbContext _db;

    public IngredientCatalogueService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IngredientListResponse> SearchAsync(
        string? query, int limit, CancellationToken cancellationToken = default)
    {
        var total = await _db.Ingredients.CountAsync(cancellationToken);

        var ingredients = _db.Ingredients.AsQueryable();

        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ILIKE on the display NAME, plus an exact hit on the alias key.
            //
            // The two answer different questions and a picker needs both. The ILIKE is
            // the browse ("chick" -> chickpeas, chicken breast), and the alias hit is the
            // resolution the WRITE path will actually perform — so typing "prawns" finds
            // shrimp, which no substring search over names ever could. Ordering puts the
            // exact-key match first for exactly that reason: it is the entry this name
            // will resolve to when the recipe is saved, and the picker should agree with
            // the resolver rather than quietly disagree.
            //
            // EF.Functions.Like with an escaped pattern, not raw concatenation: % and _
            // in user input must be literals here, unlike the tsquery path where the
            // parser handles it.
            var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            var key = IngredientKey.For(term);

            ingredients = ingredients.Where(i =>
                EF.Functions.ILike(i.Name, pattern, "\\") ||
                i.Aliases.Any(a => a.MatchKey == key));

            return await ProjectAsync(
                ingredients
                    .OrderByDescending(i => i.Aliases.Any(a => a.MatchKey == key))
                    .ThenBy(i => i.Name)
                    .Take(limit),
                total,
                cancellationToken);
        }

        return await ProjectAsync(
            ingredients.OrderBy(i => i.Category).ThenBy(i => i.Name).Take(limit),
            total,
            cancellationToken);
    }

    private static async Task<IngredientListResponse> ProjectAsync(
        IQueryable<Ingredient> ingredients, int total, CancellationToken cancellationToken)
    {
        var items = await ingredients
            .Select(i => new IngredientResponse(
                i.Id, i.Name, i.Category,
                i.GramsPerMillilitre, i.GramsPerPiece,
                i.Kcal, i.ProteinG, i.FatG, i.CarbsG, i.FibreG,
                i.FdcId))
            .ToListAsync(cancellationToken);

        return new IngredientListResponse(items, total);
    }
}
