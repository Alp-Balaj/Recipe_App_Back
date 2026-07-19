using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// meal-planning plan, cp02: POST /meal-plans, GET /meal-plans/{id}, POST/DELETE
// /meal-plans/{id}/entries[/{entryId}]. Fresh users/recipes per test (shared Testcontainers
// DB), mirroring SocialInteractionEndpointsTests' style.
public class MealPlanEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    // --- create ---------------------------------------------------------------------------

    [Fact]
    public async Task CreateMealPlan_Returns201WithShape()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var weekStart = UtcMidnight(2026, 7, 20);

        var response = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStart), TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal(weekStart, body.WeekStartDate);
        Assert.Empty(body.Entries);
        Assert.Equal($"/meal-plans/{body.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task CreateMealPlan_NonMidnightUtcDate_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var weekStart = UtcMidnight(2026, 7, 20).AddHours(3);

        var response = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStart), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMealPlan_NonUtcKindDate_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        // No trailing 'Z'/offset -> System.Text.Json decodes Kind=Unspecified, which fails
        // the "Kind Utc after binding" rule even though the time-of-day is midnight.
        var content = new StringContent("{\"weekStartDate\":\"2026-07-20T00:00:00\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/meal-plans", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateMealPlan_DuplicateWeek_Returns409()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var weekStart = UtcMidnight(2026, 8, 3);

        var first = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStart), TestJson.Options);
        var second = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStart), TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // --- get --------------------------------------------------------------------------------

    [Fact]
    public async Task GetMealPlan_OwnPlanWithEntries_Returns200()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 9, 7));
        var recipe = await CreateRecipeAsync(client);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, recipe.Id);

        var response = await client.GetAsync($"/meal-plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(plan.Id, body!.Id);
        var entry = Assert.Single(body.Entries);
        Assert.Equal(DayOfWeek.Monday, entry.DayOfWeek);
        Assert.Equal(MealType.Breakfast, entry.MealType);
        Assert.Equal(recipe.Id, entry.Recipe.Id);
        Assert.Equal(recipe.Title, entry.Recipe.Title);
    }

    [Fact]
    public async Task GetMealPlan_OtherUsersPlan_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var plan = await CreateMealPlanAsync(ownerClient, UtcMidnight(2026, 9, 14));

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.GetAsync($"/meal-plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Carry-over from cp2 verification: entries whose Recipe is soft-deleted are silently
    // omitted from the week view (chat-suggestion convention), not just filtered at the
    // point of add. Soft-delete via the real DELETE /recipes/{id} endpoint, not direct DB
    // manipulation, so this exercises the actual global query-filter path.
    [Fact]
    public async Task GetMealPlan_EntryWithSoftDeletedRecipe_IsOmittedButOthersRemain()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 9, 21));
        var keptRecipe = await CreateRecipeAsync(client);
        var doomedRecipe = await CreateRecipeAsync(client);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, keptRecipe.Id);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Tuesday, MealType.Lunch, doomedRecipe.Id);

        (await client.DeleteAsync($"/recipes/{doomedRecipe.Id}")).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/meal-plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);
        Assert.NotNull(body);
        var entry = Assert.Single(body!.Entries);
        Assert.Equal(keptRecipe.Id, entry.Recipe.Id);
    }

    [Fact]
    public async Task GetMealPlan_Unknown_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync($"/meal-plans/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- add entry --------------------------------------------------------------------------

    [Fact]
    public async Task AddEntry_Returns201()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 10, 5));
        var recipe = await CreateRecipeAsync(client);

        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{plan.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Tuesday, MealType.Lunch, recipe.Id),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MealPlanEntryResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(DayOfWeek.Tuesday, body!.DayOfWeek);
        Assert.Equal(MealType.Lunch, body.MealType);
        Assert.Equal(recipe.Id, body.Recipe.Id);
    }

    [Fact]
    public async Task AddEntry_ToOtherUsersPlan_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var plan = await CreateMealPlanAsync(ownerClient, UtcMidnight(2026, 10, 12));
        var recipe = await CreateRecipeAsync(ownerClient);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsJsonAsync(
            $"/meal-plans/{plan.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Wednesday, MealType.Dinner, recipe.Id),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddEntry_InvisibleRecipeOfAnotherUser_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 10, 19));

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        var privateRecipe = await CreateRecipeAsync(otherClient, RecipeVisibility.Private);

        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{plan.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Thursday, MealType.Dinner, privateRecipe.Id),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddEntry_AuthorsOwnPrivateRecipe_Returns201()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 10, 26));
        var ownPrivateRecipe = await CreateRecipeAsync(client, RecipeVisibility.Private);

        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{plan.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Friday, MealType.Snack, ownPrivateRecipe.Id),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task AddEntry_OccupiedSlot_Returns409()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 11, 2));
        var recipeOne = await CreateRecipeAsync(client);
        var recipeTwo = await CreateRecipeAsync(client);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Saturday, MealType.Breakfast, recipeOne.Id);

        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{plan.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Saturday, MealType.Breakfast, recipeTwo.Id),
            TestJson.Options);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddEntry_MalformedDayOfWeekString_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 11, 9));
        var recipe = await CreateRecipeAsync(client);

        var json = JsonSerializer.Serialize(new
        {
            dayOfWeek = "Fourthday",
            mealType = "Breakfast",
            recipeId = recipe.Id,
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"/meal-plans/{plan.Id}/entries", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- remove entry -----------------------------------------------------------------------

    [Fact]
    public async Task RemoveEntry_Returns204()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 11, 16));
        var recipe = await CreateRecipeAsync(client);
        var entry = await AddEntryAsync(client, plan.Id, DayOfWeek.Sunday, MealType.Dessert, recipe.Id);

        var response = await client.DeleteAsync($"/meal-plans/{plan.Id}/entries/{entry.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getAfter = await client.GetFromJsonAsync<MealPlanResponse>($"/meal-plans/{plan.Id}", TestJson.Options);
        Assert.Empty(getAfter!.Entries);
    }

    [Fact]
    public async Task RemoveEntry_CrossPlan_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var planA = await CreateMealPlanAsync(client, UtcMidnight(2026, 11, 23));
        var planB = await CreateMealPlanAsync(client, UtcMidnight(2026, 11, 30));
        var recipe = await CreateRecipeAsync(client);
        var entry = await AddEntryAsync(client, planA.Id, DayOfWeek.Monday, MealType.Lunch, recipe.Id);

        // entry belongs to planA, not planB
        var response = await client.DeleteAsync($"/meal-plans/{planB.Id}/entries/{entry.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveEntry_CrossUser_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var plan = await CreateMealPlanAsync(ownerClient, UtcMidnight(2026, 12, 7));
        var recipe = await CreateRecipeAsync(ownerClient);
        var entry = await AddEntryAsync(ownerClient, plan.Id, DayOfWeek.Tuesday, MealType.Lunch, recipe.Id);

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.DeleteAsync($"/meal-plans/{plan.Id}/entries/{entry.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- helpers ------------------------------------------------------------------------

    private static DateTime UtcMidnight(int year, int month, int day) =>
        DateTime.SpecifyKind(new DateTime(year, month, day), DateTimeKind.Utc);

    private static async Task<MealPlanResponse> CreateMealPlanAsync(HttpClient client, DateTime weekStartDate)
    {
        var response = await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(weekStartDate), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options))!;
    }

    private static async Task<MealPlanEntryResponse> AddEntryAsync(HttpClient client, Guid mealPlanId, DayOfWeek dayOfWeek, MealType mealType, Guid recipeId)
    {
        var response = await client.PostAsJsonAsync(
            $"/meal-plans/{mealPlanId}/entries",
            new AddMealPlanEntryRequest(dayOfWeek, mealType, recipeId),
            TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MealPlanEntryResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, RecipeVisibility visibility = RecipeVisibility.Public)
    {
        var response = await client.PostAsJsonAsync("/recipes", ValidCreateRecipeRequest(visibility), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }

    private static CreateRecipeRequest ValidCreateRecipeRequest(RecipeVisibility visibility = RecipeVisibility.Public) => new(
        Title: "Meal Plan Test Focaccia",
        Description: "A minimal focaccia used to exercise the meal-plan endpoints.",
        PrepTimeMinutes: 10,
        CookTimeMinutes: 20,
        Servings: 4,
        Difficulty: DifficultyLevel.Easy,
        CuisineType: "Italian",
        CaloriesPerServing: 210,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = "cups" }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
        Tags: ["bread"]);
}
