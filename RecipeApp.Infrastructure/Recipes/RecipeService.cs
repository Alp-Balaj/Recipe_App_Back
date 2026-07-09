using Microsoft.EntityFrameworkCore;
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

    public RecipeService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<RecipeResponse> CreateRecipeAsync(CreateRecipeRequest request, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        var recipe = ToRecipe(request, createdByUserId);

        _db.Recipes.Add(recipe);
        await _db.SaveChangesAsync(cancellationToken);

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
