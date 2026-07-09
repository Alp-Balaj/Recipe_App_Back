using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.IntegrationTests;

public class RecipeEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task CreateRecipe_WithValidBody_ReturnsCreatedWithOwnerFromToken()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var request = ValidCreateRecipeRequest();

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal($"/recipes/{body!.Id}", response.Headers.Location?.ToString());
        Assert.Equal(auth.UserId, body.CreatedByUserId);
        Assert.Equal(request.Title, body.Title);

        // jsonb round-trip: read the row back through EF (not the in-memory entity the
        // endpoint mapped its response from) to prove the List<> columns survive Postgres.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Recipes.SingleAsync(r => r.Id == body.Id);

        Assert.Equal(auth.UserId, stored.CreatedByUserId);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);

        var ingredient = Assert.Single(stored.Ingredients);
        Assert.Equal("flour", ingredient.Name);
        Assert.Equal(2.5m, ingredient.Quantity);
        Assert.Equal("cups", ingredient.Unit);

        Assert.Equal(2, stored.Steps.Count);
        Assert.Equal("Mix the flour with water.", stored.Steps[0].Description);
        Assert.Equal(600, stored.Steps[1].TimerSeconds);

        Assert.Equal(new List<string> { "vegan", "quick" }, stored.Tags);
    }

    [Fact]
    public async Task CreateRecipe_WithInvalidBody_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var request = ValidCreateRecipeRequest() with { Title = "", Ingredients = [] };

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRecipe_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/recipes", ValidCreateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest() => new(
        Title: "Integration Test Flatbread",
        Description: "A minimal flatbread used to exercise POST /recipes end to end.",
        PrepTimeMinutes: 10,
        CookTimeMinutes: 15,
        Servings: 4,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: "Mediterranean",
        CaloriesPerServing: 180,
        ImageUrl: null,
        Visibility: RecipeVisibility.Public,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 2.5m, Unit = "cups" }],
        Steps:
        [
            new RecipeStep { StepNumber = 1, Description = "Mix the flour with water." },
            new RecipeStep { StepNumber = 2, Description = "Rest the dough.", TimerSeconds = 600 },
        ],
        Tags: ["vegan", "quick"]);
}
