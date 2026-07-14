using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

public class RecipeService : IRecipeService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<RecipeService> _logger;

    public RecipeService(ApplicationDbContext db, ILogger<RecipeService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RecipeResponse> CreateRecipeAsync(CreateRecipeRequest request, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        var recipe = ToRecipe(request, createdByUserId);

        _db.Recipes.Add(recipe);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} created recipe {RecipeId}.", createdByUserId, recipe.Id);
        return ToRecipeResponse(recipe);
    }

    public async Task<RecipeResult<RecipeResponse>> GetRecipeByIdAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted).
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        // Visibility rule 2 (recipe-management plan): a non-public recipe not owned by the
        // caller is reported as NotFound — never Forbidden — so 404s don't leak that a
        // private recipe exists. FriendsOnly is owner-only until social-features adds follows.
        if (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        return RecipeResult<RecipeResponse>.Success(ToRecipeResponse(recipe));
    }

    public async Task<RecipeListResponse> GetRecipesAsync(RecipeListQuery query, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Visibility rule 1 (recipe-management plan): the visibility predicate is the FIRST
        // predicate composed onto the query, before any user-supplied filter, so no filter
        // combination can widen what the caller may see. FriendsOnly is owner-only until
        // social-features adds follows. Soft-deleted rows are excluded by the global filter.
        var recipes = _db.Recipes
            .Where(r => r.Visibility == RecipeVisibility.Public || r.CreatedByUserId == currentUserId);

        if (!string.IsNullOrEmpty(query.Cuisine))
        {
            // Case-insensitive exact match (Decisions §3) — lower() equality, not ILIKE,
            // so % and _ in user input can't act as wildcards.
            var cuisine = query.Cuisine.ToLowerInvariant();
            recipes = recipes.Where(r => r.CuisineType != null && r.CuisineType.ToLower() == cuisine);
        }

        if (query.Difficulty is not null)
        {
            recipes = recipes.Where(r => r.Difficulty == query.Difficulty);
        }

        // Match-ALL tags (Decisions §3): each Contains translates to jsonb containment
        // ("Tags" @> to_jsonb(@tag)), AND-composed across the requested tags.
        foreach (var tag in query.Tags)
        {
            recipes = recipes.Where(r => r.Tags.Contains(tag));
        }

        if (query.Cursor is not null)
        {
            var cursorCreatedAt = query.Cursor.CreatedAt;
            var cursorId = query.Cursor.Id;
            // Explicit two-branch keyset predicate — EF Core can't translate a row-value
            // (a, b) < (c, d) comparison from LINQ. CompareTo becomes uuid < in SQL.
            recipes = recipes.Where(r =>
                r.CreatedAt < cursorCreatedAt
                || (r.CreatedAt == cursorCreatedAt && r.Id.CompareTo(cursorId) < 0));
        }

        // limit + 1: the extra row only signals that a further page exists; it is trimmed
        // from the response, and the last returned item becomes the next cursor.
        var rows = await recipes
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > query.Limit)
        {
            rows.RemoveAt(query.Limit);
            var last = rows[^1];
            nextCursor = new RecipeListCursor(last.CreatedAt, last.Id).Encode();
        }

        return new RecipeListResponse(rows.Select(ToRecipeResponse).ToList(), nextCursor);
    }

    public async Task<RecipeResult<RecipeResponse>> UpdateRecipeAsync(Guid id, UpdateRecipeRequest request, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted).
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        // Visibility rule 2 (recipe-management plan): a non-public recipe not owned by the
        // caller is NotFound — never Forbidden — so 403s don't leak that a private recipe
        // exists. Only a recipe the caller can see but doesn't own is Forbidden.
        if (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId)
        {
            return RecipeResult<RecipeResponse>.NotFound();
        }

        if (recipe.CreatedByUserId != currentUserId)
        {
            _logger.LogWarning("User {UserId} forbidden from updating recipe {RecipeId}.", currentUserId, id);
            return RecipeResult<RecipeResponse>.Forbidden();
        }

        // Full replace: every request field overwrites the row, and the jsonb collections
        // (Ingredients/Steps/Tags) are swapped wholesale — no merge semantics.
        // Id, CreatedAt, CreatedByUserId, IsDeleted/DeletedAt are never touched.
        recipe.Title = request.Title;
        recipe.Description = request.Description;
        recipe.PrepTimeMinutes = request.PrepTimeMinutes;
        recipe.CookTimeMinutes = request.CookTimeMinutes;
        recipe.Servings = request.Servings;
        recipe.Difficulty = request.Difficulty;
        recipe.CuisineType = request.CuisineType;
        recipe.CaloriesPerServing = request.CaloriesPerServing;
        recipe.ImageUrl = request.ImageUrl;
        recipe.Visibility = request.Visibility;
        recipe.Ingredients = request.Ingredients;
        recipe.Steps = request.Steps;
        recipe.Tags = request.Tags;
        recipe.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return RecipeResult<RecipeResponse>.Success(ToRecipeResponse(recipe));
    }

    public async Task<RecipeResult<bool>> DeleteRecipeAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        // Soft-deleted rows are already excluded by the global query filter (r => !r.IsDeleted),
        // so a repeat DELETE of the same recipe naturally falls through to NotFound.
        var recipe = await _db.Recipes.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (recipe is null)
        {
            return RecipeResult<bool>.NotFound();
        }

        // Visibility rule 2 (recipe-management plan): a non-public recipe not owned by the
        // caller is NotFound — never Forbidden — so 403s don't leak that a private recipe
        // exists. Only a recipe the caller can see but doesn't own is Forbidden.
        if (recipe.Visibility != RecipeVisibility.Public && recipe.CreatedByUserId != currentUserId)
        {
            return RecipeResult<bool>.NotFound();
        }

        if (recipe.CreatedByUserId != currentUserId)
        {
            _logger.LogWarning("User {UserId} forbidden from deleting recipe {RecipeId}.", currentUserId, id);
            return RecipeResult<bool>.Forbidden();
        }

        // Soft delete: flip the flag and stamp DeletedAt, then save — no SQL DELETE is ever
        // issued, so the implicit cascades on Like/Comment/SavedRecipe/MealPlanEntry never
        // fire and interaction history survives. The global query filter hides the row from
        // every later read.
        recipe.IsDeleted = true;
        recipe.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return RecipeResult<bool>.Success(true);
    }

    // Manual DTO->entity mapping (per 02-01/02-04): a private method colocated with the
    // service, named To<Entity>(dto). CreatedByUserId is passed in explicitly from the
    // authenticated user's JWT claims, never taken from the request body.
    private static Recipe ToRecipe(CreateRecipeRequest request, Guid createdByUserId)
    {
        return new Recipe
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Description = request.Description,
            PrepTimeMinutes = request.PrepTimeMinutes,
            CookTimeMinutes = request.CookTimeMinutes,
            Servings = request.Servings,
            Difficulty = request.Difficulty,
            CuisineType = request.CuisineType,
            CaloriesPerServing = request.CaloriesPerServing,
            ImageUrl = request.ImageUrl,
            Visibility = request.Visibility,
            Ingredients = request.Ingredients,
            Steps = request.Steps,
            Tags = request.Tags,
            CreatedByUserId = createdByUserId,
        };
    }

    // Manual entity->DTO mapping (per 02-01/02-04): a private method colocated with the
    // service, named To<Dto>(entity).
    private static RecipeResponse ToRecipeResponse(Recipe recipe)
    {
        return new RecipeResponse(
            recipe.Id,
            recipe.Title,
            recipe.Description,
            recipe.PrepTimeMinutes,
            recipe.CookTimeMinutes,
            recipe.TotalTimeMinutes,
            recipe.Servings,
            recipe.Difficulty,
            recipe.CuisineType,
            recipe.CaloriesPerServing,
            recipe.ImageUrl,
            recipe.Visibility,
            recipe.CreatedAt,
            recipe.UpdatedAt,
            recipe.Ingredients,
            recipe.Steps,
            recipe.Tags,
            recipe.CreatedByUserId);
    }
}
