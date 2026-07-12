using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;

namespace RecipeApp.API.Endpoints;

public static class RecipeEndpoints
{
    // Page size policy for GET /recipes (recipe-management plan, Decisions §6):
    // default 20, above-cap values clamp silently to 50, zero/negative are a 400.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;

    public static void MapRecipeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/recipes");

        group.MapPost("/", async (CreateRecipeRequest request, IRecipeService recipeService, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var response = await recipeService.CreateRecipeAsync(request, userId);
            return Results.Created($"/recipes/{response.Id}", response);
        })
        .AddEndpointFilter<ValidationFilter<CreateRecipeRequest>>();

        group.MapGet("/", async (
            string? cuisine,
            string? difficulty,
            string[]? tags,
            string? cursor,
            int? limit,
            IRecipeService recipeService,
            ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);

            // difficulty is bound as a string and parsed by hand so that undefined values
            // (including numeric strings outside the enum, e.g. "99") are a 400 rather
            // than an undefined enum value that silently matches nothing.
            DifficultyLevel? difficultyFilter = null;
            if (!string.IsNullOrEmpty(difficulty))
            {
                if (!Enum.TryParse<DifficultyLevel>(difficulty, ignoreCase: true, out var parsedDifficulty)
                    || !Enum.IsDefined(parsedDifficulty))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["difficulty"] = [$"'{difficulty}' is not a valid difficulty."],
                    });
                }
                difficultyFilter = parsedDifficulty;
            }

            RecipeListCursor? decodedCursor = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                if (!RecipeListCursor.TryDecode(cursor, out decodedCursor))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["cursor"] = ["The cursor is malformed."],
                    });
                }
            }

            var effectiveLimit = limit ?? DefaultPageSize;
            if (effectiveLimit <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["limit"] = ["limit must be a positive integer."],
                });
            }
            effectiveLimit = Math.Min(effectiveLimit, MaxPageSize);

            var query = new RecipeListQuery(cuisine, difficultyFilter, tags ?? [], decodedCursor, effectiveLimit);
            var response = await recipeService.GetRecipesAsync(query, userId);
            return Results.Ok(response);
        });

        group.MapGet("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.GetRecipeByIdAsync(id, userId);
            // GET detail never returns 403 — everything non-Success collapses to 404 so
            // the response can't confirm a private recipe exists.
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateRecipeRequest request, IRecipeService recipeService, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.UpdateRecipeAsync(id, request, userId);
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                RecipeOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<UpdateRecipeRequest>>();

        group.MapDelete("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.DeleteRecipeAsync(id, userId);
            // Soft delete: 204 on success (no body). Same 403-vs-404 owner/visibility split
            // as PUT; a repeat DELETE lands in NotFound once the row is behind the filter.
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.NoContent(),
                RecipeOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        });
    }
}
