using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// meal-planning-ui plan, Task 1: GET /meal-plans (keyset list + exact ?weekStart= filter).
// Mirrors ShoppingListEndpointsTests' style — fresh user per test, shared Testcontainers DB.
public class MealPlanListEndpointTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    private static DateTime UtcMidnight(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task List_ReturnsCallersPlans_NewestWeekFirst()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        foreach (var day in new[] { 6, 20, 13 })
        {
            var created = await client.PostAsJsonAsync("/meal-plans",
                new CreateMealPlanRequest(UtcMidnight(2026, 7, day)), TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var response = await client.GetAsync("/meal-plans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.Equal(3, body!.Items.Count);
        Assert.Equal(UtcMidnight(2026, 7, 20), body.Items[0].WeekStartDate);
        Assert.Equal(UtcMidnight(2026, 7, 13), body.Items[1].WeekStartDate);
        Assert.Equal(UtcMidnight(2026, 7, 6), body.Items[2].WeekStartDate);
        Assert.Null(body.NextCursor);
    }

    [Fact]
    public async Task List_OmitsOtherUsersPlans()
    {
        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);
        await otherClient.PostAsJsonAsync("/meal-plans",
            new CreateMealPlanRequest(UtcMidnight(2026, 7, 20)), TestJson.Options);

        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var body = await (await client.GetAsync("/meal-plans"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task List_WeekStartFilter_ReturnsOnlyThatWeek()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(UtcMidnight(2026, 7, 13)), TestJson.Options);
        await client.PostAsJsonAsync("/meal-plans", new CreateMealPlanRequest(UtcMidnight(2026, 7, 20)), TestJson.Options);

        var body = await (await client.GetAsync("/meal-plans?weekStart=2026-07-20T00:00:00Z"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.NotNull(body);
        var item = Assert.Single(body!.Items);
        Assert.Equal(UtcMidnight(2026, 7, 20), item.WeekStartDate);
    }

    [Fact]
    public async Task List_WeekStartFilter_NoMatch_ReturnsEmpty()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var body = await (await client.GetAsync("/meal-plans?weekStart=2026-01-05T00:00:00Z"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.NotNull(body);
        Assert.Empty(body!.Items);
    }

    [Fact]
    public async Task List_WeekStartNotUtcMidnight_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?weekStart=2026-07-20T03:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_EntryCount_CountsEntries()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var recipeId = await CreateRecipeAsync(client);

        var plan = await (await client.PostAsJsonAsync("/meal-plans",
            new CreateMealPlanRequest(UtcMidnight(2026, 7, 20)), TestJson.Options))
            .Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);

        await client.PostAsJsonAsync($"/meal-plans/{plan!.Id}/entries",
            new AddMealPlanEntryRequest(DayOfWeek.Monday, MealType.Breakfast, recipeId), TestJson.Options);

        var body = await (await client.GetAsync("/meal-plans"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.Equal(1, body!.Items[0].EntryCount);
    }

    [Fact]
    public async Task List_LimitZero_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_MalformedCursor_Returns400()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.GetAsync("/meal-plans?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_PagesByKeyset()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        foreach (var day in new[] { 6, 13, 20 })
        {
            await client.PostAsJsonAsync("/meal-plans",
                new CreateMealPlanRequest(UtcMidnight(2026, 7, day)), TestJson.Options);
        }

        var first = await (await client.GetAsync("/meal-plans?limit=2"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.Equal(2, first!.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await (await client.GetAsync($"/meal-plans?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}"))
            .Content.ReadFromJsonAsync<MealPlanListResponse>(TestJson.Options);

        Assert.Single(second!.Items);
        Assert.Equal(UtcMidnight(2026, 7, 6), second.Items[0].WeekStartDate);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task List_RequiresAuth()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/meal-plans");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Same request shape MealPlanEndpointsTests uses to create the recipe an entry needs.
    private static async Task<Guid> CreateRecipeAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/recipes", new CreateRecipeRequest(
            Title: "Meal Plan List Test Focaccia",
            Description: "A minimal focaccia used to exercise the meal-plan list endpoint.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 4,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Italian",
            CaloriesPerServing: 210,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = "cups" }],
            Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
            Tags: ["bread"]), TestJson.Options);
        response.EnsureSuccessStatusCode();
        var recipe = await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options);
        return recipe!.Id;
    }
}
