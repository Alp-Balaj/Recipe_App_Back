using System.Net;
using System.Net.Http.Json;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.IntegrationTests;

// meal-planning plan, cp04: POST /meal-plans/{id}/generate-shopping-list. Fresh users/recipes
// per test (shared Testcontainers DB), mirroring MealPlanEndpointsTests'/
// ShoppingListEndpointsTests' style.
public class GenerateShoppingListEndpointsTests(IntegrationTestFactory factory) : IClassFixture<IntegrationTestFactory>
{
    [Fact]
    public async Task Generate_TwoRecipesWithKnownIngredients_ReturnsExactRows()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2027, 1, 4));

        var pancakes = await CreateRecipeAsync(client, "Pancakes",
        [
            new RecipeIngredient { Name = "Flour", Quantity = 2.5m, Unit = "cups" },
            new RecipeIngredient { Name = "Eggs", Quantity = 2m, Unit = "count" },
        ]);
        var soup = await CreateRecipeAsync(client, "Soup",
        [
            new RecipeIngredient { Name = "Carrot", Quantity = 3m, Unit = "count" },
        ]);

        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, pancakes.Id);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, soup.Id);

        var response = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);
        Assert.NotNull(items);
        Assert.Equal(3, items!.Count);

        AssertContainsRow(items, "Flour", "2.5 cups", plan.Id);
        AssertContainsRow(items, "Eggs", "2 count", plan.Id);
        AssertContainsRow(items, "Carrot", "3 count", plan.Id);
        Assert.All(items, i => Assert.False(i.IsPurchased));
    }

    [Fact]
    public async Task Generate_RepeatCall_SameCountNoDuplicates()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2027, 1, 11));
        var recipe = await CreateRecipeAsync(client, "Focaccia", [new RecipeIngredient { Name = "Flour", Quantity = 3m, Unit = "cups" }]);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, recipe.Id);

        var first = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var firstItems = await first.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);

        var second = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var secondItems = await second.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(firstItems!);
        Assert.Single(secondItems!);

        // The whole per-user list should still show exactly one row for this ingredient —
        // regeneration replaced rather than appended.
        var list = await client.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        Assert.Single(list!.Items, i => i.MealPlanId == plan.Id);
    }

    [Fact]
    public async Task Generate_ManualItemAndOtherPlansItem_SurviveRegeneration()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var manual = await AddManualItemAsync(client, "Salt", "1 box");

        var otherPlan = await CreateMealPlanAsync(client, UtcMidnight(2027, 1, 18));
        var otherRecipe = await CreateRecipeAsync(client, "Other Plan Recipe", [new RecipeIngredient { Name = "Sugar", Quantity = 1m, Unit = "kg" }]);
        await AddEntryAsync(client, otherPlan.Id, DayOfWeek.Monday, MealType.Breakfast, otherRecipe.Id);
        var otherGenerated = await client.PostAsync($"/meal-plans/{otherPlan.Id}/generate-shopping-list", null);
        var otherItems = await otherGenerated.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);
        var otherItemId = Assert.Single(otherItems!).Id;

        var plan = await CreateMealPlanAsync(client, UtcMidnight(2027, 1, 25));
        var recipe = await CreateRecipeAsync(client, "This Plan Recipe", [new RecipeIngredient { Name = "Butter", Quantity = 1m, Unit = "stick" }]);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Tuesday, MealType.Lunch, recipe.Id);

        await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        // regenerate again to make sure the replace pass doesn't disturb unrelated rows
        await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);

        var list = await client.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        var ids = list!.Items.Select(i => i.Id).ToList();
        Assert.Contains(manual.Id, ids);
        Assert.Contains(otherItemId, ids);
        Assert.Contains(list.Items, i => i.Ingredient == "Butter" && i.MealPlanId == plan.Id);
    }

    [Fact]
    public async Task Generate_SoftDeletedRecipeEntry_IsSkippedOnRegenerate()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2027, 2, 1));
        var keptRecipe = await CreateRecipeAsync(client, "Kept", [new RecipeIngredient { Name = "Rice", Quantity = 1m, Unit = "kg" }]);
        var doomedRecipe = await CreateRecipeAsync(client, "Doomed", [new RecipeIngredient { Name = "Fish", Quantity = 2m, Unit = "fillets" }]);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Lunch, keptRecipe.Id);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Tuesday, MealType.Dinner, doomedRecipe.Id);

        var firstGenerate = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var firstItems = await firstGenerate.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);
        Assert.Equal(2, firstItems!.Count);

        (await client.DeleteAsync($"/recipes/{doomedRecipe.Id}")).EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var items = await response.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(items!);
        Assert.Equal("Rice", item.Ingredient);
    }

    [Fact]
    public async Task Generate_DuplicateRecipeAcrossTwoSlots_IsDeduped()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2027, 2, 8));
        var recipe = await CreateRecipeAsync(client, "Oatmeal", [new RecipeIngredient { Name = "Oats", Quantity = 1m, Unit = "cup" }]);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Breakfast, recipe.Id);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Tuesday, MealType.Breakfast, recipe.Id);

        var response = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var items = await response.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(items!);
        Assert.Equal("Oats", item.Ingredient);
        Assert.Equal("1 cup", item.Quantity);
    }

    [Fact]
    public async Task Generate_CrossUser_Returns404()
    {
        var ownerClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(ownerClient);
        var plan = await CreateMealPlanAsync(ownerClient, UtcMidnight(2027, 2, 15));

        var otherClient = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(otherClient);

        var response = await otherClient.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_UnknownPlan_Returns404()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);

        var response = await client.PostAsync($"/meal-plans/{Guid.NewGuid()}/generate-shopping-list", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_EmptyPlan_Returns200EmptyAndRemovesPriorGeneratedRows()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2027, 2, 22));
        var recipe = await CreateRecipeAsync(client, "Temp", [new RecipeIngredient { Name = "Garlic", Quantity = 1m, Unit = "clove" }]);
        var entry = await AddEntryAsync(client, plan.Id, DayOfWeek.Wednesday, MealType.Dinner, recipe.Id);

        var firstGenerate = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var firstItems = await firstGenerate.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);
        Assert.Single(firstItems!);

        (await client.DeleteAsync($"/meal-plans/{plan.Id}/entries/{entry.Id}")).EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/meal-plans/{plan.Id}/generate-shopping-list", null);
        var items = await response.Content.ReadFromJsonAsync<List<ShoppingListItemResponse>>(TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(items!);

        var list = await client.GetFromJsonAsync<ShoppingListItemListResponse>("/shopping-list", TestJson.Options);
        Assert.DoesNotContain(list!.Items, i => i.MealPlanId == plan.Id);
    }

    // --- helpers ------------------------------------------------------------------------

    private static void AssertContainsRow(List<ShoppingListItemResponse> items, string ingredient, string quantity, Guid mealPlanId)
    {
        var row = Assert.Single(items, i => i.Ingredient == ingredient);
        Assert.Equal(quantity, row.Quantity);
        Assert.Equal(mealPlanId, row.MealPlanId);
    }

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

    private static async Task<ShoppingListItemResponse> AddManualItemAsync(HttpClient client, string ingredient, string quantity)
    {
        var response = await client.PostAsJsonAsync("/shopping-list", new AddShoppingListItemRequest(ingredient, quantity), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShoppingListItemResponse>(TestJson.Options))!;
    }

    private static async Task<RecipeResponse> CreateRecipeAsync(HttpClient client, string title, List<RecipeIngredient> ingredients)
    {
        var request = new CreateRecipeRequest(
            Title: title,
            Description: "A recipe used to exercise the generate-shopping-list endpoint.",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            Servings: 4,
            Difficulty: DifficultyLevel.Easy,
            CuisineType: "Test",
            CaloriesPerServing: 200,
            ImageUrl: null,
            Visibility: RecipeVisibility.Public,
            Ingredients: ingredients,
            Steps: [new RecipeStep { StepNumber = 1, Description = "Combine and cook." }],
            Tags: ["test"]);

        var response = await client.PostAsJsonAsync("/recipes", request, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
    }
}
