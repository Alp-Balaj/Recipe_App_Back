using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RecipeApp.API.Filters;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.API.Endpoints;

public static class RecipeEndpoints
{
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

        group.MapGet("/{id:guid}", async (Guid id, IRecipeService recipeService, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await recipeService.GetRecipeByIdAsync(id, userId);
            return result.Outcome switch
            {
                RecipeOutcome.Success => Results.Ok(result.Value),
                _ => Results.NotFound(),
            };
        });
    }
}
