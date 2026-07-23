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

        group.MapPost("/", async (CreateRecipeRequest request, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var response = await recipeService.CreateRecipeAsync(request, userId, cancellationToken);
            return Results.Created($"/recipes/{response.Id}", response);
        })
        .AddEndpointFilter<ValidationFilter<CreateRecipeRequest>>();

        // Guest access (§3.1): the two read routes are anonymous-capable — the caller id is
        // read as nullable and the services degrade to Public-only for a null caller. They
        // also pick up the IP-partitioned Social rate-limit policy (§3.4): anonymous traffic
        // is now possible here, and these were the only unbudgeted public reads.
        group.MapGet("/", async (
            string? cuisine,
            string? difficulty,
            string[]? tags,
            string? cursor,
            int? limit,
            IRecipeService recipeService,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var userId = GetOptionalUserId(user);

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
            var response = await recipeService.GetRecipesAsync(query, userId, cancellationToken);
            return Results.Ok(response);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapGet("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = GetOptionalUserId(user);
            var result = await recipeService.GetRecipeByIdAsync(id, userId, cancellationToken);
            // GET detail never returns 403 — everything non-Success collapses to 404 so
            // the response can't confirm a private recipe exists. For an anonymous caller
            // every non-Public recipe lands here too — existence never leaks to guests.
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Social);

        group.MapPut("/{id:guid}", async (Guid id, UpdateRecipeRequest request, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.UpdateRecipeAsync(id, request, userId, cancellationToken);
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                RecipeOutcome.Forbidden => Results.Forbid(),
                _ => Results.NotFound(),
            };
        })
        .AddEndpointFilter<ValidationFilter<UpdateRecipeRequest>>();

        group.MapDelete("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.DeleteRecipeAsync(id, userId, cancellationToken);
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

    // Guest access (§3.1): the anonymous-capable read routes resolve the caller id as
    // nullable — no claim (or an unparsable one) is an anonymous caller, not a throw.
    // The write routes keep the parsing GetUserId shape above (auth guarantees the claim).
    private static Guid? GetOptionalUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
