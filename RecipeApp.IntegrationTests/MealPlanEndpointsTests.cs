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

    // --- entry recipe summary: time + calories (meal-plan insights) --------------------------
    // Two construction sites build this projection — the week view's Join and the add-entry
    // response — so both are covered. The month view reads them off entries it already
    // fetches, which is the entire point: no GET /recipes/{id} per dish.

    [Fact]
    public async Task GetMealPlan_Entry_CarriesRecipeTimeAndCalories()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 9, 14));
        var recipe = await CreateRecipeAsync(client);
        await AddEntryAsync(client, plan.Id, DayOfWeek.Monday, MealType.Dinner, recipe.Id);

        var response = await client.GetAsync($"/meal-plans/{plan.Id}");
        var body = await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);

        var entry = Assert.Single(body!.Entries);
        // Prep 10 + Cook 20, the same sum MealPlanSummaryResponse.TotalMinutes uses per entry.
        Assert.Equal(30, entry.Recipe.TotalTimeMinutes);
        Assert.Equal(210, entry.Recipe.CaloriesPerServing);
    }

    [Fact]
    public async Task AddEntry_Response_CarriesRecipeTimeAndCalories()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 9, 21));
        var recipe = await CreateRecipeAsync(client);

        var entry = await AddEntryAsync(client, plan.Id, DayOfWeek.Friday, MealType.Lunch, recipe.Id);

        Assert.Equal(30, entry.Recipe.TotalTimeMinutes);
        Assert.Equal(210, entry.Recipe.CaloriesPerServing);
    }

    // Nullable end to end. Defaulting this to 0 would let a client total a month and
    // under-report it silently — the caller is meant to see the hole and carry a denominator.
    [Fact]
    public async Task GetMealPlan_RecipeWithoutCalories_LeavesCaloriesNull()
    {
        var client = factory.CreateClient();
        await AuthTestHelper.RegisterAndAuthenticateAsync(client);
        var plan = await CreateMealPlanAsync(client, UtcMidnight(2026, 9, 28));

        var created = await client.PostAsJsonAsync(
            "/recipes",
            ValidCreateRecipeRequest() with { Title = "Uncounted stew", CaloriesPerServing = null },
            TestJson.Options);
        created.EnsureSuccessStatusCode();
        var recipe = (await created.Content.ReadFromJsonAsync<RecipeResponse>(TestJson.Options))!;
        await AddEntryAsync(client, plan.Id, DayOfWeek.Tuesday, MealType.Dinner, recipe.Id);

        var response = await client.GetAsync($"/meal-plans/{plan.Id}");
        var body = await response.Content.ReadFromJsonAsync<MealPlanResponse>(TestJson.Options);

        var entry = Assert.Single(body!.Entries);
        Assert.Null(entry.Recipe.CaloriesPerServing);
        // Time is never null, so it survives a recipe that has no calorie figure.
        Assert.Equal(30, entry.Recipe.TotalTimeMinutes);
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

    // --- grocery insight (week board) -----------------------------------------------------
    // Week/shopping rework, Task 4: generate-shopping-list is gone (the projection makes
    // regeneration meaningless) and GET /meal-plans/{id}/grocery-insight takes its place as
    // the week board's size/overlap/outlier read.

    // Not a plain 404. Program.cs's app.MapFallbackToFile("index.html") registers a
    // GET/HEAD-only catch-all for every unmatched path (SPA deep-link support); ASP.NET
    // Core's routing sees that catch-all's URL match, then 405s because the requested
    // method (POST) isn't one it accepts — confirmed generic (not specific to this path) by
    // probing an unrelated unmapped POST, which 405s the same way. The task brief expected a
    // 404 here; this asserts the app's real behaviour instead.
    [Fact]
    public async Task Generate_shopping_list_endpoint_is_gone()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var planId = await CreatePlanAsync(client, MealPlanTestHelper.NextMonday());

        var response = await client.PostAsync($"/meal-plans/{planId}/generate-shopping-list", null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Grocery_insight_counts_size_overlap_and_the_outlier()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var weekStart = MealPlanTestHelper.NextMonday();

        // Shared: flour (both). Unique to Tagine: 3 spices. So 5 distinct, 1 shared,
        // and Tagine is the outlier with 3 ingredients nothing else uses.
        var bread = await CreateInsightRecipeAsync(client, "Bread", [("Flour", 500m, UnitOfMeasure.Gram), ("Yeast", 7m, UnitOfMeasure.Gram)]);
        var tagine = await CreateInsightRecipeAsync(client, "Tagine",
            [("Flour", 2m, UnitOfMeasure.Cup), ("Cumin", 1m, UnitOfMeasure.Teaspoon), ("Saffron", 1m, UnitOfMeasure.Pinch), ("Harissa", 2m, UnitOfMeasure.Tablespoon)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddInsightEntryAsync(client, planId, "Monday", "Dinner", bread);
        await AddInsightEntryAsync(client, planId, "Tuesday", "Dinner", tagine);

        var insight = await client.GetFromJsonAsync<GroceryInsightResponse>(
            $"/meal-plans/{planId}/grocery-insight", TestJson.Options);

        Assert.Equal(5, insight!.DistinctIngredientCount);
        Assert.Equal(1, insight.SharedIngredientCount);
        Assert.Equal("Tagine", insight.Outlier!.Title);
        Assert.Equal(3, insight.Outlier.UniqueIngredientCount);
    }

    // The contract's other null case, distinct from an empty plan: every recipe's keys are
    // fully shared, so no recipe has anything "used by no other recipe" to be an outlier.
    [Fact]
    public async Task Grocery_insight_has_no_outlier_when_nothing_is_unique()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var weekStart = MealPlanTestHelper.NextMonday();

        var pasta = await CreateInsightRecipeAsync(client, "Pasta", [("Flour", 2m, UnitOfMeasure.Cup), ("Egg", 3m, UnitOfMeasure.Piece)]);
        var bread = await CreateInsightRecipeAsync(client, "Bread", [("flour", 500m, UnitOfMeasure.Gram), ("egg", 1m, UnitOfMeasure.Piece)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddInsightEntryAsync(client, planId, "Monday", "Dinner", pasta);
        await AddInsightEntryAsync(client, planId, "Tuesday", "Dinner", bread);

        var insight = await client.GetFromJsonAsync<GroceryInsightResponse>(
            $"/meal-plans/{planId}/grocery-insight", TestJson.Options);

        Assert.Equal(2, insight!.DistinctIngredientCount);
        Assert.Equal(2, insight.SharedIngredientCount);
        Assert.Null(insight.Outlier);
    }

    // Ties broken by title, ordinal — "Apple Pie" sorts before "Banana Bread".
    [Fact]
    public async Task Grocery_insight_breaks_an_outlier_tie_by_title_ordinal()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var weekStart = MealPlanTestHelper.NextMonday();

        var apple = await CreateInsightRecipeAsync(client, "Apple Pie", [("Apple", 1m, UnitOfMeasure.Kilogram), ("Cinnamon", 1m, UnitOfMeasure.Teaspoon)]);
        var banana = await CreateInsightRecipeAsync(client, "Banana Bread", [("Banana", 1m, UnitOfMeasure.Kilogram), ("Walnut", 1m, UnitOfMeasure.Cup)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddInsightEntryAsync(client, planId, "Monday", "Dinner", apple);
        await AddInsightEntryAsync(client, planId, "Tuesday", "Dinner", banana);

        var insight = await client.GetFromJsonAsync<GroceryInsightResponse>(
            $"/meal-plans/{planId}/grocery-insight", TestJson.Options);

        Assert.Equal("Apple Pie", insight!.Outlier!.Title);
        Assert.Equal(2, insight.Outlier.UniqueIngredientCount);
    }

    // Collection is per DISTINCT recipe, not per entry: a dish planned twice must not look
    // like it "shares" its ingredients with a second recipe, and must still register as its
    // own outlier.
    [Fact]
    public async Task Grocery_insight_counts_a_repeated_recipe_once()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var weekStart = MealPlanTestHelper.NextMonday();

        var tagine = await CreateInsightRecipeAsync(client, "Tagine", [("Cumin", 1m, UnitOfMeasure.Teaspoon), ("Saffron", 1m, UnitOfMeasure.Pinch)]);
        var planId = await CreatePlanAsync(client, weekStart);
        await AddInsightEntryAsync(client, planId, "Monday", "Dinner", tagine);
        await AddInsightEntryAsync(client, planId, "Thursday", "Dinner", tagine);

        var insight = await client.GetFromJsonAsync<GroceryInsightResponse>(
            $"/meal-plans/{planId}/grocery-insight", TestJson.Options);

        Assert.Equal(2, insight!.DistinctIngredientCount);
        Assert.Equal(0, insight.SharedIngredientCount);
        Assert.Equal("Tagine", insight.Outlier!.Title);
        Assert.Equal(2, insight.Outlier.UniqueIngredientCount);
    }

    [Fact]
    public async Task Grocery_insight_has_no_outlier_for_an_empty_plan()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var planId = await CreatePlanAsync(client, MealPlanTestHelper.NextMonday());

        var insight = await client.GetFromJsonAsync<GroceryInsightResponse>(
            $"/meal-plans/{planId}/grocery-insight", TestJson.Options);

        Assert.Equal(0, insight!.DistinctIngredientCount);
        Assert.Equal(0, insight.SharedIngredientCount);
        Assert.Null(insight.Outlier);
    }

    // 404-never-403 (meal plans have no visibility tier), same rule GetMealPlanByIdAsync uses
    // — not shown in the brief's test list but part of the stated contract.
    [Fact]
    public async Task GroceryInsight_UnknownPlan_Returns404()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync($"/meal-plans/{Guid.NewGuid()}/grocery-insight");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GroceryInsight_OtherUsersPlan_Returns404()
    {
        var ownerClient = await factory.CreateAuthenticatedClientAsync();
        var planId = await CreatePlanAsync(ownerClient, MealPlanTestHelper.NextMonday());

        var otherClient = await factory.CreateAuthenticatedClientAsync();

        var response = await otherClient.GetAsync($"/meal-plans/{planId}/grocery-insight");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- grocery-insight helpers ----------------------------------------------------------
    // Thin adapters over MealPlanTestHelper, mirroring ShoppingListProjectionTests' style.

    private static async Task<Guid> CreatePlanAsync(HttpClient client, DateTime weekStart) =>
        (await MealPlanTestHelper.CreateMealPlanAsync(client, weekStart)).Id;

    private static async Task<Guid> AddInsightEntryAsync(HttpClient client, Guid planId, string day, string meal, Guid recipeId) =>
        (await MealPlanTestHelper.AddEntryAsync(
            client, planId, Enum.Parse<DayOfWeek>(day), Enum.Parse<MealType>(meal), recipeId)).Id;

    private static async Task<Guid> CreateInsightRecipeAsync(
        HttpClient client, string title, (string Name, decimal Qty, UnitOfMeasure Unit)[] ingredients)
    {
        var recipe = await MealPlanTestHelper.CreateRecipeAsync(
            client,
            title,
            [.. ingredients.Select(i => new RecipeIngredient { Name = i.Name, Quantity = i.Qty, Unit = i.Unit })]);
        return recipe.Id;
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
        CuisineType: Cuisine.Italian,
        CaloriesPerServing: 210,
        ImageUrl: null,
        Visibility: visibility,
        Ingredients: [new RecipeIngredient { Name = "flour", Quantity = 3m, Unit = UnitOfMeasure.Cup }],
        Steps: [new RecipeStep { StepNumber = 1, Description = "Mix, rest, bake." }],
        Tags: [RecipeTag.Bread]);
}
