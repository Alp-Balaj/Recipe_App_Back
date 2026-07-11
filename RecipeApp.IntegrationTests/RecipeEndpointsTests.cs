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

    [Fact]
    public async Task GetRecipeById_PublicRecipe_ReturnsOkForAnotherUser()
    {
        var ownerClient = factory.CreateClient();
        var owner = await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest());

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal(created.Title, body.Title);
        Assert.Equal(owner.UserId, body.CreatedByUserId);
        Assert.Equal(created.Ingredients.Count, body.Ingredients.Count);
        Assert.Equal(created.Steps.Count, body.Steps.Count);
        Assert.Equal(created.Tags, body.Tags);
    }

    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task GetRecipeById_NonPublicRecipe_ReturnsOkForOwner(RecipeVisibility visibility)
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest() with { Visibility = visibility });

        var response = await client.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal(visibility, body.Visibility);
        Assert.Equal(auth.UserId, body.CreatedByUserId);
    }

    // 404 — not 403 — so the response doesn't confirm the private recipe exists.
    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task GetRecipeById_AnotherUsersNonPublicRecipe_ReturnsNotFound(RecipeVisibility visibility)
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest() with { Visibility = visibility });

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecipeById_NonexistentId_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/recipes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecipeById_SoftDeletedRecipe_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        // No DELETE endpoint until checkpoint 05 — soft-delete the row directly.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Recipes.SingleAsync(r => r.Id == created.Id);
            stored.IsDeleted = true;
            stored.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/recipes/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRecipeById_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/recipes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_AsOwner_ReturnsOkWithFullyReplacedRecipe()
    {
        var client = factory.CreateClient();
        var auth = await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());
        var update = ValidUpdateRecipeRequest();

        // Capture CreatedAt as Postgres materialized it (microsecond precision) — the POST
        // response carries the in-memory pre-save DateTime.UtcNow (100 ns ticks), which
        // differs from the stored value by sub-microsecond truncation.
        DateTime createdAtInDb;
        using (var preUpdateScope = factory.Services.CreateScope())
        {
            var preUpdateDb = preUpdateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            createdAtInDb = (await preUpdateDb.Recipes.SingleAsync(r => r.Id == created.Id)).CreatedAt;
        }

        var response = await client.PutAsJsonAsync($"/recipes/{created.Id}", update, TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body!.Id);
        Assert.Equal(update.Title, body.Title);
        Assert.Equal(update.Description, body.Description);
        Assert.Equal(update.Difficulty, body.Difficulty);
        Assert.Equal(update.CuisineType, body.CuisineType);
        Assert.Equal(update.Visibility, body.Visibility);
        Assert.NotNull(body.UpdatedAt);
        Assert.Equal(createdAtInDb, body.CreatedAt);
        Assert.Equal(auth.UserId, body.CreatedByUserId);

        // jsonb full replace: re-read the row through a fresh DbContext scope (not the
        // endpoint's in-memory response) to prove the List<> columns were overwritten
        // wholesale in Postgres.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Recipes.SingleAsync(r => r.Id == created.Id);

        Assert.Equal(update.Title, stored.Title);
        Assert.NotNull(stored.UpdatedAt);
        Assert.Equal(createdAtInDb, stored.CreatedAt);
        Assert.Equal(auth.UserId, stored.CreatedByUserId);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);

        var ingredient = Assert.Single(stored.Ingredients);
        Assert.Equal("rye flour", ingredient.Name);
        Assert.Equal(3m, ingredient.Quantity);
        Assert.Equal("cups", ingredient.Unit);

        var step = Assert.Single(stored.Steps);
        Assert.Equal("Knead the rye dough and bake.", step.Description);
        Assert.Equal(1200, step.TimerSeconds);

        Assert.Equal(new List<string> { "hearty", "baked" }, stored.Tags);
    }

    // Visible-but-not-owned is a 403; the recipe's existence is already public knowledge.
    [Fact]
    public async Task UpdateRecipe_AnotherUsersPublicRecipe_ReturnsForbidden()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest());

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // 404 — not 403 — so the response doesn't confirm the private recipe exists.
    [Theory]
    [InlineData(RecipeVisibility.Private)]
    [InlineData(RecipeVisibility.FriendsOnly)]
    public async Task UpdateRecipe_AnotherUsersNonPublicRecipe_ReturnsNotFound(RecipeVisibility visibility)
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var created = await CreateRecipeAsync(ownerClient, ValidCreateRecipeRequest() with { Visibility = visibility });

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_NonexistentId_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PutAsJsonAsync($"/recipes/{Guid.NewGuid()}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_SoftDeletedRecipe_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());

        // No DELETE endpoint until checkpoint 05 — soft-delete the row directly.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Recipes.SingleAsync(r => r.Id == created.Id);
            stored.IsDeleted = true;
            stored.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.PutAsJsonAsync($"/recipes/{created.Id}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_WithInvalidBody_ReturnsBadRequest()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var created = await CreateRecipeAsync(client, ValidCreateRecipeRequest());
        var update = ValidUpdateRecipeRequest() with { Title = "", Ingredients = [] };

        var response = await client.PutAsJsonAsync($"/recipes/{created.Id}", update, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRecipe_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/recipes/{Guid.NewGuid()}", ValidUpdateRecipeRequest(), TestJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, CreateRecipeRequest request)
    {
        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options)
            ?? throw new InvalidOperationException("POST /recipes returned an empty body.");
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

    // Every field differs from ValidCreateRecipeRequest so the full-replace assertions
    // can't pass by accident.
    private static UpdateRecipeRequest ValidUpdateRecipeRequest() => new(
        Title: "Integration Test Rye Bread",
        Description: "A replacement recipe used to exercise PUT /recipes/{id} end to end.",
        PrepTimeMinutes: 20,
        CookTimeMinutes: 40,
        Servings: 6,
        Difficulty: DifficultyLevel.Medium,
        CuisineType: "Nordic",
        CaloriesPerServing: 250,
        ImageUrl: "https://example.test/rye.jpg",
        Visibility: RecipeVisibility.Public,
        Ingredients: [new RecipeIngredient { Name = "rye flour", Quantity = 3m, Unit = "cups" }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Knead the rye dough and bake.", TimerSeconds = 1200 }],
        Tags: ["hearty", "baked"]);
}
